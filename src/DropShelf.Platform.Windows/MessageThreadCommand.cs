namespace DropShelf.Platform.Windows;

internal sealed class MessageThreadCommand(Func<WindowsNativeResult> action)
{
    private readonly Func<WindowsNativeResult> action = action ?? throw new ArgumentNullException(nameof(action));
    private readonly TaskCompletionSource<WindowsNativeResult> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int state;

    public Task<WindowsNativeResult> Completion => completion.Task;

    public void TryExecute()
    {
        if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
        {
            return;
        }

        _ = completion.TrySetResult(NativeCallbackContainment.Run(action));
    }

    public bool TryCancel()
    {
        if (Interlocked.CompareExchange(ref state, 2, 0) != 0)
        {
            return false;
        }

        _ = completion.TrySetResult(WindowsNativeResult.Failed);
        return true;
    }
}
