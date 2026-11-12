using WaterMarker.App;
using WaterMarker.Rendering;
using Xunit;

namespace WaterMarker.Stage1.Tests;

public sealed class ViewModelTests
{
    private sealed class Dialogs : IDesktopDialogs
    {
        public string? Target { get; set; }
        public string? Name { get; set; }
        public int SaveCalls { get; private set; }
        public string? OpenImage() => null;
        public string? SaveImage(ImageDocument image) { SaveCalls++; return Target; }
        public string? ProjectName(string? currentName) => Name;
    }

    [Fact]
    public async Task EmptyFieldsAndOtherChannelAreValidatedBeforeSaveDialog()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.File("source.png");
        await StaWorker.Run(() => { ImageTests.SavePng(ImageTests.Synthetic(640, 480), path); return true; });
        var dialogs = new Dialogs { Target = workspace.File("copy.png") };
        var vm = new MainViewModel(workspace.Store(), dialogs);
        Assert.Null(vm.SelectedProject!.Project);
        Assert.False(vm.RenameProjectCommand.CanExecute(null));
        await vm.LoadImageAsync(path);
        await vm.WatermarkCommand.ExecuteAsync();
        Assert.Equal("Enter a recipient.", vm.Status);
        Assert.Equal(0, dialogs.SaveCalls);
        vm.Recipient = "A";
        vm.Channel = "Other";
        await vm.WatermarkCommand.ExecuteAsync();
        Assert.Equal("Enter a channel.", vm.Status);
        Assert.Equal(0, dialogs.SaveCalls);
        vm.OtherChannel = "Private portal";
        await vm.WatermarkCommand.ExecuteAsync();
        Assert.Equal("Copy saved.", vm.Status);
        Assert.Equal("Private portal", Assert.Single(vm.Journal).Channel);
        Assert.True(vm.OpenFileCommand.CanExecute(null));
        Assert.True(vm.OpenFolderCommand.CanExecute(null));
    }

    [Fact]
    public async Task GlobalSearchIgnoresFilterAndErrorsKeepImageAndFields()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.File("source.png");
        await StaWorker.Run(() => { ImageTests.SavePng(ImageTests.Synthetic(640, 480), path); return true; });
        var dialogs = new Dialogs { Target = workspace.File("copy.png"), Name = "First" };
        var vm = new MainViewModel(workspace.Store(), dialogs) { Recipient = "A" };
        vm.NewProjectCommand.Execute(null);
        await vm.LoadImageAsync(path);
        await vm.WatermarkCommand.ExecuteAsync();
        var record = Assert.Single(vm.Journal);
        vm.SelectedFilter = vm.Filters.Single(p => p.Label == "No project");
        Assert.Empty(vm.Journal);
        vm.SearchCode = " " + record.Codes[0].ToLowerInvariant() + " ";
        vm.SearchCommand.Execute(null);
        Assert.Equal(record.Id, Assert.Single(vm.Journal).Id);
        Assert.Contains(record.Codes[0], vm.RecordDetails);
        await vm.WatermarkCommand.ExecuteAsync();
        Assert.Contains("already in use", vm.Status);
        Assert.Equal("A", vm.Recipient);
        Assert.NotNull(vm.Preview);
        Assert.True(vm.WatermarkCommand.CanExecute(null));
        vm.SearchCode = "NOTACODE";
        vm.SearchCommand.Execute(null);
        Assert.Equal("No matching record", vm.SearchStatus);
    }

    [Fact]
    public async Task ExportRunsInBackgroundAndCapturesFieldsBeforeFurtherEdits()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.File("source.png");
        await StaWorker.Run(() => { ImageTests.SavePng(ImageTests.Synthetic(1920, 1080), path); return true; });
        var dialogs = new Dialogs { Target = workspace.File("copy.png"), Name = "Before" };
        var vm = new MainViewModel(workspace.Store(), dialogs) { Recipient = "A" };
        vm.NewProjectCommand.Execute(null);
        await vm.LoadImageAsync(path);
        var task = vm.WatermarkCommand.ExecuteAsync();
        Assert.True(vm.IsBusy);
        Assert.False(vm.WatermarkCommand.CanExecute(null));
        vm.Recipient = "B";
        dialogs.Name = "After";
        vm.RenameProjectCommand.Execute(null);
        await task;
        var record = Assert.Single(vm.Journal);
        Assert.Equal("A", record.Recipient);
        Assert.Equal("Before", record.ProjectName);
        Assert.Equal("After", vm.SelectedProject!.Label);
        Assert.Equal("B", vm.Recipient);
    }
}
