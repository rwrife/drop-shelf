using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Xunit;

namespace DropShelf.App.Tests;

public sealed class MainWindowTests
{
    [Fact]
    public async Task PackagedSmokeModeExercisesAddRestoreAndClearWithoutStartingTheUi()
    {
        int result = await PackagedSmokeTest.RunAsync();

        Assert.Equal(0, result);
    }

    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] CommandButtonNames =
        ["MoveUpButton", "MoveDownButton", "CopyButton", "OpenButton", "RevealButton", "PinButton", "RemoveButton", "ClearButton", "CollapseButton"];

    [AvaloniaFact]
    public void EmptyShelfWindowExposesClearInitialState()
    {
        MainWindow window = new();
        TextBlock? emptyState = window.FindControl<TextBlock>("EmptyShelfMessage");

        window.Show();

        Assert.NotNull(emptyState);
        Assert.True(window.IsVisible);
        Assert.Equal("Drop Shelf", window.Title);
        Assert.Equal("Drop files, text, or URLs here", emptyState.Text);

        window.Close();
    }

    [AvaloniaFact]
    public void AcceptedSyntheticDropRendersSafeItemAndStatus()
    {
        MainWindow window = new();
        string path = Path.GetFullPath(Path.Combine("private", "report.txt"));

        Core.DropAdmissionResult result = window.AcceptDropForHost(
            new Core.InboundDropPayload([path], null, null),
            new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero));

        Assert.True(result.Accepted);
        StackPanel items = window.FindControl<StackPanel>("ShelfItems")!;
        ToggleButton rendered = Assert.IsType<ToggleButton>(Assert.Single(items.Children));
        string renderedText = Assert.IsType<TextBlock>(rendered.Content).Text ?? string.Empty;
        Assert.Contains("report.txt", renderedText, StringComparison.Ordinal);
        Assert.DoesNotContain(path, renderedText, StringComparison.Ordinal);
        Assert.DoesNotContain(path, window.FindControl<TextBlock>("DropStatus")?.Text ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("Added 1 item.", window.FindControl<TextBlock>("DropStatus")?.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void TextDropNeverRendersOrAnnouncesPayloadContentByDefault()
    {
        const string privateText = "quarterly password rotation notes";
        MainWindow window = new();

        _ = window.AcceptDropForHost(new Core.InboundDropPayload(null, null, privateText), Now);

        ToggleButton card = Assert.IsType<ToggleButton>(
            Assert.Single(window.FindControl<StackPanel>("ShelfItems")!.Children));
        string rendered = Assert.IsType<TextBlock>(card.Content).Text ?? string.Empty;
        Assert.Contains("Text", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(privateText, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(privateText, AutomationProperties.GetName(card), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void InteractiveCardsAndCommandsExposeAccessibleWrappedState()
    {
        MainWindow window = new();
        string path = Path.GetFullPath(Path.Combine("private", "report.txt"));

        _ = window.AcceptDropForHost(new Core.InboundDropPayload([path], null, null), Now);

        StackPanel items = window.FindControl<StackPanel>("ShelfItems")!;
        ToggleButton card = Assert.IsType<ToggleButton>(Assert.Single(items.Children));
        Assert.Equal(AutomationControlType.ListItem, AutomationProperties.GetControlTypeOverride(card));
        Assert.Contains("File, report.txt", AutomationProperties.GetName(card), StringComparison.Ordinal);
        Assert.DoesNotContain(path, AutomationProperties.GetName(card), StringComparison.Ordinal);
        Assert.Equal(1, AutomationProperties.GetPositionInSet(card));
        Assert.Equal(1, AutomationProperties.GetSizeOfSet(card));
        Assert.True(card.MinHeight >= 44);

        Assert.Equal(AutomationLiveSetting.Polite,
            AutomationProperties.GetLiveSetting(window.FindControl<TextBlock>("LiveStatus")!));
        Assert.All(CommandButtonNames,
            name => Assert.True(window.FindControl<Button>(name)?.IsVisible));
        Assert.Equal("›", window.FindControl<Button>("ExpandButton")?.Content);
        window.Close();
    }

    [AvaloniaFact]
    public async Task StartupSettingsLoadIsAsynchronousAndAppliesThePersistedDockEdge()
    {
        Core.AppSettings expected = Core.AppSettings.Create(Core.DockEdge.Top);
        TaskCompletionSource<Core.AppSettings> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        MainWindow window = new();

        Task load = App.LoadAndApplyStartupSettingsForHostAsync(window, new DeferredSettingsStore(pending.Task));

        Assert.False(load.IsCompleted);
        Assert.Equal("Loading shelf items…", window.FindControl<TextBlock>("StateMessage")?.Text);
        pending.SetResult(expected);
        await load;
        ShelfBounds recovered = window.RecoverDockForHost(
            new ShelfBounds(100, 100, 500, 600), new ShelfBounds(0, 0, 1200, 900));
        Assert.Equal(0, recovered.Top);
        window.Close();
    }

    [AvaloniaFact]
    public void UsefulShelfStatesOfferVisibleRecoveryActions()
    {
        MainWindow window = new();

        window.ShowShelfState(ShelfUiState.Loading);
        Assert.Equal("Loading shelf items…", window.FindControl<TextBlock>("StateMessage")?.Text);
        window.ShowShelfState(ShelfUiState.RecoverableError);
        Assert.Equal("Shelf items could not be loaded.", window.FindControl<TextBlock>("StateMessage")?.Text);
        Assert.True(window.FindControl<Button>("RetryButton")?.IsVisible);
        Assert.True(window.FindControl<Button>("ResetLocalDataButton")?.IsVisible);
        Grid recoveryRow = Assert.IsType<Grid>(window.FindControl<TextBlock>("StateMessage")?.Parent);
        Assert.Equal(2, recoveryRow.ColumnDefinitions.Count);
        window.ShowShelfState(ShelfUiState.Unavailable);
        Assert.Equal("Some source items are unavailable. Remove them or try again after reconnecting the source.",
            window.FindControl<TextBlock>("StateMessage")?.Text);
        window.ShowShelfState(ShelfUiState.Expired);
        Assert.Equal("Expired items were removed according to your retention setting.",
            window.FindControl<TextBlock>("StateMessage")?.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task StartupLoadRejectsMutationsUntilStoredSnapshotIsApplied()
    {
        BlockingLoadShelfStore store = new(new Core.StoreSnapshot([], Core.AppSettings.Default));
        Core.ShelfDataService service = new(store);
        MainWindow window = new();

        Task loading = App.LoadAndApplyStartupDataForHostAsync(window, service);
        _ = await store.LoadStarted.Task;

        Core.DropAdmissionResult rejected = window.AcceptDropForHost(
            new Core.InboundDropPayload([], "during-load", null), DateTimeOffset.UtcNow);
        Assert.False(rejected.Accepted);
        Assert.Empty(window.Session.Items);

        _ = store.ReleaseLoad.TrySetResult(true);
        await loading;

        Core.DropAdmissionResult accepted = window.AcceptDropForHost(
            new Core.InboundDropPayload([], null, "after-load"), DateTimeOffset.UtcNow);
        Assert.True(accepted.Accepted);
        window.Close();
    }

    [AvaloniaFact]
    public async Task QueuedPersistenceCapturesEachMutationSnapshotBeforeBackgroundSave()
    {
        BlockingSaveShelfStore store = new();
        Core.ShelfDataService service = new(store);
        MainWindow window = new();
        window.ConfigureLocalDataForHost(service, Core.AppSettings.Default);

        _ = window.AcceptDropForHost(new Core.InboundDropPayload([], null, "first"), DateTimeOffset.UtcNow);
        _ = await store.FirstSaveStarted.Task;
        _ = window.AcceptDropForHost(new Core.InboundDropPayload([], null, "second"), DateTimeOffset.UtcNow);
        _ = store.ReleaseFirstSave.TrySetResult(true);
        await window.FlushPersistenceForHostAsync();

        Assert.Equal(2, store.Saved.Count);
        _ = Assert.Single(store.Saved[0].Items);
        Assert.Equal(2, store.Saved[1].Items.Count);
        window.Close();
    }

    [AvaloniaFact]
    public async Task StartupDataRestoresSessionExpiresOldUnpinnedAndPersistsDrops()
    {
        Core.ShelfItem old = Core.ShelfItem.Create(Guid.NewGuid(), "old", Core.TextPayload.Create("old"),
            Now - TimeSpan.FromDays(2));
        Core.ShelfItem pinned = Core.ShelfItem.Create(Guid.NewGuid(), "pinned", Core.TextPayload.Create("pinned"),
            Now - TimeSpan.FromDays(2), isPinned: true, ordinal: 1);
        RecordingShelfStore store = new(new Core.StoreSnapshot([old, pinned], Core.AppSettings.Default));
        Core.ShelfDataService service = new(store, new FixedClock(Now));
        MainWindow window = new();
        window.ConfigureLocalDataForHost(service, Core.AppSettings.Default);

        await App.LoadAndApplyStartupDataForHostAsync(window, service);
        _ = window.AcceptDropForHost(new Core.InboundDropPayload(null, null, "new private text"), Now);
        await window.FlushPersistenceForHostAsync();

        Assert.Equal(2, window.Session.Items.Count);
        Assert.DoesNotContain(window.Session.Items, item => item.Id == old.Id);
        Assert.Contains(window.Session.Items, item => item.Id == pinned.Id);
        Assert.Equal(2, store.Saved!.Items.Count);
        Assert.DoesNotContain("new private text", window.FindControl<TextBlock>("DropStatus")!.Text, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public async Task PrivacyControlsPreviewPolicyClearUnpinnedAndPreservePinnedItems()
    {
        Core.ShelfItem old = Core.ShelfItem.Create(Guid.NewGuid(), "old", Core.TextPayload.Create("private old"),
            Now - TimeSpan.FromDays(2));
        Core.ShelfItem pinned = Core.ShelfItem.Create(Guid.NewGuid(), "pinned", Core.TextPayload.Create("private pinned"),
            Now - TimeSpan.FromDays(2), isPinned: true, ordinal: 1);
        RecordingShelfStore store = new(new Core.StoreSnapshot([old, pinned], Core.AppSettings.Default));
        MainWindow window = new();
        window.ConfigureLocalDataForHost(new Core.ShelfDataService(store, new FixedClock(Now)), Core.AppSettings.Default);
        window.ApplySnapshotForHost(new Core.StoreSnapshot([old, pinned], Core.AppSettings.Default));

        int preview = window.PreviewRetentionForHost(TimeSpan.FromDays(1), expireOnExit: false);
        int removed = await window.ClearUnpinnedForHostAsync();

        Assert.Equal(1, preview);
        Assert.Equal(1, removed);
        Assert.Equal(pinned.Id, Assert.Single(window.Session.Items).Id);
        Assert.Contains("1 item", window.FindControl<TextBlock>("RetentionPreview")!.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("private", window.FindControl<TextBlock>("DropStatus")!.Text, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public async Task MetadataImportIsTransactionalAndPrivateWhileExportIncludesNoFileBytes()
    {
        string path = Path.GetFullPath("private-source.txt");
        Core.ShelfItem item = Core.ShelfItem.Create(Guid.NewGuid(), "private-source.txt",
            Core.FileReferencePayload.Create(path), Now);
        RecordingShelfStore store = new(new Core.StoreSnapshot([item], Core.AppSettings.Default));
        MainWindow window = new();
        window.ConfigureLocalDataForHost(new Core.ShelfDataService(store, new FixedClock(Now)), Core.AppSettings.Default);
        window.ApplySnapshotForHost(new Core.StoreSnapshot([item], Core.AppSettings.Default));

        byte[] exported = window.ExportMetadataForHost();
        bool imported = await window.ImportMetadataForHostAsync(/*lang=json,strict*/ "{\"schemaVersion\":999,\"private\":\"secret\"}"u8.ToArray());

        Assert.False(imported);
        Assert.Equal(item.Id, Assert.Single(window.Session.Items).Id);
        Assert.DoesNotContain("fileContents", System.Text.Encoding.UTF8.GetString(exported), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Import failed. Current shelf data was kept.", window.FindControl<TextBlock>("DropStatus")!.Text);
        Assert.DoesNotContain(path, window.FindControl<TextBlock>("DropStatus")!.Text, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void PrivacyControlsExplainRetentionAndAppOwnedStorageWithoutExposingPayloads()
    {
        MainWindow window = new();

        Assert.NotNull(window.FindControl<ComboBox>("RetentionPicker"));
        Assert.NotNull(window.FindControl<Button>("ClearUnpinnedButton"));
        Assert.NotNull(window.FindControl<Button>("ExportButton"));
        Assert.NotNull(window.FindControl<Button>("ImportButton"));
        string disclosure = window.FindControl<TextBlock>("StorageDisclosure")!.Text ?? string.Empty;
        Assert.Contains("24 hours", disclosure, StringComparison.Ordinal);
        Assert.Contains("DropShelf", disclosure, StringComparison.Ordinal);
        Assert.Contains("file contents", disclosure, StringComparison.OrdinalIgnoreCase);
        window.Close();
    }

    [AvaloniaFact]
    public void RetryActionTransitionsARecoverableStateBackToReady()
    {
        int attempts = 0;
        MainWindow window = new(null, () =>
        {
            attempts++;
            return Task.CompletedTask;
        });
        window.ShowShelfState(ShelfUiState.RecoverableError);

        window.FindControl<Button>("RetryButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(1, attempts);
        Assert.Equal(string.Empty, window.FindControl<TextBlock>("StateMessage")?.Text);
        Assert.False(window.FindControl<Button>("RetryButton")?.IsVisible);
        Assert.Equal("Shelf ready.", window.FindControl<TextBlock>("DropStatus")?.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void PointerReorderAndRemoveCommandsRestoreActualCardFocus()
    {
        MainWindow window = new();
        window.Show();
        _ = window.AcceptDropForHost(new Core.InboundDropPayload(null, null, "first"), Now);
        _ = window.AcceptDropForHost(new Core.InboundDropPayload(null, null, "second"), Now.AddSeconds(1));

        Button moveUp = window.FindControl<Button>("MoveUpButton")!;
        Assert.NotNull(moveUp);
        moveUp.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("second", Assert.IsType<Core.TextPayload>(window.Session.Items[0].Payload).Text);

        Button remove = window.FindControl<Button>("RemoveButton")!;
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        ToggleButton survivor = Assert.IsType<ToggleButton>(
            Assert.Single(window.FindControl<StackPanel>("ShelfItems")!.Children));
        Assert.Equal("first", Assert.IsType<Core.TextPayload>(window.Session.Items[0].Payload).Text);
        Assert.True(survivor.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void CollapseThenExpandRestoresUsableExpandedGeometry()
    {
        MainWindow window = new();
        window.Show();
        double expandedHeight = window.Height;

        window.FindControl<Button>("CollapseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(window.Height <= ShelfGeometry.MinimumReachableSize);
        Assert.Equal("Shelf collapsed. Expand button focused.", window.FindControl<TextBlock>("LiveStatus")?.Text);
        Assert.True(window.FindControl<TextBlock>("LiveStatus")?.IsVisible);
        window.FindControl<Button>("ExpandButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(window.Height >= Math.Min(180, expandedHeight));
        Assert.True(window.FindControl<Grid>("ExpandedShelf")!.IsVisible);
        window.Close();
    }

    [Fact]
    public void TargetScreenScalingWinsOverStaleWindowScaling()
    {
        Assert.Equal(2, MainWindow.ResolveTargetScaleForHost(2, 1));
        Assert.Equal(1.5, MainWindow.ResolveTargetScaleForHost(0, 1.5));
    }

    [AvaloniaFact]
    public void ConfiguredDockEdgeAndLiveExpandedThicknessDriveRecovery()
    {
        MainWindow window = new(Core.AppSettings.Create(Core.DockEdge.Left));

        ShelfBounds recovered = window.RecoverDockForHost(
            new ShelfBounds(100, 100, 500, 600),
            new ShelfBounds(0, 0, 1200, 900));

        Assert.Equal(0, recovered.Left);
        Assert.Equal(500, recovered.Width);
        window.Close();
    }

    [AvaloniaFact]
    public async Task FocusedCommandButtonsKeepEnterForTheirOwnActivation()
    {
        CountingActions actions = new();
        MainWindow window = new(actions);
        _ = window.AcceptDropForHost(new Core.InboundDropPayload(null, null, "private"), Now);

        bool commandHandled = await window.HandleShelfShortcutForHostAsync(Key.Enter, KeyModifiers.None, commandButtonFocused: true);
        bool pinHandled = await window.HandleShelfShortcutForHostAsync(Key.P, KeyModifiers.None, commandButtonFocused: true);
        bool cardHandled = await window.HandleShelfShortcutForHostAsync(Key.Enter, KeyModifiers.None, commandButtonFocused: false);

        Assert.False(commandHandled);
        Assert.True(pinHandled);
        Assert.True(cardHandled);
        Assert.True(window.Session.Items[0].IsPinned);
        Assert.Equal(1, actions.OpenCount);
        window.Close();
    }

    [AvaloniaFact]
    public void RejectedSyntheticDropShowsStatusAndDoesNotRenderPartialItems()
    {
        MainWindow window = new();
        string valid = Path.GetFullPath("valid.txt");

        Core.DropAdmissionResult result = window.AcceptDropForHost(
            new Core.InboundDropPayload([valid, "relative.txt"], null, null),
            DateTimeOffset.UtcNow);

        Assert.False(result.Accepted);
        Assert.NotEqual(string.Empty, window.FindControl<TextBlock>("DropStatus")?.Text);
        Assert.Empty(window.Session.Items);
        Assert.True(window.FindControl<TextBlock>("EmptyShelfMessage")?.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task LiveOutboundTransferBuilderPreservesOrderedFilesAndUsesUniversalFileFormat()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"drop-shelf-transfer-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(directory);
        string firstPath = Path.Combine(directory, "first.txt");
        string secondPath = Path.Combine(directory, "second.txt");
        await File.WriteAllTextAsync(firstPath, "first");
        await File.WriteAllTextAsync(secondPath, "second");
        MainWindow window = new();
        try
        {
            Core.ShelfItem first = Core.ShelfItem.Create(Guid.NewGuid(), "first.txt", Core.FileReferencePayload.Create(firstPath), Now);
            Core.ShelfItem second = Core.ShelfItem.Create(Guid.NewGuid(), "second.txt", Core.FileReferencePayload.Create(secondPath), Now, ordinal: 1);

            using OutboundDataTransfer owner = await window.BuildDataTransferForHostAsync([first, second]);
            IDataTransfer transfer = owner.Data;

            Assert.Equal([firstPath, secondPath], transfer.TryGetFiles()!.Select(file => file.Path.LocalPath));
            Assert.All(transfer.Items, item => Assert.Contains(DataFormat.File, item.Formats));
        }
        finally
        {
            window.Close();
            File.Delete(firstPath);
            File.Delete(secondPath);
            Directory.Delete(directory);
        }
    }

    [AvaloniaFact]
    public async Task RenderedBatchDragHandleCarriesSessionOrderedFilesIntoHostDragPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"drop-shelf-batch-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(directory);
        string firstPath = Path.Combine(directory, "first.txt");
        string secondPath = Path.Combine(directory, "second.txt");
        await File.WriteAllTextAsync(firstPath, "first");
        await File.WriteAllTextAsync(secondPath, "second");
        MainWindow window = new();
        try
        {
            Core.DropAdmissionResult result = window.AcceptDropForHost(
                new Core.InboundDropPayload([firstPath, secondPath], null, null), Now);

            Assert.True(result.Accepted);
            StackPanel renderedItems = window.FindControl<StackPanel>("ShelfItems")!;
            TextBlock handle = Assert.IsType<TextBlock>(Assert.Single(
                renderedItems.Children, control => control.Name == "FileBatchDragHandle"));
            Assert.Equal("Drag all 2 files", handle.Text);
            IReadOnlyList<Core.ShelfItem> orderedItems =
                Assert.IsAssignableFrom<IReadOnlyList<Core.ShelfItem>>(handle.Tag);
            Assert.Equal([firstPath, secondPath], orderedItems.Select(
                item => Assert.IsType<Core.FileReferencePayload>(item.Payload).Path));

            IReadOnlyList<string>? draggedPaths = null;
            await window.RunOutboundDragForHostAsync(orderedItems, transfer =>
            {
                draggedPaths = transfer.TryGetFiles()!.Select(file => file.Path.LocalPath).ToArray();
                return Task.CompletedTask;
            });

            Assert.Equal([firstPath, secondPath], draggedPaths);
        }
        finally
        {
            window.Close();
            File.Delete(firstPath);
            File.Delete(secondPath);
            Directory.Delete(directory);
        }
    }

    [AvaloniaFact]
    public async Task UnresolvableOutboundSelectionFailsWithoutReturningPartialTransferOrLeakingPath()
    {
        MainWindow window = new();
        string missingPath = Path.GetFullPath($"missing-{Guid.NewGuid():N}.txt");
        Core.ShelfItem missing = Core.ShelfItem.Create(Guid.NewGuid(), "missing.txt", Core.FileReferencePayload.Create(missingPath), Now);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => window.BuildDataTransferForHostAsync([missing]));

        Assert.DoesNotContain(missingPath, error.Message, StringComparison.Ordinal);
        Assert.Equal("One or more selected files are unavailable.", error.Message);
        window.Close();
    }

    [AvaloniaFact]
    public async Task LiveUrlTransferUsesUniversalTextAndRequestedNativePlatformFormat()
    {
        MainWindow window = new();
        Core.ShelfItem url = Core.ShelfItem.Create(
            Guid.NewGuid(), "example.test", Core.UrlPayload.Create("https://example.test/path"), Now);
        DataFormat<string> windowsUrl = DataFormat.CreateStringPlatformFormat("UniformResourceLocatorW");

        using OutboundDataTransfer owner = await window.BuildDataTransferForHostAsync([url], windowsUrl);
        IDataTransfer transfer = owner.Data;

        Assert.Equal("https://example.test/path", transfer.TryGetText());
        Assert.Equal("https://example.test/path", transfer.TryGetValue(windowsUrl));
        Assert.Contains(DataFormat.Text, transfer.Formats);
        Assert.Contains(windowsUrl, transfer.Formats);
        window.Close();
    }

    [AvaloniaFact]
    public void LiveInboundBridgeReadsExplicitNativeUrlAheadOfUniversalText()
    {
        DataFormat<string> macUrl = DataFormat.CreateStringPlatformFormat("public.url");
        DataTransferItem item = DataTransferItem.CreateText("ignored text");
        item.Set(macUrl, "https://example.test/native");
        DataTransfer transfer = new();
        transfer.Add(item);

        Core.InboundDropPayload payload = MainWindow.ReadDataTransferForHost(transfer, macUrl);
        Core.DropAdmissionResult result = new Core.DropAdmissionService(
            new Core.CanonicalDropConverter(), new Core.ShelfSession()).Admit(payload, Now);

        Assert.True(result.Accepted);
        Assert.Equal("https://example.test/native", Assert.IsType<Core.UrlPayload>(result.Items.Single().Payload).Url.AbsoluteUri);
    }

    [AvaloniaFact]
    public async Task LiveDirectoryRoundTripsWithDirectoryKindAndRemainsUsableUntilOwnerIsDisposed()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"drop-shelf-directory-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(directory);
        MainWindow window = new();
        try
        {
            IStorageFolder folder = await window.StorageProvider.TryGetFolderFromPathAsync(new Uri(directory))
                ?? throw new InvalidOperationException("Headless storage provider did not resolve the test directory.");
            DataTransfer inboundTransfer = new();
            inboundTransfer.Add(DataTransferItem.CreateFile(folder));

            Core.InboundDropPayload inbound = MainWindow.ReadDataTransferForHost(inboundTransfer, null);
            Core.ShelfItem item = Assert.Single(new Core.CanonicalDropConverter().ConvertInbound(inbound, Now).Items);
            Assert.Equal(Core.FileReferenceKind.Directory, Assert.IsType<Core.FileReferencePayload>(item.Payload).ReferenceKind);

            using OutboundDataTransfer outbound = await window.BuildDataTransferForHostAsync([item]);
            IStorageItem resolved = Assert.Single(outbound.Data.TryGetFiles()!);
            _ = Assert.IsAssignableFrom<IStorageFolder>(resolved);
            Assert.Equal(
                directory.TrimEnd(Path.DirectorySeparatorChar),
                resolved.Path.LocalPath.TrimEnd(Path.DirectorySeparatorChar));
        }
        finally
        {
            window.Close();
            Directory.Delete(directory);
        }
    }

    [AvaloniaFact]
    public async Task OutboundOwnerDisposesResolvedItemsIdempotentlyButNotBeforeDragCompletes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"drop-shelf-owned-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "owned");
        MainWindow window = new();
        try
        {
            Core.ShelfItem item = Core.ShelfItem.Create(
                Guid.NewGuid(), "owned.txt", Core.FileReferencePayload.Create(path), Now);
            OutboundDataTransfer owner = await window.BuildDataTransferForHostAsync([item]);
            IStorageItem resolved = Assert.Single(owner.Data.TryGetFiles()!);

            Assert.Equal(path, resolved.Path.LocalPath);
            Assert.False(owner.IsDisposed);
            owner.Dispose();
            owner.Dispose();
            Assert.True(owner.IsDisposed);
        }
        finally
        {
            window.Close();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task DragHostBoundaryMapsAnyFailureToOneGenericPrivateStatus()
    {
        MainWindow window = new();
        string privateText = "private failure detail";
        Core.ShelfItem item = Core.ShelfItem.Create(Guid.NewGuid(), "safe", Core.TextPayload.Create("safe"), Now);

        await window.RunOutboundDragForHostAsync([item], _ => throw new IOException(privateText));

        Assert.Equal("The drag could not be completed.", window.FindControl<TextBlock>("DropStatus")?.Text);
        Assert.DoesNotContain(privateText, window.FindControl<TextBlock>("DropStatus")?.Text ?? string.Empty, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public async Task InboundHostBoundaryRejectsFaultyMappingWithoutMutationOrPrivateStatus()
    {
        MainWindow window = new();
        string privateText = "private storage failure";
        string path = Path.Combine(Path.GetTempPath(), $"drop-shelf-inbound-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "inbound");
        IStorageFile file = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(path))
            ?? throw new InvalidOperationException("Headless storage provider did not resolve the test file.");
        DataTransfer transfer = new();
        transfer.Add(DataTransferItem.CreateFile(file));

        Core.DropAdmissionResult result = window.AcceptDataTransferForHost(
            transfer, null, Now, _ => throw new IOException(privateText));

        Assert.False(result.Accepted);
        Assert.Empty(window.Session.Items);
        Assert.Equal("That drop does not contain supported content.", window.FindControl<TextBlock>("DropStatus")?.Text);
        Assert.DoesNotContain(privateText, result.UserMessage, StringComparison.Ordinal);
        window.Close();
        file.Dispose();
        File.Delete(path);
    }

    private sealed class CountingActions : IShelfItemActions
    {
        public int OpenCount { get; private set; }
        public Task CopyAsync(IReadOnlyList<Core.ShelfItem> items) => Task.CompletedTask;
        public Task RevealAsync(IReadOnlyList<Core.ShelfItem> items) => Task.CompletedTask;

        public Task OpenAsync(IReadOnlyList<Core.ShelfItem> items)
        {
            OpenCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class DeferredSettingsStore(Task<Core.AppSettings> settings) : Core.ISettingsStore
    {
        public Task<Core.AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
            settings;

        public Task SaveSettingsAsync(Core.AppSettings value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FixedClock(DateTimeOffset now) : Core.IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class BlockingLoadShelfStore(Core.StoreSnapshot snapshot) : Core.IShelfStore
    {
        public TaskCompletionSource<bool> LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseLoad { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Core.StoreSnapshot> LoadAsync(CancellationToken cancellationToken = default)
        {
            _ = LoadStarted.TrySetResult(true);
            _ = await ReleaseLoad.Task.WaitAsync(cancellationToken);
            return snapshot;
        }

        public Task SaveAsync(Core.StoreSnapshot value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class BlockingSaveShelfStore : Core.IShelfStore
    {
        public TaskCompletionSource<bool> FirstSaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseFirstSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<Core.StoreSnapshot> Saved { get; } = [];

        public Task<Core.StoreSnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new Core.StoreSnapshot([], Core.AppSettings.Default));

        public async Task SaveAsync(Core.StoreSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Saved.Add(snapshot);
            if (Saved.Count == 1)
            {
                _ = FirstSaveStarted.TrySetResult(true);
                _ = await ReleaseFirstSave.Task.WaitAsync(cancellationToken);
            }
        }
    }

    private sealed class RecordingShelfStore(Core.StoreSnapshot snapshot) : Core.IShelfStore
    {
        public Core.StoreSnapshot? Saved { get; private set; }
        public Task<Core.StoreSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
        public Task SaveAsync(Core.StoreSnapshot value, CancellationToken cancellationToken = default)
        {
            Saved = value;
            return Task.CompletedTask;
        }
    }
}
