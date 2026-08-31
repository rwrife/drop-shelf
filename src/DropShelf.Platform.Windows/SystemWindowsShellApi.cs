using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DropShelf.Platform.Windows;

public static class NativeCallbackContainment
{
    public static WindowsNativeResult Run(Func<WindowsNativeResult> action)
    {
        try { return action(); } catch { return WindowsNativeResult.Failed; }
    }

    public static void RunCallback(Action action)
    {
        try { action(); } catch { }
    }
}

[SupportedOSPlatform("windows")]
public sealed partial class SystemWindowsShellApi : IWindowsShellApi, IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const uint WmHotKey = 0x0312;
    private const uint WmAppCommand = 0x8001;
    private const uint WmQuit = 0x0012;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const int HotKeyAlreadyRegistered = 1409;
    private readonly ConcurrentQueue<MessageThreadCommand> commands = new();
    private readonly Dictionary<string, int> registrations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ManualResetEventSlim ready = new();
    private readonly Thread messageThread;
    private readonly Action shortcutActivated;
    private uint messageThreadId;
    private int nextId = 1;
    private int disposed;

    public SystemWindowsShellApi(Action shortcutActivated)
    {
        this.shortcutActivated = shortcutActivated ?? throw new ArgumentNullException(nameof(shortcutActivated));
        messageThread = new(MessageLoop) { IsBackground = true, Name = "DropShelf global shortcut" };
        messageThread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException("The global shortcut message loop did not start.");
        }
    }

    public WindowsNativeResult RegisterShortcut(string shortcut)
        => !TryParseShortcut(shortcut, out uint modifiers, out uint key)
            ? WindowsNativeResult.InvalidTarget
            : InvokeOnMessageThread(() =>
        {
            if (registrations.ContainsKey(shortcut))
            {
                return WindowsNativeResult.Success;
            }
            int id = nextId++;
            if (!RegisterHotKey(0, id, modifiers | ModNoRepeat, key))
            {
                return Marshal.GetLastPInvokeError() == HotKeyAlreadyRegistered
                    ? WindowsNativeResult.Conflict
                    : WindowsNativeResult.Failed;
            }
            registrations.Add(shortcut, id);
            return WindowsNativeResult.Success;
        });

    public void UnregisterShortcut(string shortcut) => _ = InvokeOnMessageThread(() =>
    {
        if (registrations.Remove(shortcut, out int id))
        {
            _ = UnregisterHotKey(0, id);
        }
        return WindowsNativeResult.Success;
    });

    public WindowsNativeResult Open(Uri target) => Start(new ProcessStartInfo(target.IsFile ? target.LocalPath : target.AbsoluteUri) { UseShellExecute = true });
    public WindowsNativeResult Reveal(string path) => Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path }, UseShellExecute = false });

    public WindowsNativeResult SetLaunchAtLogin(bool enabled)
    {
        try
        {
            using RegistryKey? key = enabled ? Registry.CurrentUser.CreateSubKey(RunKey, writable: true) : Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null)
            {
                return WindowsNativeResult.Unavailable;
            }
            if (enabled)
            {
                string? executable = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executable))
                {
                    return WindowsNativeResult.Unavailable;
                }
                key.SetValue("DropShelf", $"\"{executable}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue("DropShelf", throwOnMissingValue: false);
            }
            return WindowsNativeResult.Success;
        }
        catch (UnauthorizedAccessException) { return WindowsNativeResult.PermissionDenied; }
        catch { return WindowsNativeResult.Failed; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        _ = PostThreadMessage(messageThreadId, WmQuit, 0, 0);
        _ = messageThread.Join(TimeSpan.FromSeconds(5));
        ready.Dispose();
    }

    private WindowsNativeResult InvokeOnMessageThread(Func<WindowsNativeResult> action)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return WindowsNativeResult.Unavailable;
        }
        MessageThreadCommand command = new(action);
        commands.Enqueue(command);
        if (!PostThreadMessage(messageThreadId, WmAppCommand, 0, 0))
        {
            if (command.TryCancel())
            {
                return WindowsNativeResult.Failed;
            }
        }
        else if (!command.Completion.Wait(TimeSpan.FromSeconds(5)) && command.TryCancel())
        {
            return WindowsNativeResult.Failed;
        }

        return command.Completion.GetAwaiter().GetResult();
    }

    private void MessageLoop()
    {
        messageThreadId = GetCurrentThreadId();
        _ = PeekMessage(out _, 0, 0, 0, 0);
        ready.Set();
        while (GetMessage(out NativeMessage message, 0, 0, 0) > 0)
        {
            if (message.Message == WmAppCommand)
            {
                while (commands.TryDequeue(out MessageThreadCommand? command))
                {
                    command.TryExecute();
                }
            }
            else if (message.Message == WmHotKey)
            {
                NativeCallbackContainment.RunCallback(shortcutActivated);
            }
        }
        while (commands.TryDequeue(out MessageThreadCommand? pending))
        {
            _ = pending.TryCancel();
        }
        foreach (int id in registrations.Values)
        {
            _ = UnregisterHotKey(0, id);
        }
        registrations.Clear();
    }

    private static bool TryParseShortcut(string shortcut, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        string[] parts = shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }
        foreach (string modifier in parts[..^1])
        {
            uint value = modifier.ToUpperInvariant() switch
            {
                "ALT" => ModAlt,
                "CTRL" or "CONTROL" => ModControl,
                "SHIFT" => ModShift,
                "WIN" or "WINDOWS" => ModWin,
                _ => 0,
            };
            if (value == 0)
            {
                return false;
            }
            modifiers |= value;
        }
        string keyName = parts[^1].ToUpperInvariant();
        key = keyName == "SPACE" ? 0x20U : keyName.Length == 1 && char.IsAsciiLetterOrDigit(keyName[0]) ? keyName[0] : 0U;
        return modifiers != 0 && key != 0;
    }

    private static WindowsNativeResult Start(ProcessStartInfo info)
    {
        try { return Process.Start(info) is null ? WindowsNativeResult.Failed : WindowsNativeResult.Success; }
        catch (UnauthorizedAccessException) { return WindowsNativeResult.PermissionDenied; }
        catch { return WindowsNativeResult.Failed; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeMessage
    {
        public readonly nint Window;
        public readonly uint Message;
        public readonly nuint WParam;
        public readonly nint LParam;
        public readonly uint Time;
        public readonly int X;
        public readonly int Y;
        public readonly uint Private;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint window, int id);
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll")]
    private static partial int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(out NativeMessage message, nint window, uint minimum, uint maximum, uint remove);
    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
