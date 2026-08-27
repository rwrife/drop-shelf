using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using DropShelf.Core;
using DropShelf.Platform.macOS;
using DropShelf.Platform.Windows;
using System.Runtime.InteropServices;

namespace DropShelf.App;

public readonly record struct StorageDropReference(string Path, FileReferenceKind Kind);

public sealed class OutboundDataTransfer : IDisposable
{
    private IReadOnlyList<IStorageItem>? resolvedItems;
    private int disposed;

    internal OutboundDataTransfer(IDataTransfer data, IReadOnlyList<IStorageItem>? resolvedItems = null)
    {
        Data = data;
        this.resolvedItems = resolvedItems;
    }

    public IDataTransfer Data { get; }
    public bool IsDisposed => Volatile.Read(ref disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        IReadOnlyList<IStorageItem>? items = Interlocked.Exchange(ref resolvedItems, null);
        if (items is null)
        {
            return;
        }

        DisposeStorageItems(items);
    }

    internal static void DisposeStorageItems(IEnumerable<IStorageItem> items)
    {
        foreach (IStorageItem item in items)
        {
            try
            {
                item.Dispose();
            }
            catch
            {
                // Storage handles are best-effort cleanup at an untrusted host boundary.
            }
        }
    }
}

public sealed partial class MainWindow : Window
{
    private readonly DropAdmissionService admission;
    private Point? dragStart;
    private bool dragInProgress;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Session = new ShelfSession();
        admission = new(new CanonicalDropConverter(), Session);
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDropHandler(this, OnDrop);
    }

    public ShelfSession Session { get; }

    public DropAdmissionResult AcceptDropForHost(InboundDropPayload payload, DateTimeOffset createdAt)
    {
        DropAdmissionResult result = admission.Admit(payload, createdAt);
        Render(result.UserMessage);
        return result;
    }

    /// <summary>Builds the live Avalonia transfer used by the native drag backend.</summary>
    public Task<OutboundDataTransfer> BuildDataTransferForHostAsync(IReadOnlyList<ShelfItem> orderedItems) =>
        BuildDataTransferForHostAsync(orderedItems, NativeUrlFormat);

    /// <summary>Host-test seam for a platform URL format while retaining universal file and text formats.</summary>
    public async Task<OutboundDataTransfer> BuildDataTransferForHostAsync(
        IReadOnlyList<ShelfItem> orderedItems, DataFormat<string>? nativeUrlFormat)
    {
        ArgumentNullException.ThrowIfNull(orderedItems);
        if (orderedItems.Count == 0 || (orderedItems.Count > 1 && orderedItems.Any(item => item.Payload is not FileReferencePayload)))
        {
            throw new ArgumentException("Select files, or one text or URL item.", nameof(orderedItems));
        }

        DataTransfer transfer = new();
        if (orderedItems.All(item => item.Payload is FileReferencePayload))
        {
            List<IStorageItem> resolved = new(orderedItems.Count);
            try
            {
                foreach (FileReferencePayload file in orderedItems.Select(item => (FileReferencePayload)item.Payload))
                {
                    IStorageItem? storageItem = file.ReferenceKind == FileReferenceKind.Directory
                        ? await StorageProvider.TryGetFolderFromPathAsync(file.Path)
                        : await StorageProvider.TryGetFileFromPathAsync(file.Path);
                    if (storageItem is null)
                    {
                        throw new InvalidOperationException("One or more selected files are unavailable.");
                    }
                    resolved.Add(storageItem);
                }

                foreach (IStorageItem item in resolved)
                {
                    transfer.Add(DataTransferItem.CreateFile(item));
                }
                return new(transfer, resolved);
            }
            catch
            {
                OutboundDataTransfer.DisposeStorageItems(resolved);
                throw;
            }
        }

        ShelfPayload payload = orderedItems[0].Payload;
        if (payload is TextPayload text)
        {
            transfer.Add(DataTransferItem.CreateText(text.Text));
            return new(transfer);
        }

        string url = ((UrlPayload)payload).Url.AbsoluteUri;
        DataTransferItem urlItem = DataTransferItem.CreateText(url);
        if (nativeUrlFormat is not null)
        {
            urlItem.Set(nativeUrlFormat, url);
        }
        transfer.Add(urlItem);
        return new(transfer);
    }

    internal static DataFormat<string>? NativeUrlFormat => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? DataFormat.CreateStringPlatformFormat(WindowsDragDropFormats.Url)
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
        ? DataFormat.CreateStringPlatformFormat(MacPasteboardFormats.Url)
        : null;

    /// <summary>Converts Avalonia's native-backed transfer without discarding malformed file members.</summary>
    public static InboundDropPayload ReadDataTransferForHost(
        IDataTransfer transfer, DataFormat<string>? nativeUrlFormat)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        return ReadDataTransferForHost(transfer, nativeUrlFormat, GetStoragePathOrInvalidMarker);
    }

    public static InboundDropPayload ReadDataTransferForHost(
        IDataTransfer transfer,
        DataFormat<string>? nativeUrlFormat,
        Func<IStorageItem, StorageDropReference> mapStorageItem)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        ArgumentNullException.ThrowIfNull(mapStorageItem);
        StorageDropReference[]? files = transfer.TryGetFiles()?.Select(item =>
        {
            try
            {
                return item is null ? InvalidStorageReference : mapStorageItem(item);
            }
            catch
            {
                return InvalidStorageReference;
            }
        }).ToArray();
        string? url = nativeUrlFormat is null ? null : transfer.TryGetValue(nativeUrlFormat);
        return new(files?.Select(file => file.Path).ToArray(), url, transfer.TryGetText(),
            files?.Select(file => file.Kind).ToArray());
    }

    private static readonly StorageDropReference InvalidStorageReference = new("\0", FileReferenceKind.File);

    private static StorageDropReference GetStoragePathOrInvalidMarker(IStorageItem item)
    {
        try
        {
            Uri path = item.Path;
            FileReferenceKind kind = item switch
            {
                IStorageFolder => FileReferenceKind.Directory,
                IStorageFile => FileReferenceKind.File,
                _ => throw new InvalidOperationException("Unsupported storage item type."),
            };
            return path.IsAbsoluteUri && path.IsFile
                ? new(path.LocalPath, kind)
                : InvalidStorageReference;
        }
        catch
        {
            return InvalidStorageReference;
        }
    }

    public DropAdmissionResult AcceptDataTransferForHost(
        IDataTransfer transfer,
        DataFormat<string>? nativeUrlFormat,
        DateTimeOffset createdAt,
        Func<IStorageItem, StorageDropReference>? mapStorageItem = null)
    {
        try
        {
            InboundDropPayload payload = ReadDataTransferForHost(
                transfer, nativeUrlFormat, mapStorageItem ?? GetStoragePathOrInvalidMarker);
            if (payload.FilePaths?.Any(path => path == InvalidStorageReference.Path) == true)
            {
                const string unsupported = "That drop does not contain supported content.";
                Render(unsupported);
                return DropAdmissionResult.Rejected(unsupported);
            }
            return AcceptDropForHost(payload, createdAt);
        }
        catch
        {
            const string message = "That drop does not contain supported content.";
            Render(message);
            return DropAdmissionResult.Rejected(message);
        }
    }

    private void OnDrop(object? sender, DragEventArgs eventArgs)
    {
        try
        {
            DropAdmissionResult result = AcceptDataTransferForHost(eventArgs.DataTransfer, NativeUrlFormat, DateTimeOffset.UtcNow);
            eventArgs.DragEffects = result.Accepted ? DragDropEffects.Copy : DragDropEffects.None;
        }
        catch
        {
            Render("That drop does not contain supported content.");
            eventArgs.DragEffects = DragDropEffects.None;
        }
        finally
        {
            eventArgs.Handled = true;
        }
    }

    private void Render(string status)
    {
        StackPanel panel = this.FindControl<StackPanel>("ShelfItems")!;
        panel.Children.Clear();
        IReadOnlyList<ShelfItem> fileItems = Session.Items
            .Where(item => item.Payload is FileReferencePayload)
            .ToArray();
        if (fileItems.Count >= 2)
        {
            TextBlock batchHandle = CreateDragHandle(
                "FileBatchDragHandle", $"Drag all {fileItems.Count} files", fileItems);
            panel.Children.Add(batchHandle);
        }

        for (int index = 0; index < Session.Items.Count; index++)
        {
            ShelfItem item = Session.Items[index];
            TextBlock card = CreateDragHandle(
                $"ItemName{index}", $"{item.Kind}: {item.DisplayName}", [item]);
            panel.Children.Add(card);
        }
        this.FindControl<TextBlock>("EmptyShelfMessage")!.IsVisible = Session.Items.Count == 0;
        this.FindControl<TextBlock>("DropStatus")!.Text = status;
    }

    private TextBlock CreateDragHandle(string name, string text, IReadOnlyList<ShelfItem> items)
    {
        TextBlock handle = new()
        {
            Name = name,
            Text = text,
            Tag = items,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        handle.PointerPressed += OnItemPointerPressed;
        handle.PointerMoved += OnItemPointerMoved;
        handle.PointerReleased += OnItemPointerReleased;
        return handle;
    }

    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (sender is Control control && eventArgs.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            dragStart = eventArgs.GetPosition(control);
        }
    }

    private async void OnItemPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (sender is not Control { Tag: IReadOnlyList<ShelfItem> items } control || dragStart is not { } start || dragInProgress ||
            !eventArgs.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Point current = eventArgs.GetPosition(control);
        if (Math.Abs(current.X - start.X) < 4 && Math.Abs(current.Y - start.Y) < 4)
        {
            return;
        }

        dragStart = null;
        dragInProgress = true;
        try
        {
            await RunOutboundDragForHostAsync(items, async transfer =>
            {
                _ = await DragDrop.DoDragDropAsync(eventArgs, transfer, DragDropEffects.Copy);
            });
        }
        finally
        {
            dragInProgress = false;
        }
    }

    public async Task RunOutboundDragForHostAsync(
        IReadOnlyList<ShelfItem> orderedItems, Func<IDataTransfer, Task> performDragAsync)
    {
        ArgumentNullException.ThrowIfNull(orderedItems);
        ArgumentNullException.ThrowIfNull(performDragAsync);
        try
        {
            using OutboundDataTransfer transfer = await BuildDataTransferForHostAsync(orderedItems);
            await performDragAsync(transfer.Data);
        }
        catch
        {
            Render("The drag could not be completed.");
        }
    }

    private void OnItemPointerReleased(object? sender, PointerReleasedEventArgs eventArgs) => dragStart = null;
}
