using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using WaterMarker.App.Visuals;
using WaterMarker.Core;
using WaterMarker.Infrastructure;
using WaterMarker.Rendering;

namespace WaterMarker.App;

public sealed record ProjectOption(string Label, ProjectInfo? Project = null, bool AllProjects = false)
{
    public override string ToString() => Label;
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IJournalStore store;
    private readonly IDesktopDialogs dialogs;
    private ImageDocument? image;
    private VideoInfo? video;
    private CancellationTokenSource? seekCancellation;
    private int seekVersion;
    private double videoPosition;
    private double confirmedVideoPosition;
    private bool seeking;
    public Task VideoSeekExecution { get; private set; } = Task.CompletedTask;
    private readonly VideoTools videoTools = new();
    private ProjectOption? selectedProject;
    private ProjectOption? selectedFilter;
    private ExportRecord? selectedRecord;
    private BitmapSource? preview;
    private string recipient = "", channel = "Telegram", otherChannel = "", searchCode = "", status = "", searchStatus = "";
    private bool busy;
    private int previewVersion;
    private CancellationTokenSource? previewCancellation;
    private string? lastOutput;
    private ExportControl? exportControl;
    private CancellationTokenSource? searchCancellation;
    private int searchVersion;
    public Task JournalSearchExecution { get; private set; } = Task.CompletedTask;
    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ProjectOption> Projects { get; } = [];
    public ObservableCollection<ProjectOption> Filters { get; } = [];
    public ObservableCollection<ExportRecord> Journal { get; } = [];
    public IReadOnlyList<string> Channels { get; } = ["Telegram", "Discord", "Email", "Other"];
    public string Recipient { get => recipient; set { recipient = value; Changed(); } }
    public string Channel { get => channel; set { channel = value; Changed(); Changed(nameof(IsOtherChannel)); } }
    public string OtherChannel { get => otherChannel; set { otherChannel = value; Changed(); } }
    public bool IsOtherChannel => Channel == "Other";
    public string SearchCode
    {
        get => searchCode;
        set
        {
            if (searchCode == value) return;
            searchCode = value;
            Changed();
            JournalSearchExecution = SearchLiveAsync();
        }
    }
    public string Status { get => status; private set { status = value; Changed(); } }
    public string SearchStatus { get => searchStatus; private set { searchStatus = value; Changed(); } }
    public string SourceDisplay => image is null ? "No media selected" : $"{image.SourceName} · {image.Pixels.PixelWidth} × {image.Pixels.PixelHeight}" +
        (video is null ? "" : $" · {VideoProgressViewModel.Format(video.Duration)}");
    public double TelevisionAspectRatio => image is null ? TelevisionFormats.Default : TelevisionFormats.Select(image.Pixels.PixelWidth, image.Pixels.PixelHeight);
    public bool HasVideo => video is not null;
    public double VideoDurationSeconds => video?.Duration.TotalSeconds ?? 0;
    public double VideoMaxPosition => Math.Max(0, VideoDurationSeconds - .001);
    public string VideoPositionText => $"{VideoProgressViewModel.Format(TimeSpan.FromSeconds(VideoPosition))} / {VideoProgressViewModel.Format(TimeSpan.FromSeconds(VideoDurationSeconds))}";
    public double VideoPosition
    {
        get => videoPosition;
        set
        {
            var position = Math.Clamp(value, 0, VideoMaxPosition);
            if (Math.Abs(videoPosition - position) < .001) return;
            videoPosition = position;
            Changed(); Changed(nameof(VideoPositionText));
            if (video is not null) VideoSeekExecution = SeekVideoAsync(video, position);
        }
    }
    public BitmapSource? Preview { get => preview; private set { preview = value; Changed(); WatermarkCommand?.Refresh(); } }
    public bool IsBusy { get => busy; private set { busy = value; Changed(); RefreshCommands(); } }
    public bool IsExporting => exportControl is not null;
    public bool CanCancel => IsBusy && exportControl?.CanCancel == true;
    public ProjectOption? SelectedProject
    {
        get => selectedProject;
        set { selectedProject = value; Changed(); RenameProjectCommand?.Refresh(); _ = RefreshPreviewAsync(); }
    }
    public ProjectOption? SelectedFilter
    {
        get => selectedFilter;
        set { selectedFilter = value; Changed(); if (value is not null) Safe(RefreshJournal); }
    }
    public ExportRecord? SelectedRecord
    {
        get => selectedRecord;
        set { selectedRecord = value; Changed(); Changed(nameof(RecordDetails)); }
    }
    public string RecordDetails => SelectedRecord is null ? "" :
        $"Project: {SelectedRecord.ProjectDisplay}\nRecipient: {SelectedRecord.Recipient}\nChannel: {SelectedRecord.Channel}\n" +
        $"Exported at: {SelectedRecord.ExportedAtDisplay}\nState: {SelectedRecord.State}\nSource: {SelectedRecord.SourceName}\n" +
        $"Copy: {SelectedRecord.TargetPath}\nCodes ({SelectedRecord.Codes.Count}):\n{string.Join("  ", SelectedRecord.Codes)}";

    public AsyncCommand OpenCommand { get; }
    public AsyncCommand CaptureCommand { get; }
    public AsyncCommand WatermarkCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand NewProjectCommand { get; }
    public RelayCommand RenameProjectCommand { get; }
    public RelayCommand SearchCommand { get; }
    public RelayCommand RefreshJournalCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand OpenFileCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    public MainViewModel(IJournalStore store, IDesktopDialogs dialogs)
    {
        this.store = store;
        this.dialogs = dialogs;
        OpenCommand = new(OpenAsync, ReportError, () => !IsBusy);
        CaptureCommand = new(CaptureAsync, ReportError, () => !IsBusy);
        WatermarkCommand = new(ExportAsync, ReportError, () => !IsBusy && !seeking && image is not null && Preview is not null);
        CancelCommand = new(CancelExport, () => CanCancel);
        NewProjectCommand = new(() => Safe(() => EditProject(false)));
        RenameProjectCommand = new(() => Safe(() => EditProject(true)), () => SelectedProject?.Project is not null);
        SearchCommand = new(() => Safe(Search));
        RefreshJournalCommand = new(() => Safe(RefreshJournal));
        ClearSearchCommand = new(() => Safe(ShowJournal));
        OpenFileCommand = new(() => Safe(() => ShellActions.OpenFile(lastOutput!)), () => lastOutput is not null);
        OpenFolderCommand = new(() => Safe(() => ShellActions.OpenFolder(lastOutput!)), () => lastOutput is not null);
        ReloadProjects(null);
    }

    private void ReloadProjects(Guid? selectId)
    {
        var filter = SelectedFilter;
        Projects.Clear();
        Filters.Clear();
        Projects.Add(new("No project"));
        Filters.Add(new("All projects", AllProjects: true));
        Filters.Add(new("No project"));
        foreach (var project in store.GetProjects())
        {
            Projects.Add(new(project.Name, project));
            Filters.Add(new(project.Name, project));
        }
        SelectedProject = Projects.FirstOrDefault(p => p.Project?.Id == selectId) ?? Projects[0];
        SelectedFilter = filter is null || filter.AllProjects ? Filters[0]
            : Filters.FirstOrDefault(p => !p.AllProjects && p.Project?.Id == filter.Project?.Id) ?? Filters[0];
    }

    private void EditProject(bool rename)
    {
        var current = rename ? SelectedProject?.Project : null;
        var name = dialogs.ProjectName(current?.Name);
        if (name is null) return;
        var project = current is null ? store.CreateProject(name) : store.RenameProject(current.Id, name);
        ReloadProjects(project.Id);
        if (!IsBusy) Status = "";
    }

    public async Task LoadImageAsync(string path)
    {
        IsBusy = true;
        Status = "Opening image…";
        try
        {
            var loaded = await StaWorker.Run(() => ImageDocument.Load(path));
            CancelVideoSeek();
            image = loaded;
            video = null;
            ResetVideoPosition();
            Preview = null;
            Changed(nameof(SourceDisplay));
            Changed(nameof(TelevisionAspectRatio));
            await RefreshPreviewAsync();
            if (Preview is not null) Status = "";
        }
        finally { IsBusy = false; }
    }

    public async Task LoadMediaAsync(string path)
    {
        if (!new[] { ".mp4", ".mov", ".mkv" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            await LoadImageAsync(path);
            return;
        }
        IsBusy = true;
        Status = "Reading video duration…";
        try
        {
            await videoTools.EnsureAvailableAsync();
            var loaded = await videoTools.ProbeAsync(path);
            Status = "Checking audio and containers…";
            loaded = await videoTools.CheckContainersAsync(loaded);
            var bytes = await videoTools.PreviewAsync(loaded);
            var frame = await StaWorker.Run(() =>
            {
                using var buffer = new MemoryStream(bytes);
                var decoder = BitmapDecoder.Create(buffer, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var pixels = decoder.Frames[0];
                pixels.Freeze();
                return new ImageDocument(loaded.SourcePath, "Video", pixels);
            });
            CancelVideoSeek();
            image = frame;
            video = loaded;
            ResetVideoPosition();
            Changed(nameof(SourceDisplay));
            Changed(nameof(TelevisionAspectRatio));
            await RefreshPreviewAsync();
            if (Preview is not null) Status = loaded.RequiresMkv ? "This video's audio will be saved in MKV." : "";
        }
        finally { IsBusy = false; }
    }

    private async Task OpenAsync()
    {
        var path = dialogs.OpenImage();
        if (path is null) return;
        try { await LoadMediaAsync(path); }
        catch (IOException ex) { throw new ArgumentException("The media could not be opened. Check its location and format.", ex); }
    }

    private async Task CaptureAsync()
    {
        IsBusy = true;
        Status = "Capturing…";
        try
        {
            var captured = await dialogs.CaptureImageAsync();
            if (captured is null) { Status = ""; return; }
            image = captured;
            CancelVideoSeek();
            video = null;
            ResetVideoPosition();
            Changed(nameof(SourceDisplay));
            Changed(nameof(TelevisionAspectRatio));
            await RefreshPreviewAsync();
            if (Preview is not null) Status = "";
        }
        catch (IOException) { Status = "Screen capture failed. Try again."; }
        finally { IsBusy = false; }
    }

    public void ShowRecoveryResult(RecoveryResult result)
    {
        Status = result.Pending > 0 ? "Some copies could not be recovered. Close files in use and restart WaterMarker."
            : result.Completed > 0 ? $"Recovered {result.Completed} copies."
            : result.Interrupted > 0 ? "Interrupted operations were cleaned up." : "";
    }

    public static bool CanDropFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count != 1) return false;
        try
        {
            var extension = Path.GetExtension(paths[0]);
            return File.Exists(paths[0]) && new[] { ".png", ".jpg", ".jpeg", ".mp4", ".mov", ".mkv" }.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    public async Task OpenDroppedFilesAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy) return;
        if (!CanDropFiles(paths)) { Status = "Drop one PNG, JPEG, MP4, MOV or MKV file."; return; }
        try { await LoadMediaAsync(paths[0]); }
        catch (Exception ex) { ReportError(ex); }
    }

    private void CancelExport()
    {
        if (exportControl?.TryCancel() == true) Status = "Cancelling…";
        RefreshCancellation();
    }

    private void RefreshCancellation()
    {
        Changed(nameof(IsExporting));
        Changed(nameof(CanCancel));
        CancelCommand.Refresh();
    }

    public async Task StopForCloseAsync()
    {
        CancelExport();
        await WatermarkCommand.Execution;
        ReleasePreview();
    }

    public void ReleasePreview()
    {
        CancelVideoSeek();
        CancelPendingSearch();
        ++previewVersion;
        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
        previewCancellation = null;
    }

    private void ResetVideoPosition()
    {
        videoPosition = confirmedVideoPosition = 0;
        Changed(nameof(HasVideo)); Changed(nameof(VideoDurationSeconds)); Changed(nameof(VideoMaxPosition));
        Changed(nameof(VideoPosition)); Changed(nameof(VideoPositionText));
    }

    private void CancelVideoSeek()
    {
        var wasSeeking = seeking;
        ++seekVersion;
        seekCancellation?.Cancel();
        seekCancellation = null;
        seeking = false;
        if (wasSeeking) WatermarkCommand?.Refresh();
    }

    private async Task SeekVideoAsync(VideoInfo source, double position)
    {
        CancelVideoSeek();
        var version = seekVersion;
        using var cancellation = new CancellationTokenSource();
        seekCancellation = cancellation;
        seeking = true;
        WatermarkCommand.Refresh();
        try
        {
            await Task.Delay(200, cancellation.Token);
            var bytes = await videoTools.PreviewAsync(source, cancellation.Token, TimeSpan.FromSeconds(position));
            var frame = await StaWorker.Run(() =>
            {
                using var buffer = new MemoryStream(bytes);
                var decoder = BitmapDecoder.Create(buffer, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var pixels = decoder.Frames[0];
                pixels.Freeze();
                return new ImageDocument(source.SourcePath, "Video", pixels);
            });
            if (version != seekVersion || !ReferenceEquals(video, source)) return;
            image = frame;
            confirmedVideoPosition = position;
            await RefreshPreviewAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version == seekVersion)
            {
                videoPosition = confirmedVideoPosition;
                Changed(nameof(VideoPosition)); Changed(nameof(VideoPositionText));
                ReportError(ex);
            }
        }
        finally
        {
            if (version == seekVersion)
            {
                seekCancellation = null;
                seeking = false;
                WatermarkCommand.Refresh();
            }
        }
    }

    private async Task RefreshPreviewAsync()
    {
        var version = ++previewVersion;
        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
        previewCancellation = new CancellationTokenSource();
        var token = previewCancellation.Token;
        var source = image;
        var projectName = SelectedProject?.Project?.Name;
        if (source is null) return;
        Preview = null;
        try
        {
            var result = await StaWorker.Run(() =>
            {
                var layout = WatermarkRenderer.Calculate(source.Pixels.PixelWidth, source.Pixels.PixelHeight, projectName);
                token.ThrowIfCancellationRequested();
                return WatermarkRenderer.Compose(source.Pixels, WatermarkRenderer.RenderLayer(layout), token);
            });
            if (version == previewVersion) Preview = result;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == previewVersion) { Preview = null; ReportError(ex); } }
    }

    private async Task ExportAsync()
    {
        var source = image!;
        var sourceVideo = video;
        var channelName = IsOtherChannel ? OtherChannel : Channel;
        ExportRequest.ValidateFields(Recipient, channelName);
        var target = sourceVideo is null ? dialogs.SaveImage(source) : dialogs.SaveVideo(sourceVideo);
        if (target is null) return;
        var extension = Path.GetExtension(target).ToLowerInvariant();
        var jpeg = extension is ".jpg" or ".jpeg";
        if (sourceVideo is null)
        {
            if (extension != ".png" && !jpeg) throw new ArgumentException("Choose a PNG or JPEG output file.");
            if (source.SourceType != "JPEG" && jpeg) throw new ArgumentException("Screenshots and PNG images must be saved as PNG to preserve transparency.");
        }
        else
        {
            if (extension is not (".mp4" or ".mkv")) throw new ArgumentException("Choose an MP4 or MKV output file.");
            if (sourceVideo.RequiresMkv && extension != ".mkv") throw new ArgumentException("Choose MKV to preserve this video's original audio tracks.");
            if (extension == ".mkv" && !sourceVideo.SupportsMkv) throw new ArgumentException("Choose MP4 to preserve this video's original audio tracks.");
        }
        // Immutable snapshot: editing project or fields after this point affects the next export only.
        var request = new ExportRequest(SelectedProject?.Project, Recipient, channelName, target).Validate();
        using var control = new ExportControl();
        exportControl = control;
        IsBusy = true;
        RefreshCancellation();
        Status = "Rendering…";
        var videoProgress = sourceVideo is null ? null : new VideoProgressViewModel(sourceVideo.Duration, CancelCommand);
        var progress = new Progress<ExportCheckpoint>(phase =>
        {
            if (!ReferenceEquals(exportControl, control)) return;
            if (phase >= ExportCheckpoint.PublicationStarting) { Status = "Publishing…"; videoProgress?.Publishing(); }
            RefreshCancellation();
        });
        try
        {
            ExportRecord? record = null;
            var encodingProgress = new Progress<VideoProgress>(value =>
            {
                if (ReferenceEquals(exportControl, control)) videoProgress?.Update(value);
            });
            async Task RenderAsync()
            {
                record = await StaWorker.Run(() =>
                {
                    var layout = WatermarkRenderer.Calculate(source.Pixels.PixelWidth, source.Pixels.PixelHeight, request.Project?.Name);
                    IExportMedia media = sourceVideo is null ? new PreparedImage(source, layout, jpeg)
                        : new PreparedVideo(sourceVideo, layout, extension == ".mkv", videoTools, encodingProgress);
                    return new ExportService(store, (phase, _) => ((IProgress<ExportCheckpoint>)progress).Report(phase))
                        .Export(request, media, control: control);
                });
                videoProgress?.Complete();
            }
            if (videoProgress is null) await RenderAsync();
            else await dialogs.RunVideoExportAsync(videoProgress, RenderAsync);
            if (record is null) throw new InvalidOperationException("The export did not complete.");
            lastOutput = record.TargetPath;
            RefreshJournal();
            SelectedRecord = Journal.FirstOrDefault(item => item.Id == record.Id);
            Status = "Copy saved.";
        }
        catch (OperationCanceledException) { Status = "Cancelled."; }
        catch (IOException ex)
        {
            var message = File.Exists(target) || Directory.Exists(target)
                ? "This name is already in use. Choose another file name."
                : "The copy could not be saved. Check the folder, access rights and available disk space.";
            throw new ArgumentException(message, ex);
        }
        finally
        {
            exportControl = null;
            IsBusy = false;
            RefreshCancellation();
        }
    }

    private void RefreshJournal()
    {
        CancelPendingSearch();
        if (!string.IsNullOrWhiteSpace(SearchCode)) { Search(); return; }
        if (SelectedFilter is null) return;
        var records = store.List(new(SelectedFilter.AllProjects, SelectedFilter.Project?.Id));
        Journal.Clear();
        foreach (var record in records) Journal.Add(record);
        SelectedRecord = null;
        SearchStatus = "";
    }

    private void Search()
    {
        CancelPendingSearch();
        if (string.IsNullOrWhiteSpace(SearchCode)) { RefreshJournal(); return; }
        ApplySearchResults(store.SearchCodes(SearchCode));
    }

    private void ShowJournal()
    {
        CancelPendingSearch();
        searchCode = "";
        Changed(nameof(SearchCode));
        RefreshJournal();
    }

    private void CancelPendingSearch()
    {
        ++searchVersion;
        searchCancellation?.Cancel();
        searchCancellation = null;
    }

    private async Task SearchLiveAsync()
    {
        CancelPendingSearch();
        var version = searchVersion;
        var fragment = SearchCode;
        using var cancellation = new CancellationTokenSource();
        searchCancellation = cancellation;
        Journal.Clear();
        SelectedRecord = null;
        SearchStatus = "Searching…";
        try
        {
            // Keep typing responsive; older database reads can finish but cannot replace newer results.
            if (!string.IsNullOrWhiteSpace(fragment)) await Task.Delay(200, cancellation.Token);
            var filter = SelectedFilter;
            var records = await Task.Run(() => string.IsNullOrWhiteSpace(fragment)
                ? filter is null ? [] : store.List(new(filter.AllProjects, filter.Project?.Id))
                : store.SearchCodes(fragment), cancellation.Token);
            if (version != searchVersion) return;
            if (string.IsNullOrWhiteSpace(fragment))
            {
                foreach (var record in records) Journal.Add(record);
                SearchStatus = "";
            }
            else ApplySearchResults(records);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == searchVersion) { SearchStatus = "Search failed. Try again."; ReportError(ex); } }
        finally { if (ReferenceEquals(searchCancellation, cancellation)) searchCancellation = null; }
    }

    private void ApplySearchResults(IReadOnlyList<ExportRecord> records)
    {
        Journal.Clear();
        foreach (var record in records) Journal.Add(record);
        SelectedRecord = records.Count == 1 ? records[0] : null;
        SearchStatus = records.Count == 0 ? "No matching record" : records.Count == 1
            ? records[0].State == ExportState.Ready ? "" : "Incomplete save"
            : $"{records.Count} matching records" + (records.Any(r => r.State != ExportState.Ready) ? " (includes incomplete saves)" : "");
    }

    private void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex) { ReportError(ex); }
    }
    private void ReportError(Exception ex) => Status = ex switch
    {
        ArgumentException input => input.ParamName is null ? input.Message : "Check the file name and input fields.",
        UnauthorizedAccessException => "Access denied. Choose a file or folder you can access.",
        IOException => "The file could not be opened. Check its location and access rights.",
        NotSupportedException => ex.Message,
        _ => "The operation could not be completed. Check the file and available disk space."
    };
    private void RefreshCommands()
    {
        OpenCommand?.Refresh();
        CaptureCommand?.Refresh();
        WatermarkCommand?.Refresh();
        OpenFileCommand?.Refresh();
        OpenFolderCommand?.Refresh();
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
