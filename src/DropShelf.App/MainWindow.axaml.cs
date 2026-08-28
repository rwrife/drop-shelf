using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Platform;
using DropShelf.Core;
using DropShelf.Platform.macOS;
using DropShelf.Platform.Windows;
using System.Runtime.InteropServices;

namespace DropShelf.App;

public readonly record struct StorageDropReference(string Path, FileReferenceKind Kind);
public enum ShelfUiState { Ready, Loading, Expired, Unavailable, RecoverableError }

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
    private DockEdge dockEdge;
    private Screens? activeScreens;
    private readonly Func<Task>? retryShelfLoad;
    private ShelfBounds? expandedBounds;
    private bool retryInProgress;

    public MainWindow()
        : this(AppSettings.Default, null, null)
    {
    }

    public MainWindow(IShelfItemActions? actions)
        : this(AppSettings.Default, actions, null)
    {
    }

    public MainWindow(IShelfItemActions? actions, Func<Task>? retryShelfLoad)
        : this(AppSettings.Default, actions, retryShelfLoad)
    {
    }

    public MainWindow(AppSettings settings, IShelfItemActions? actions = null, Func<Task>? retryShelfLoad = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AvaloniaXamlLoader.Load(this);
        dockEdge = settings.DockEdge;
        this.retryShelfLoad = retryShelfLoad;
        Session = new ShelfSession();
        ViewModel = new ShelfViewModel(Session, DateTimeOffset.UtcNow, actions);
        admission = new(new CanonicalDropConverter(), Session);
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDropHandler(this, OnDrop);
        AddHandler(KeyDownEvent, OnShelfKeyDown);
        this.FindControl<Button>("MoveUpButton")!.Click += (_, _) => RunAndRender(() => _ = ViewModel.MoveSelected(-1));
        this.FindControl<Button>("MoveDownButton")!.Click += (_, _) => RunAndRender(() => _ = ViewModel.MoveSelected(1));
        this.FindControl<Button>("CopyButton")!.Click += async (_, _) => await RunViewModelActionAsync(ViewModel.CopySelectedAsync);
        this.FindControl<Button>("OpenButton")!.Click += async (_, _) => await RunViewModelActionAsync(ViewModel.OpenSelectedAsync);
        this.FindControl<Button>("RevealButton")!.Click += async (_, _) => await RunViewModelActionAsync(ViewModel.RevealSelectedAsync);
        this.FindControl<Button>("PinButton")!.Click += (_, _) => RunAndRender(ViewModel.TogglePinned);
        this.FindControl<Button>("RemoveButton")!.Click += (_, _) => RunAndRender(() => _ = ViewModel.RemoveSelected());
        this.FindControl<Button>("ClearButton")!.Click += (_, _) => RunAndRender(ViewModel.Clear);
        this.FindControl<Button>("CollapseButton")!.Click += (_, _) => SetCollapsed(true);
        this.FindControl<Button>("ExpandButton")!.Click += (_, _) => SetCollapsed(false);
        this.FindControl<Button>("RetryButton")!.Click += OnRetryShelfLoad;
        Opened += OnWindowOpened;
        Closed += OnWindowClosed;
        Render("Ready for a drop.");
    }

    public ShelfSession Session { get; }
    public ShelfViewModel ViewModel { get; }

    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        dockEdge = settings.DockEdge;
        RecoverDockToCurrentScreen();
    }

    public DropAdmissionResult AcceptDropForHost(InboundDropPayload payload, DateTimeOffset createdAt)
    {
        DropAdmissionResult result = admission.Admit(payload, createdAt);
        if (result.Accepted)
        {
            ViewModel.ExistingItemsAdded(result.Items);
        }
        Render(result.UserMessage);
        RestoreViewModelFocus();
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

        for (int index = 0; index < ViewModel.Cards.Count; index++)
        {
            ShelfCardViewModel cardModel = ViewModel.Cards[index];
            ShelfItem item = Session.Items[index];
            TextBlock content = new()
            {
                Text = BuildVisibleCardText(cardModel),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };
            ToggleButton card = new()
            {
                Name = $"ItemName{index}",
                Content = content,
                Tag = new ShelfCardTag(item),
                IsChecked = cardModel.IsSelected,
                MinHeight = ShelfGeometry.MinimumReachableSize,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            };
            card.Classes.Add("shelf-card");
            AutomationProperties.SetName(card, cardModel.AccessibleName);
            AutomationProperties.SetControlTypeOverride(card, AutomationControlType.ListItem);
            AutomationProperties.SetPositionInSet(card, index + 1);
            AutomationProperties.SetSizeOfSet(card, ViewModel.Cards.Count);
            AutomationProperties.SetItemStatus(card, cardModel.IsSelected ? "Selected" : "Not selected");
            card.Click += (_, _) =>
            {
                ViewModel.ToggleSelection(cardModel.Id);
                Render(ViewModel.Announcement);
                RestoreViewModelFocus();
            };
            card.PointerPressed += OnItemPointerPressed;
            card.PointerMoved += OnItemPointerMoved;
            card.PointerReleased += OnItemPointerReleased;
            panel.Children.Add(card);
        }
        this.FindControl<TextBlock>("EmptyShelfMessage")!.IsVisible = Session.Items.Count == 0;
        SetStatusText(status);
    }

    private void SetStatusText(string status)
    {
        this.FindControl<TextBlock>("DropStatus")!.Text = status;
        this.FindControl<TextBlock>("LiveStatus")!.Text = status;
    }

    private static string BuildVisibleCardText(ShelfCardViewModel card)
    {
        string source = card.SourceHint is null ? string.Empty : $" · From {card.SourceHint}";
        string states = $" · {(card.IsPinned ? "Pinned" : "Not pinned")}{(card.IsMissing ? " · Unavailable" : string.Empty)}";
        return $"{card.TypeLabel} · {card.DisplayLabel}{source} · {card.AgeLabel}{states}";
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
        IReadOnlyList<ShelfItem>? items = sender switch
        {
            Control { Tag: IReadOnlyList<ShelfItem> batch } => batch,
            Control { Tag: ShelfCardTag card } => [card.Item],
            _ => null,
        };
        if (sender is not Control control || items is null || dragStart is not { } start || dragInProgress ||
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

    public void ShowShelfState(ShelfUiState state)
    {
        TextBlock message = this.FindControl<TextBlock>("StateMessage")!;
        Button retry = this.FindControl<Button>("RetryButton")!;
        string status;
        (message.Text, retry.IsVisible, status) = state switch
        {
            ShelfUiState.Ready => (string.Empty, false, "Shelf ready."),
            ShelfUiState.Loading => ("Loading shelf items…", false, "Loading shelf items…"),
            ShelfUiState.Expired => ("Expired items were removed according to your retention setting.", false,
                "Expired items were removed according to your retention setting."),
            ShelfUiState.Unavailable => ("Some source items are unavailable. Remove them or try again after reconnecting the source.", false,
                "Some source items are unavailable. Remove them or try again after reconnecting the source."),
            ShelfUiState.RecoverableError => ("Shelf items could not be loaded.", true, "Shelf items could not be loaded."),
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
        SetStatusText(status);
    }

    private async void OnRetryShelfLoad(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (retryInProgress)
        {
            return;
        }
        retryInProgress = true;
        ShowShelfState(ShelfUiState.Loading);
        try
        {
            if (retryShelfLoad is null)
            {
                throw new InvalidOperationException("No shelf-load retry action is configured.");
            }
            await retryShelfLoad();
            ShowShelfState(ShelfUiState.Ready);
            RestoreViewModelFocus();
        }
        catch
        {
            ShowShelfState(ShelfUiState.RecoverableError);
        }
        finally
        {
            retryInProgress = false;
        }
    }

    private async void OnShelfKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        bool commandButtonFocused = eventArgs.Source is Button and not ToggleButton;
        eventArgs.Handled = await HandleShelfShortcutForHostAsync(
            eventArgs.Key, eventArgs.KeyModifiers, commandButtonFocused);
    }

    public async Task<bool> HandleShelfShortcutForHostAsync(
        Key key, KeyModifiers modifiers, bool commandButtonFocused)
    {
        bool control = modifiers.HasFlag(KeyModifiers.Control);
        bool alt = modifiers.HasFlag(KeyModifiers.Alt);
        if (commandButtonFocused && key is Key.Enter or Key.Space)
        {
            return false;
        }
        if (control && key == Key.A)
        {
            RunAndRender(ViewModel.SelectAll);
        }
        else if (control && key == Key.C)
        {
            await RunViewModelActionAsync(ViewModel.CopySelectedAsync);
        }
        else if (key == Key.Delete)
        {
            RunAndRender(() => _ = ViewModel.RemoveSelected());
        }
        else if (alt && key is Key.Up or Key.Left)
        {
            RunAndRender(() => _ = ViewModel.MoveSelected(-1));
        }
        else if (alt && key is Key.Down or Key.Right)
        {
            RunAndRender(() => _ = ViewModel.MoveSelected(1));
        }
        else if (key == Key.Enter)
        {
            await RunViewModelActionAsync(ViewModel.OpenSelectedAsync);
        }
        else if (key == Key.P)
        {
            RunAndRender(ViewModel.TogglePinned);
        }
        else if (key == Key.Escape)
        {
            SetCollapsed(true);
        }
        else
        {
            return false;
        }
        return true;
    }

    private async Task RunViewModelActionAsync(Func<Task> action)
    {
        Task running = action();
        SetActionButtonsEnabled(!ViewModel.IsActionInProgress);
        try
        {
            await running;
        }
        finally
        {
            SetActionButtonsEnabled(!ViewModel.IsActionInProgress);
            Render(ViewModel.Announcement);
            RestoreViewModelFocus();
        }
    }

    private void SetActionButtonsEnabled(bool enabled)
    {
        this.FindControl<Button>("CopyButton")!.IsEnabled = enabled;
        this.FindControl<Button>("OpenButton")!.IsEnabled = enabled;
        this.FindControl<Button>("RevealButton")!.IsEnabled = enabled;
    }

    private void RunAndRender(Action action)
    {
        action();
        Render(ViewModel.Announcement);
        RestoreViewModelFocus();
    }

    private void RestoreViewModelFocus()
    {
        if (ViewModel.IsCollapsed)
        {
            _ = this.FindControl<Button>("ExpandButton")!.Focus();
            return;
        }

        Control target = ViewModel.FocusedItemId is Guid id
            ? this.FindControl<StackPanel>("ShelfItems")!.Children
                .OfType<Control>().First(control => control is ToggleButton { Tag: ShelfCardTag tag } && tag.Item.Id == id)
            : this.FindControl<TextBlock>("EmptyShelfMessage")!;
        _ = target.Focus();
    }

    private void SetCollapsed(bool collapsed)
    {
        ShelfBounds? restoreBounds = collapsed ? null : expandedBounds;
        if (collapsed && !ViewModel.IsCollapsed)
        {
            expandedBounds = CurrentBounds();
        }
        ViewModel.SetCollapsed(collapsed);
        this.FindControl<Grid>("ExpandedShelf")!.IsVisible = !collapsed;
        Button expand = this.FindControl<Button>("ExpandButton")!;
        expand.IsVisible = collapsed;
        if (collapsed)
        {
            _ = expand.Focus();
        }
        else
        {
            Render(ViewModel.Announcement);
            RestoreViewModelFocus();
        }
        SetStatusText(ViewModel.Announcement);
        RecoverDockToCurrentScreen(restoreBounds);
    }

    private void OnWindowOpened(object? sender, EventArgs eventArgs)
    {
        activeScreens = Screens;
        activeScreens.Changed += OnScreensChanged;
        RecoverDockToCurrentScreen();
    }

    private void OnWindowClosed(object? sender, EventArgs eventArgs)
    {
        if (activeScreens is not null)
        {
            activeScreens.Changed -= OnScreensChanged;
            activeScreens = null;
        }
    }

    private void OnScreensChanged(object? sender, EventArgs eventArgs) => RecoverDockToCurrentScreen();

    public ShelfBounds RecoverDockForHost(ShelfBounds current, ShelfBounds workArea, double renderScaling = 1) =>
        RecoverDock(current, workArea, previousOverride: null, renderScaling);

    private void RecoverDockToCurrentScreen(ShelfBounds? previousOverride = null)
    {
        Screens? screens = activeScreens;
        if (screens is null)
        {
            return;
        }
        Screen? screen = screens.ScreenFromWindow(this) ?? screens.Primary;
        if (screen is null)
        {
            return;
        }
        PixelRect work = screen.WorkingArea;
        double scale = RenderScaling <= 0 ? 1 : RenderScaling;
        ShelfBounds current = CurrentBounds();
        ShelfBounds recovered = RecoverDock(
            current, new ShelfBounds(work.X, work.Y, work.Width, work.Height), previousOverride, scale);
        Position = new PixelPoint(recovered.Left, recovered.Top);
        Width = recovered.Width / scale;
        Height = recovered.Height / scale;
    }

    private ShelfBounds RecoverDock(
        ShelfBounds current, ShelfBounds workArea, ShelfBounds? previousOverride, double renderScaling)
    {
        ShelfBounds recovered = ShelfGeometry.Recover(
            previousOverride ?? current, workArea, dockEdge, ViewModel.IsCollapsed, renderScaling);
        if (!ViewModel.IsCollapsed)
        {
            expandedBounds = recovered;
        }
        return recovered;
    }

    private ShelfBounds CurrentBounds()
    {
        double scale = RenderScaling <= 0 ? 1 : RenderScaling;
        PixelSize size = PixelSize.FromSize(ClientSize, scale);
        return new ShelfBounds(Position.X, Position.Y, size.Width, size.Height);
    }

    private sealed record ShelfCardTag(ShelfItem Item);
}
