using DropShelf.Core;
using Xunit;

namespace DropShelf.App.Tests;

public sealed class ShelfViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CardsExposeSafeMetadataAndSelectionWithoutSensitivePayloads()
    {
        string fullPath = Path.GetFullPath(Path.Combine("private", "report.txt"));
        ShelfSession session = new([
            Item("report.txt", FileReferencePayload.Create(fullPath, availability: FileAvailability.Missing), source: "Finder"),
            Item("Research note", TextPayload.Create("secret body"), ordinal: 1, source: "Notes"),
            Item("Example", UrlPayload.Create("https://user.example/private?q=secret"), ordinal: 2,
                source: Path.GetFullPath(Path.Combine("private", "source-app.exe"))),
        ]);
        ShelfViewModel viewModel = new(session, Now);

        viewModel.Select(session.Items[0].Id);
        viewModel.ToggleSelection(session.Items[1].Id);

        Assert.Equal(3, viewModel.Cards.Count);
        Assert.Equal("File", viewModel.Cards[0].TypeLabel);
        Assert.Equal("report.txt", viewModel.Cards[0].DisplayLabel);
        Assert.Equal("Finder", viewModel.Cards[0].SourceHint);
        Assert.Equal("just now", viewModel.Cards[0].AgeLabel);
        Assert.True(viewModel.Cards[0].IsMissing);
        Assert.True(viewModel.Cards[0].IsSelected);
        Assert.True(viewModel.Cards[1].IsSelected);
        Assert.Equal("URL", viewModel.Cards[2].TypeLabel);
        Assert.Null(viewModel.Cards[2].SourceHint);
        string rendered = string.Join(' ', viewModel.Cards.Select(card => card.AccessibleName));
        Assert.DoesNotContain(fullPath, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("secret body", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("private?q=secret", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAndRemoveRestoreFocusDeterministically()
    {
        ShelfSession session = new([Item("one", TextPayload.Create("one")), Item("two", TextPayload.Create("two"), ordinal: 1)]);
        ShelfViewModel viewModel = new(session, Now);
        Guid first = session.Items[0].Id;
        Guid second = session.Items[1].Id;

        ShelfItem third = Item("three", TextPayload.Create("three"), ordinal: 2);
        viewModel.ItemsAdded([third]);
        Assert.Equal(third.Id, viewModel.FocusedItemId);

        viewModel.Select(second);
        _ = viewModel.RemoveSelected();
        Assert.Equal(third.Id, viewModel.FocusedItemId);

        viewModel.Select(third.Id);
        _ = viewModel.RemoveSelected();
        Assert.Equal(first, viewModel.FocusedItemId);

        viewModel.Select(first);
        _ = viewModel.RemoveSelected();
        Assert.Null(viewModel.FocusedItemId);
        Assert.Equal("Shelf is empty. Drop files, text, or URLs here.", viewModel.Announcement);
    }

    [Fact]
    public async Task CommandsReorderPinAndInvokeExplicitActionsInShelfOrder()
    {
        ShelfSession session = new([Item("one", TextPayload.Create("one")), Item("two", TextPayload.Create("two"), ordinal: 1)]);
        RecordingActions actions = new();
        ShelfViewModel viewModel = new(session, Now, actions);
        Guid first = session.Items[0].Id;
        Guid second = session.Items[1].Id;

        viewModel.Select(second);
        Assert.True(viewModel.MoveSelected(-1));
        Assert.Equal([second, first], session.Items.Select(item => item.Id));
        Assert.Equal(second, viewModel.FocusedItemId);

        viewModel.TogglePinned();
        Assert.True(session.Items[0].IsPinned);
        Assert.Contains("Pinned", viewModel.Announcement, StringComparison.Ordinal);

        viewModel.ToggleSelection(first);
        await viewModel.CopySelectedAsync();
        await viewModel.OpenSelectedAsync();
        await viewModel.RevealSelectedAsync();
        Assert.Equal([second, first], actions.Copied.Select(item => item.Id));
        Assert.Equal([second, first], actions.Opened.Select(item => item.Id));
        Assert.Equal([second, first], actions.Revealed.Select(item => item.Id));
    }

    [Fact]
    public void ReorderMovesEverySelectedItemAsAnOrderedGroup()
    {
        ShelfSession session = new([
            Item("one", TextPayload.Create("one")),
            Item("two", TextPayload.Create("two"), ordinal: 1),
            Item("three", TextPayload.Create("three"), ordinal: 2),
            Item("four", TextPayload.Create("four"), ordinal: 3),
        ]);
        ShelfViewModel viewModel = new(session, Now);
        Guid[] original = session.Items.Select(item => item.Id).ToArray();
        viewModel.Select(original[1]);
        viewModel.ToggleSelection(original[2]);

        Assert.True(viewModel.MoveSelected(-1));
        Assert.Equal([original[1], original[2], original[0], original[3]], session.Items.Select(item => item.Id));
        Assert.Equal("Moved 2 selected items earlier.", viewModel.Announcement);

        Assert.True(viewModel.MoveSelected(1));
        Assert.Equal(original, session.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task FailedActionIsRecoverablePrivateAndRetainsSelection()
    {
        ShelfSession session = new([Item("safe label", TextPayload.Create("private text"))]);
        RecordingActions actions = new() { Failure = new IOException("private failure details") };
        ShelfViewModel viewModel = new(session, Now, actions);
        viewModel.Select(session.Items[0].Id);

        await viewModel.CopySelectedAsync();

        Assert.Equal("Could not copy the selected item. Try again.", viewModel.Announcement);
        Assert.True(viewModel.Cards[0].IsSelected);
        Assert.DoesNotContain("private", viewModel.Announcement, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentActionsAreRejectedUntilTheFirstActionCompletes()
    {
        ShelfSession session = new([Item("safe", TextPayload.Create("private text"))]);
        DelayedActions actions = new();
        ShelfViewModel viewModel = new(session, Now, actions);
        viewModel.Select(session.Items[0].Id);

        Task copy = viewModel.CopySelectedAsync();
        Assert.True(viewModel.IsActionInProgress);
        Task open = viewModel.OpenSelectedAsync();

        Assert.Equal(1, actions.CallCount);
        Assert.Equal("Another item action is already in progress.", viewModel.Announcement);
        actions.Complete();
        await Task.WhenAll(copy, open);

        Assert.False(viewModel.IsActionInProgress);
        Assert.Equal("Copied 1 item.", viewModel.Announcement);
    }

    [Fact]
    public void CollapseAndClearHaveDeterministicReachableFocusTargets()
    {
        ShelfSession session = new([Item("one", TextPayload.Create("one")), Item("two", TextPayload.Create("two"), ordinal: 1)]);
        ShelfViewModel viewModel = new(session, Now);
        viewModel.Select(session.Items[1].Id);

        viewModel.SetCollapsed(true);
        Assert.True(viewModel.IsCollapsed);
        Assert.Null(viewModel.FocusedItemId);
        Assert.Equal(ShelfFocusTarget.ExpandButton, viewModel.FocusTarget);

        viewModel.SetCollapsed(false);
        Assert.Equal(session.Items[1].Id, viewModel.FocusedItemId);
        Assert.Equal(ShelfFocusTarget.Item, viewModel.FocusTarget);

        viewModel.Clear();
        Assert.Empty(session.Items);
        Assert.Equal(ShelfFocusTarget.DropSurface, viewModel.FocusTarget);
        Assert.Equal("Cleared 2 items. Shelf is empty.", viewModel.Announcement);
    }

    [Theory]
    [InlineData(DockEdge.Left, false, 0, 100, 420, 700)]
    [InlineData(DockEdge.Right, false, 580, 100, 420, 700)]
    [InlineData(DockEdge.Top, true, 956, 0, 44, 44)]
    [InlineData(DockEdge.Bottom, true, 956, 756, 44, 44)]
    public void DockGeometryRecoversIntoChangedMonitorWorkArea(
        DockEdge edge, bool collapsed, int x, int y, int width, int height)
    {
        ShelfBounds staleBounds = new(3000, 1800, 420, 900);
        ShelfBounds workArea = new(0, 0, 1000, 800);

        ShelfBounds recovered = ShelfGeometry.Recover(staleBounds, workArea, edge, collapsed);

        Assert.Equal(new ShelfBounds(x, y, width, height), recovered);
        Assert.True(recovered.Left >= workArea.Left && recovered.Top >= workArea.Top);
        Assert.True(recovered.Right <= workArea.Right && recovered.Bottom <= workArea.Bottom);
    }

    [Theory]
    [InlineData(DockEdge.Left, false)]
    [InlineData(DockEdge.Top, false)]
    [InlineData(DockEdge.Right, true)]
    [InlineData(DockEdge.Bottom, true)]
    public void DockGeometryNeverExceedsAConstrainedWorkArea(DockEdge edge, bool collapsed)
    {
        ShelfBounds workArea = new(10, 20, 30, 20);

        ShelfBounds recovered = ShelfGeometry.Recover(new ShelfBounds(500, 500, 900, 900), workArea, edge, collapsed);

        Assert.True(recovered.Width > 0 && recovered.Height > 0);
        Assert.True(recovered.Left >= workArea.Left && recovered.Top >= workArea.Top);
        Assert.True(recovered.Right <= workArea.Right && recovered.Bottom <= workArea.Bottom);
    }

    [Theory]
    [InlineData(DockEdge.Left, 480, 700, 480, 700)]
    [InlineData(DockEdge.Right, 480, 700, 480, 700)]
    [InlineData(DockEdge.Top, 800, 500, 800, 500)]
    [InlineData(DockEdge.Bottom, 800, 500, 800, 500)]
    public void ExpandedGeometryPreservesUserSelectedThickness(
        DockEdge edge, int previousWidth, int previousHeight, int expectedWidth, int expectedHeight)
    {
        ShelfBounds recovered = ShelfGeometry.Recover(
            new ShelfBounds(100, 100, previousWidth, previousHeight),
            new ShelfBounds(0, 0, 1200, 900), edge, collapsed: false);

        Assert.Equal(expectedWidth, recovered.Width);
        Assert.Equal(expectedHeight, recovered.Height);
    }

    [Fact]
    public void CollapsedGeometryKeepsAFortyFourLogicalPixelTargetAtTwoHundredPercentScaling()
    {
        ShelfBounds recovered = ShelfGeometry.Recover(
            new ShelfBounds(100, 100, 420, 700),
            new ShelfBounds(0, 0, 1000, 800),
            DockEdge.Right,
            collapsed: true,
            renderScaling: 2);

        Assert.Equal(88, recovered.Width);
        Assert.Equal(912, recovered.Left);
        Assert.Equal(44, recovered.Width / 2);
    }

    [Fact]
    public void CardMetadataRemovesControlsBidiFormattingAndExcessLength()
    {
        string hostileName = new string('x', 120) + "\n\u202Ehidden.txt";
        string path = Path.GetFullPath(hostileName);
        ShelfItem item = Item(hostileName, FileReferencePayload.Create(path), source: "Finder\n\u202Espoofed");

        ShelfCardViewModel card = Assert.Single(new ShelfViewModel(new ShelfSession([item]), Now).Cards);

        Assert.True(card.DisplayLabel.Length <= ShelfViewModel.MaximumDisplayLabelLength);
        Assert.True(card.SourceHint?.Length <= ShelfViewModel.MaximumSourceHintLength);
        Assert.DoesNotContain('\n', card.DisplayLabel);
        Assert.DoesNotContain('\u202E', card.DisplayLabel);
        Assert.DoesNotContain('\n', card.SourceHint ?? string.Empty);
        Assert.DoesNotContain('\u202E', card.SourceHint ?? string.Empty);
    }

    private static ShelfItem Item(string label, ShelfPayload payload, int ordinal = 0, string? source = null) =>
        ShelfItem.Create(Guid.NewGuid(), label, payload, Now, ordinal: ordinal, sourceHint: source);

    private sealed class RecordingActions : IShelfItemActions
    {
        public Exception? Failure { get; init; }
        public IReadOnlyList<ShelfItem> Copied { get; private set; } = [];
        public IReadOnlyList<ShelfItem> Opened { get; private set; } = [];
        public IReadOnlyList<ShelfItem> Revealed { get; private set; } = [];

        public Task CopyAsync(IReadOnlyList<ShelfItem> items)
        {
            Copied = [.. items];
            return Complete();
        }

        public Task OpenAsync(IReadOnlyList<ShelfItem> items)
        {
            Opened = [.. items];
            return Complete();
        }

        public Task RevealAsync(IReadOnlyList<ShelfItem> items)
        {
            Revealed = [.. items];
            return Complete();
        }

        private Task Complete() => Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }

    private sealed class DelayedActions : IShelfItemActions
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }
        public Task CopyAsync(IReadOnlyList<ShelfItem> items) => Delay();
        public Task OpenAsync(IReadOnlyList<ShelfItem> items) => Delay();
        public Task RevealAsync(IReadOnlyList<ShelfItem> items) => Delay();
        public void Complete() => completion.SetResult();

        private Task Delay()
        {
            CallCount++;
            return completion.Task;
        }
    }
}
