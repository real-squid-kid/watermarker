using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
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
    private ProjectOption? selectedProject;
    private ProjectOption? selectedFilter;
    private ExportRecord? selectedRecord;
    private BitmapSource? preview;
    private string recipient = "", channel = "Telegram", otherChannel = "", searchCode = "", status = "", searchStatus = "";
    private bool busy;
    private int previewVersion;
    private CancellationTokenSource? previewCancellation;
    private string? lastOutput;
    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ProjectOption> Projects { get; } = [];
    public ObservableCollection<ProjectOption> Filters { get; } = [];
    public ObservableCollection<ExportRecord> Journal { get; } = [];
    public IReadOnlyList<string> Channels { get; } = ["Telegram", "Discord", "Email", "Other"];
    public string Recipient { get => recipient; set { recipient = value; Changed(); } }
    public string Channel { get => channel; set { channel = value; Changed(); Changed(nameof(IsOtherChannel)); } }
    public string OtherChannel { get => otherChannel; set { otherChannel = value; Changed(); } }
    public bool IsOtherChannel => Channel == "Other";
    public string SearchCode { get => searchCode; set { searchCode = value; Changed(); } }
    public string Status { get => status; private set { status = value; Changed(); } }
    public string SearchStatus { get => searchStatus; private set { searchStatus = value; Changed(); } }
    public string SourceDisplay => image is null ? "No image selected" : $"{image.SourceName} · {image.Pixels.PixelWidth} × {image.Pixels.PixelHeight}";
    public BitmapSource? Preview { get => preview; private set { preview = value; Changed(); WatermarkCommand?.Refresh(); } }
    public bool IsBusy { get => busy; private set { busy = value; Changed(); RefreshCommands(); } }
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
    public AsyncCommand WatermarkCommand { get; }
    public RelayCommand NewProjectCommand { get; }
    public RelayCommand RenameProjectCommand { get; }
    public RelayCommand SearchCommand { get; }
    public RelayCommand RefreshJournalCommand { get; }
    public RelayCommand OpenFileCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    public MainViewModel(IJournalStore store, IDesktopDialogs dialogs)
    {
        this.store = store;
        this.dialogs = dialogs;
        OpenCommand = new(OpenAsync, ReportError, () => !IsBusy);
        WatermarkCommand = new(ExportAsync, ReportError, () => !IsBusy && image is not null && Preview is not null);
        NewProjectCommand = new(() => Safe(() => EditProject(false)));
        RenameProjectCommand = new(() => Safe(() => EditProject(true)), () => SelectedProject?.Project is not null);
        SearchCommand = new(() => Safe(Search));
        RefreshJournalCommand = new(() => Safe(RefreshJournal));
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
            image = loaded;
            Preview = null;
            Changed(nameof(SourceDisplay));
            await RefreshPreviewAsync();
            if (Preview is not null) Status = "";
        }
        finally { IsBusy = false; }
    }

    private async Task OpenAsync()
    {
        var path = dialogs.OpenImage();
        if (path is not null) await LoadImageAsync(path);
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
        var channelName = IsOtherChannel ? OtherChannel : Channel;
        ExportRequest.ValidateFields(Recipient, channelName);
        var target = dialogs.SaveImage(source);
        if (target is null) return;
        var extension = Path.GetExtension(target).ToLowerInvariant();
        var jpeg = extension is ".jpg" or ".jpeg";
        if (extension != ".png" && !jpeg) throw new ArgumentException("Choose a PNG or JPEG output file.");
        if (source.SourceType == "PNG" && jpeg) throw new ArgumentException("PNG images must be saved as PNG to preserve transparency.");
        // Immutable snapshot: editing project or fields after this point affects the next export only.
        var request = new ExportRequest(SelectedProject?.Project, Recipient, channelName, target).Validate();
        IsBusy = true;
        Status = "Rendering…";
        try
        {
            var record = await StaWorker.Run(() =>
            {
                var layout = WatermarkRenderer.Calculate(source.Pixels.PixelWidth, source.Pixels.PixelHeight, request.Project?.Name);
                return new ExportService(store).Export(request, new PreparedImage(source, layout, jpeg));
            });
            lastOutput = record.TargetPath;
            RefreshJournal();
            SelectedRecord = record;
            Status = "Copy saved.";
        }
        finally { IsBusy = false; }
    }

    private void RefreshJournal()
    {
        if (SelectedFilter is null) return;
        var records = store.List(new(SelectedFilter.AllProjects, SelectedFilter.Project?.Id));
        Journal.Clear();
        foreach (var record in records) Journal.Add(record);
        SelectedRecord = null;
        SearchStatus = "";
    }

    private void Search()
    {
        var record = store.Find(SearchCode);
        Journal.Clear();
        if (record is not null) Journal.Add(record);
        SelectedRecord = record;
        SearchStatus = record is null ? "No matching record" : record.State == ExportState.Ready ? "" : "Incomplete save";
    }

    private void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex) { ReportError(ex); }
    }
    private void ReportError(Exception ex) => Status = ex switch
    {
        ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException => ex.Message,
        _ => "The operation could not be completed. Check the file and available disk space."
    };
    private void RefreshCommands()
    {
        OpenCommand?.Refresh();
        WatermarkCommand?.Refresh();
        OpenFileCommand?.Refresh();
        OpenFolderCommand?.Refresh();
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
