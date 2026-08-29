using Win = DropShelf.Platform.Windows;
using Mac = DropShelf.Platform.macOS;
using Xunit;

namespace DropShelf.Platform.Tests;

public sealed class NativeShellAdapterTests
{
    [Fact]
    public void NativeCallbackContainmentConvertsExceptionsWithoutEscaping()
    {
        Assert.Equal(Win.WindowsNativeResult.Failed, Win.NativeCallbackContainment.Run(() => throw new InvalidOperationException("boom")));
        Win.NativeCallbackContainment.RunCallback(() => throw new InvalidOperationException("boom"));
        Mac.NativeCallbackContainment.RunCallback(() => throw new InvalidOperationException("boom"));
    }

    [Fact]
    public void WindowsShortcutConflictKeepsPreviousRegistration()
    {
        FakeWindowsApi api = new();
        Win.WindowsShellAdapter adapter = new(api);

        Assert.True(adapter.ConfigureShortcut("Ctrl+Alt+Space").Succeeded);
        api.NextRegistration = Win.WindowsNativeResult.Conflict;

        Win.ShellOperationResult result = adapter.ConfigureShortcut("Ctrl+Shift+Space");

        Assert.Equal(Win.ShellError.ShortcutConflict, result.Error);
        Assert.Equal("Ctrl+Alt+Space", adapter.RegisteredShortcut);
        Assert.Equal(["Ctrl+Alt+Space", "Ctrl+Shift+Space"], api.Registered);
        Assert.Empty(api.Unregistered);
    }

    [Fact]
    public void MissingAndUntrustedTargetsAreRejectedWithoutCallingTheOs()
    {
        FakeWindowsApi api = new();
        Win.WindowsShellAdapter adapter = new(api);
        string missing = Path.GetFullPath($"missing-{Guid.NewGuid():N}.txt");

        Assert.Equal(Win.ShellError.InvalidTarget, adapter.Open("relative/private.txt").Error);
        Assert.Equal(Win.ShellError.InvalidTarget, adapter.Open("https://secret@example.test/").Error);
        Assert.Equal(Win.ShellError.TargetMissing, adapter.Open(missing).Error);
        Assert.Equal(Win.ShellError.TargetMissing, adapter.Reveal(missing).Error);
        Assert.Equal(0, api.OpenCalls + api.RevealCalls);
    }


    [Fact]
    public async Task CancelledWindowsMessageCommandCannotExecuteLater()
    {
        int nativeCalls = 0;
        Win.MessageThreadCommand command = new(() =>
        {
            nativeCalls++;
            return Win.WindowsNativeResult.Success;
        });

        Assert.True(command.TryCancel());
        command.TryExecute();

        Assert.Equal(0, nativeCalls);
        Assert.Equal(Win.WindowsNativeResult.Failed, await command.Completion);
    }

    [Fact]
    public async Task ExecutingWindowsMessageCommandCannotBeReportedAsCancelled()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Win.MessageThreadCommand command = new(() =>
        {
            started.SetResult();
            release.Task.GetAwaiter().GetResult();
            return Win.WindowsNativeResult.Success;
        });

        Task execution = Task.Run(command.TryExecute);
        await started.Task;
        Assert.False(command.TryCancel());
        release.SetResult();
        await execution;

        Assert.Equal(Win.WindowsNativeResult.Success, await command.Completion);
    }

    private sealed class FakeWindowsApi : Win.IWindowsShellApi
    {
        public Win.WindowsNativeResult NextRegistration { get; set; } = Win.WindowsNativeResult.Success;
        public List<string> Registered { get; } = [];
        public List<string> Unregistered { get; } = [];
        public int OpenCalls { get; private set; }
        public int RevealCalls { get; private set; }

        public Win.WindowsNativeResult RegisterShortcut(string shortcut)
        {
            Registered.Add(shortcut);
            return NextRegistration;
        }

        public void UnregisterShortcut(string shortcut) => Unregistered.Add(shortcut);
        public Win.WindowsNativeResult Open(Uri target) { OpenCalls++; return Win.WindowsNativeResult.Success; }
        public Win.WindowsNativeResult Reveal(string path) { RevealCalls++; return Win.WindowsNativeResult.Success; }
    }

}
