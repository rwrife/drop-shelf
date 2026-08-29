using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DropShelf.Platform.macOS;

public static class NativeCallbackContainment
{
    public static void RunCallback(Action action)
    {
        try { action(); } catch { }
    }
}

public sealed partial class SystemMacShellApi : IMacShellApi, IDisposable
{
    private const uint EventClassKeyboard = 0x6B657962;
    private const uint EventHotKeyPressed = 6;
    private const uint EventParamDirectObject = 0x2D2D2D2D;
    private const uint TypeEventHotKeyId = 0x686B6964;
    private const uint ControlKey = 1U << 12;
    private const uint OptionKey = 1U << 11;
    private const uint ShiftKey = 1U << 9;
    private const uint CommandKey = 1U << 8;
    private const int EventHotKeyExists = -9878;
    private readonly Dictionary<string, HotKeyRegistration> registrations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action shortcutActivated;
    private readonly EventHandlerCallback callback;
    private nint eventHandler;
    private nint serviceManagementLibrary;
    private uint nextId = 1;
    private int disposed;

    public SystemMacShellApi(Action shortcutActivated)
    {
        this.shortcutActivated = shortcutActivated ?? throw new ArgumentNullException(nameof(shortcutActivated));
        callback = OnHotKey;
        EventTypeSpec eventType = new(EventClassKeyboard, EventHotKeyPressed);
        int status = InstallEventHandler(GetApplicationEventTarget(), callback, 1, in eventType, 0, out eventHandler);
        if (status != 0)
        {
            throw new InvalidOperationException("The global shortcut event handler could not be installed.");
        }
    }

    public MacNativeResult RegisterShortcut(string shortcut)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return MacNativeResult.Unavailable;
        }
        if (registrations.ContainsKey(shortcut))
        {
            return MacNativeResult.Success;
        }
        if (!TryParseShortcut(shortcut, out uint modifiers, out uint keyCode))
        {
            return MacNativeResult.InvalidTarget;
        }
        EventHotKeyId id = new(0x44534846, nextId++);
        int status = RegisterEventHotKey(keyCode, modifiers, id, GetApplicationEventTarget(), 0, out nint reference);
        if (status != 0)
        {
            return status == EventHotKeyExists ? MacNativeResult.Conflict : MacNativeResult.Failed;
        }
        registrations.Add(shortcut, new(reference, id.Id));
        return MacNativeResult.Success;
    }

    public void UnregisterShortcut(string shortcut)
    {
        if (registrations.Remove(shortcut, out HotKeyRegistration registration))
        {
            _ = UnregisterEventHotKey(registration.Reference);
        }
    }

    public MacNativeResult Open(Uri target) => Run("/usr/bin/open", target.IsFile ? target.LocalPath : target.AbsoluteUri);
    public MacNativeResult Reveal(string path) => Run("/usr/bin/open", "-R", path);

    public MacNativeResult SetLaunchAtLogin(bool enabled)
    {
        try
        {
            if (serviceManagementLibrary == 0 && !NativeLibrary.TryLoad(
                "/System/Library/Frameworks/ServiceManagement.framework/ServiceManagement",
                out serviceManagementLibrary))
            {
                return MacNativeResult.Unavailable;
            }
            nint serviceClass = objc_getClass("SMAppService");
            if (serviceClass == 0)
            {
                return MacNativeResult.Unavailable;
            }
            nint service = objc_msgSend(serviceClass, sel_registerName("mainAppService"));
            if (service == 0)
            {
                return MacNativeResult.Unavailable;
            }
            string selector = enabled ? "registerAndReturnError:" : "unregisterAndReturnError:";
            nint error = 0;
            return objc_msgSend_bool_error(service, sel_registerName(selector), ref error)
                ? MacNativeResult.Success
                : MacNativeResult.Failed;
        }
        catch (EntryPointNotFoundException) { return MacNativeResult.Unavailable; }
        catch (DllNotFoundException) { return MacNativeResult.Unavailable; }
        catch { return MacNativeResult.Failed; }
    }


    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        foreach (HotKeyRegistration registration in registrations.Values)
        {
            _ = UnregisterEventHotKey(registration.Reference);
        }
        registrations.Clear();
        if (eventHandler != 0)
        {
            _ = RemoveEventHandler(eventHandler);
            eventHandler = 0;
        }
        if (serviceManagementLibrary != 0)
        {
            NativeLibrary.Free(serviceManagementLibrary);
            serviceManagementLibrary = 0;
        }
    }

    private int OnHotKey(nint nextHandler, nint eventReference, nint userData)
    {
        try
        {
            int status = GetEventParameter(eventReference, EventParamDirectObject, TypeEventHotKeyId, 0,
                (uint)Marshal.SizeOf<EventHotKeyId>(), out _, out EventHotKeyId id);
            if (status == 0 && Volatile.Read(ref disposed) == 0 && registrations.Values.Any(registration => registration.Id == id.Id))
            {
                NativeCallbackContainment.RunCallback(shortcutActivated);
            }
            return status;
        }
        catch
        {
            return -1;
        }
    }

    private static bool TryParseShortcut(string shortcut, out uint modifiers, out uint keyCode)
    {
        modifiers = 0;
        keyCode = 0;
        string[] parts = shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }
        foreach (string modifier in parts[..^1])
        {
            uint value = modifier.ToUpperInvariant() switch
            {
                "ALT" or "OPTION" => OptionKey,
                "CTRL" or "CONTROL" => ControlKey,
                "SHIFT" => ShiftKey,
                "CMD" or "COMMAND" => CommandKey,
                _ => 0,
            };
            if (value == 0)
            {
                return false;
            }
            modifiers |= value;
        }
        keyCode = parts[^1].ToUpperInvariant() switch
        {
            "SPACE" => 49,
            "A" => 0,
            "B" => 11,
            "C" => 8,
            "D" => 2,
            "E" => 14,
            "F" => 3,
            "G" => 5,
            "H" => 4,
            "I" => 34,
            "J" => 38,
            "K" => 40,
            "L" => 37,
            "M" => 46,
            "N" => 45,
            "O" => 31,
            "P" => 35,
            "Q" => 12,
            "R" => 15,
            "S" => 1,
            "T" => 17,
            "U" => 32,
            "V" => 9,
            "W" => 13,
            "X" => 7,
            "Y" => 16,
            "Z" => 6,
            "0" => 29,
            "1" => 18,
            "2" => 19,
            "3" => 20,
            "4" => 21,
            "5" => 23,
            "6" => 22,
            "7" => 26,
            "8" => 28,
            "9" => 25,
            _ => uint.MaxValue,
        };
        return modifiers != 0 && keyCode != uint.MaxValue;
    }

    private static MacNativeResult Run(string fileName, params string[] arguments)
    {
        try
        {
            ProcessStartInfo info = new(fileName) { UseShellExecute = false };
            foreach (string argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }
            return Process.Start(info) is null ? MacNativeResult.Failed : MacNativeResult.Success;
        }
        catch (UnauthorizedAccessException) { return MacNativeResult.PermissionDenied; }
        catch { return MacNativeResult.Failed; }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EventHandlerCallback(nint nextHandler, nint eventReference, nint userData);
    private readonly record struct HotKeyRegistration(nint Reference, uint Id);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct EventTypeSpec(uint EventClass, uint EventKind);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct EventHotKeyId(uint Signature, uint Id);

    [LibraryImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
    private static partial nint GetApplicationEventTarget();
    [LibraryImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
    private static partial int InstallEventHandler(nint target, EventHandlerCallback handler, uint count,
        in EventTypeSpec eventTypes, nint userData, out nint handlerReference);
    [LibraryImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
    private static partial int RemoveEventHandler(nint handlerReference);
    [LibraryImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
    private static partial int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyId id,
        nint target, uint options, out nint hotKeyReference);
    [LibraryImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
    private static partial int UnregisterEventHotKey(nint hotKeyReference);
    [LibraryImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
    private static partial int GetEventParameter(nint eventReference, uint name, uint desiredType,
        nint actualType, uint bufferSize, out uint actualSize, out EventHotKeyId data);
    [LibraryImport("/usr/lib/libobjc.A.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);
    [LibraryImport("/usr/lib/libobjc.A.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial nint objc_msgSend(nint receiver, nint selector);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool objc_msgSend_bool_error(nint receiver, nint selector, ref nint error);
}
