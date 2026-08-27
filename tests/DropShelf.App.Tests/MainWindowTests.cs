using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Xunit;

namespace DropShelf.App.Tests;

public sealed class MainWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

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
        TextBlock rendered = Assert.IsType<TextBlock>(Assert.Single(items.Children));
        Assert.Contains("report.txt", rendered.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(path, rendered.Text ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(path, window.FindControl<TextBlock>("DropStatus")?.Text ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("Added 1 item.", window.FindControl<TextBlock>("DropStatus")?.Text);
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
}
