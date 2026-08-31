using DropShelf.Core;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace DropShelf.App.Tests;

public sealed class NativeShellHostTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MultiOpenPrevalidatesEntireSelectionBeforeDispatch()
    {
        FakeShell shell = new();
        FakeFileSystem files = new([Path.GetFullPath("first.txt")]);
        NativeShelfItemActions actions = new(shell, files);
        ShelfItem first = ShelfItem.Create(Guid.NewGuid(), "first", FileReferencePayload.Create(Path.GetFullPath("first.txt")), Now);
        ShelfItem missing = ShelfItem.Create(Guid.NewGuid(), "missing", FileReferencePayload.Create(Path.GetFullPath("missing.txt")), Now, ordinal: 1);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => actions.OpenAsync([first, missing]));

        Assert.Empty(shell.Opened);
    }

    [Fact]
    public async Task MultiOpenPrevalidatesMissingFileUrlBeforeDispatch()
    {
        string existingPath = Path.GetFullPath("first.txt");
        string missingPath = Path.GetFullPath("missing.txt");
        FakeShell shell = new();
        NativeShelfItemActions actions = new(shell, new FakeFileSystem([existingPath]));
        ShelfItem first = ShelfItem.Create(Guid.NewGuid(), "first", FileReferencePayload.Create(existingPath), Now);
        ShelfItem missingUrl = ShelfItem.Create(
            Guid.NewGuid(), "missing", UrlPayload.Create(new Uri(missingPath).AbsoluteUri), Now, ordinal: 1);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => actions.OpenAsync([first, missingUrl]));

        Assert.Empty(shell.Opened);
    }

    [Fact]
    public async Task MultiOpenAttemptsEveryPrevalidatedItemAndAggregatesFailure()
    {
        FakeShell shell = new() { OpenResults = new Queue<NativeShellStatus>([NativeShellStatus.Failed, NativeShellStatus.Success]) };
        string firstPath = Path.GetFullPath("first.txt");
        string secondPath = Path.GetFullPath("second.txt");
        NativeShelfItemActions actions = new(shell, new FakeFileSystem([firstPath, secondPath]));
        ShelfItem first = ShelfItem.Create(Guid.NewGuid(), "first", FileReferencePayload.Create(firstPath), Now);
        ShelfItem second = ShelfItem.Create(Guid.NewGuid(), "second", FileReferencePayload.Create(secondPath), Now, ordinal: 1);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => actions.OpenAsync([first, second]));

        Assert.Equal([firstPath, secondPath], shell.Opened);
        Assert.DoesNotContain(firstPath, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secondPath, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultiRevealUsesTheSameAtomicValidationAndAggregateDispatchPolicy()
    {
        string firstPath = Path.GetFullPath("first.txt");
        string secondPath = Path.GetFullPath("second.txt");
        ShelfItem first = ShelfItem.Create(Guid.NewGuid(), "first", FileReferencePayload.Create(firstPath), Now);
        ShelfItem second = ShelfItem.Create(Guid.NewGuid(), "second", FileReferencePayload.Create(secondPath), Now, ordinal: 1);
        FakeShell invalidShell = new();

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new NativeShelfItemActions(invalidShell, new FakeFileSystem([firstPath])).RevealAsync([first, second]));
        Assert.Empty(invalidShell.Revealed);

        FakeShell dispatchShell = new() { RevealResults = new Queue<NativeShellStatus>([NativeShellStatus.Failed, NativeShellStatus.Success]) };
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new NativeShelfItemActions(dispatchShell, new FakeFileSystem([firstPath, secondPath])).RevealAsync([first, second]));
        Assert.Equal([firstPath, secondPath], dispatchShell.Revealed);
    }

    [Fact]
    public async Task NativeFailureIsVisibleAndDoesNotMutateOrDisableShelf()
    {
        FakeShell shell = new() { OpenResult = NativeShellStatus.TargetMissing };
        ShelfItem item = ShelfItem.Create(Guid.NewGuid(), "private.txt",
            FileReferencePayload.Create(Path.GetFullPath("private.txt")), Now);
        ShelfSession session = new([item]);
        ShelfViewModel viewModel = new(session, Now, new NativeShelfItemActions(shell));
        viewModel.Select(item.Id);

        await viewModel.OpenSelectedAsync();

        Assert.Equal("Could not open the selected item. Try again.", viewModel.Announcement);
        _ = Assert.Single(session.Items);
        Assert.False(viewModel.IsActionInProgress);
        Assert.DoesNotContain(((FileReferencePayload)item.Payload).Path, viewModel.Announcement, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HostOperatingSystem.Windows, 1, 0)]
    [InlineData(HostOperatingSystem.MacOS, 0, 1)]
    [InlineData(HostOperatingSystem.Unsupported, 0, 0)]
    public void HostSelectionIsDeterministicAndHasNoNativeSideEffects(HostOperatingSystem operatingSystem, int windowsCalls, int macCalls)
    {
        int createdWindows = 0;
        int createdMac = 0;

        INativeShell shell = App.CreateNativeShellForHost(operatingSystem,
            () => { createdWindows++; return new FakeShell(); },
            () => { createdMac++; return new FakeShell(); });

        Assert.Equal(windowsCalls, createdWindows);
        Assert.Equal(macCalls, createdMac);
        Assert.Equal(operatingSystem == HostOperatingSystem.Unsupported, shell is UnavailableNativeShell);
    }

    [AvaloniaFact]
    public async Task SettingsControlsApplyAndPersistASelectedShortcut()
    {
        FakeShell shell = new();
        RecordingSettingsStore store = new(AppSettings.Default);
        MainWindow window = new();
        window.ConfigureNativeSettingsForHost(store, shell, AppSettings.Default);
        ComboBox picker = window.FindControl<ComboBox>("ShortcutPicker")!;
        picker.SelectedItem = "Ctrl+Shift+Space";

        window.FindControl<Button>("ApplyShortcutButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(() => store.Saved is not null);

        Assert.Equal("Ctrl+Shift+Space", shell.LastShortcut);
        Assert.Equal("Ctrl+Shift+Space", store.Saved!.GlobalShortcut);
        window.Close();
    }

    [AvaloniaFact]
    public async Task FailedLoginToggleRestoresControlAndDoesNotPersist()
    {
        FakeShell shell = new() { LaunchResult = NativeShellStatus.Failed };
        RecordingSettingsStore store = new(AppSettings.Default);
        MainWindow window = new();
        window.ConfigureNativeSettingsForHost(store, shell, AppSettings.Default);
        CheckBox toggle = window.FindControl<CheckBox>("LaunchAtLoginToggle")!;

        toggle.IsChecked = true;
        await WaitForAsync(() => shell.LastLaunchAtLogin is not null);

        Assert.False(toggle.IsChecked);
        Assert.Null(store.Saved);
        Assert.Equal("Could not change launch at login. Your previous setting was kept.", window.FindControl<TextBlock>("DropStatus")!.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void ShortcutConflictIsReportedWithoutRemovingWindowAccess()
    {
        FakeShell shell = new() { ShortcutResult = NativeShellStatus.Conflict };
        MainWindow window = new();

        NativeShellStatus result = App.ConfigureShortcutForHost(window, shell, "Ctrl+Shift+Space");

        Assert.Equal(NativeShellStatus.Conflict, result);
        Assert.Equal("That shortcut is already in use. Use the tray or menu to open the shelf.",
            window.FindControl<TextBlock>("DropStatus")?.Text);
        Assert.True(window.IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public void CarbonShortcutBackendDoesNotPromptForBroadAccessibilityPermission()
    {
        MainWindow window = new();

        Assert.Null(window.FindControl<TextBlock>("PermissionPurpose"));
        Assert.Null(window.FindControl<Button>("OpenPermissionSettingsButton"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task StartupDoesNotMutateLoginRegistrationWithoutUserAction()
    {
        FakeShell shell = new();
        MainWindow window = new();

        await App.LoadAndApplyStartupSettingsForHostAsync(
            window, new FixedSettingsStore(AppSettings.Create(startAtLogin: true)), shell);

        Assert.Null(shell.LastLaunchAtLogin);
        Assert.Equal("Shelf ready.", window.FindControl<TextBlock>("DropStatus")?.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task StartupShortcutConflictRemainsVisibleAfterLoadingCompletes()
    {
        FakeShell shell = new() { ShortcutResult = NativeShellStatus.Conflict };
        MainWindow window = new();

        await App.LoadAndApplyStartupSettingsForHostAsync(
            window, new FixedSettingsStore(AppSettings.Default), shell);

        Assert.Equal(string.Empty, window.FindControl<TextBlock>("StateMessage")?.Text);
        Assert.Equal("That shortcut is already in use. Use the tray or menu to open the shelf.",
            window.FindControl<TextBlock>("DropStatus")?.Text);
        Assert.True(window.IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task RetryInitializationRebindsNativeControlsAndShortcut()
    {
        FakeShell shell = new();
        AppSettings settings = AppSettings.Create(globalShortcut: "Ctrl+Shift+D");
        FixedSettingsStore store = new(settings);
        MainWindow window = new();

        await App.CreateRetryShelfLoadForHost(window, store, shell)();

        Assert.Equal("Ctrl+Shift+D", shell.LastShortcut);
        Assert.Equal("Ctrl+Shift+D", window.FindControl<ComboBox>("ShortcutPicker")?.SelectedItem);
        window.Close();
    }

    [AvaloniaFact]
    public async Task NativeSettingsWritesAreSerializedAndControlsRecover()
    {
        FakeShell shell = new();
        DeferredSettingsStore store = new();
        MainWindow window = new();
        window.ConfigureNativeSettingsForHost(store, shell, AppSettings.Default);
        window.FindControl<ComboBox>("ShortcutPicker")!.SelectedItem = "Ctrl+Shift+Space";

        window.FindControl<Button>("ApplyShortcutButton")!.RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(() => store.SaveCalls == 1);
        window.FindControl<CheckBox>("LaunchAtLoginToggle")!.IsChecked = true;
        await Task.Delay(30);

        Assert.Null(shell.LastLaunchAtLogin);
        Assert.False(window.FindControl<Button>("ApplyShortcutButton")!.IsEnabled);
        store.Complete();
        await WaitForAsync(() => window.FindControl<Button>("ApplyShortcutButton")!.IsEnabled);
        Assert.False(window.FindControl<CheckBox>("LaunchAtLoginToggle")!.IsChecked);
        window.Close();
    }

    [AvaloniaFact]
    public async Task FailedShortcutRollbackReportsUncertainNativeState()
    {
        FakeShell shell = new()
        {
            ShortcutResults = new Queue<NativeShellStatus>([NativeShellStatus.Success, NativeShellStatus.Failed]),
        };
        MainWindow window = new();
        window.ConfigureNativeSettingsForHost(new ThrowingSettingsStore(), shell, AppSettings.Default);
        window.FindControl<ComboBox>("ShortcutPicker")!.SelectedItem = "Ctrl+Shift+Space";

        window.FindControl<Button>("ApplyShortcutButton")!.RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(() => window.FindControl<Button>("ApplyShortcutButton")!.IsEnabled);

        Assert.Contains("could not be restored", window.FindControl<TextBlock>("DropStatus")!.Text,
            StringComparison.OrdinalIgnoreCase);
        window.Close();
    }

    [AvaloniaFact]
    public async Task FailedLaunchAtLoginRollbackReportsUncertainNativeState()
    {
        FakeShell shell = new()
        {
            LaunchResults = new Queue<NativeShellStatus>([NativeShellStatus.Success, NativeShellStatus.Failed]),
        };
        MainWindow window = new();
        window.ConfigureNativeSettingsForHost(new ThrowingSettingsStore(), shell, AppSettings.Default);

        window.FindControl<CheckBox>("LaunchAtLoginToggle")!.IsChecked = true;
        await WaitForAsync(() => window.FindControl<CheckBox>("LaunchAtLoginToggle")!.IsEnabled);

        Assert.Contains("could not be restored", window.FindControl<TextBlock>("DropStatus")!.Text,
            StringComparison.OrdinalIgnoreCase);
        window.Close();
    }

    private sealed class FakeShell : INativeShell
    {

        public NativeShellStatus OpenResult { get; init; }
        public Queue<NativeShellStatus>? OpenResults { get; init; }
        public Queue<NativeShellStatus>? RevealResults { get; init; }
        public List<string> Opened { get; } = [];
        public List<string> Revealed { get; } = [];
        public NativeShellStatus ShortcutResult { get; init; } = NativeShellStatus.Success;
        public NativeShellStatus LaunchResult { get; init; } = NativeShellStatus.Success;
        public Queue<NativeShellStatus>? ShortcutResults { get; init; }
        public Queue<NativeShellStatus>? LaunchResults { get; init; }

        public string? LastShortcut { get; private set; }
        public bool? LastLaunchAtLogin { get; private set; }
        public NativeShellStatus ConfigureShortcut(string shortcut)
        {
            LastShortcut = shortcut;
            return ShortcutResults?.Dequeue() ?? ShortcutResult;
        }
        public NativeShellStatus Open(string target) { Opened.Add(target); return OpenResults?.Dequeue() ?? OpenResult; }
        public NativeShellStatus Reveal(string path) { Revealed.Add(path); return RevealResults?.Dequeue() ?? NativeShellStatus.Success; }
        public NativeShellStatus SetLaunchAtLogin(bool enabled)
        {
            LastLaunchAtLogin = enabled;
            return LaunchResults?.Dequeue() ?? LaunchResult;
        }

        public void Dispose() { }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }
        Assert.True(condition());
    }

    private sealed class FakeFileSystem(IEnumerable<string> existing) : IFileSystem
    {
        private readonly HashSet<string> existing = new(existing, StringComparer.Ordinal);
        public bool FileExists(string path) => existing.Contains(path);
        public bool DirectoryExists(string path) => existing.Contains(path);
    }

    private sealed class RecordingSettingsStore(AppSettings settings) : ISettingsStore
    {
        public AppSettings? Saved { get; private set; }
        public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
        public Task SaveSettingsAsync(AppSettings value, CancellationToken cancellationToken = default)
        {
            Saved = value;
            return Task.CompletedTask;
        }
    }

    private sealed class DeferredSettingsStore : ISettingsStore
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SaveCalls { get; private set; }
        public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AppSettings.Default);
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            return completion.Task;
        }
        public void Complete() => completion.SetResult();
    }

    private sealed class ThrowingSettingsStore : ISettingsStore
    {
        public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AppSettings.Default);
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("synthetic persistence failure"));
    }

    private sealed class FixedSettingsStore(AppSettings settings) : ISettingsStore
    {
        public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
