using System.Globalization;
using System.Text;
using DropShelf.Core;

namespace DropShelf.App;

public interface IShelfItemActions
{
    Task CopyAsync(IReadOnlyList<ShelfItem> items);
    Task OpenAsync(IReadOnlyList<ShelfItem> items);
    Task RevealAsync(IReadOnlyList<ShelfItem> items);
}

public enum ShelfFocusTarget { DropSurface, Item, ExpandButton }

public sealed record ShelfCardViewModel(
    Guid Id,
    string TypeLabel,
    string DisplayLabel,
    string? SourceHint,
    string AgeLabel,
    bool IsPinned,
    bool IsMissing,
    bool IsSelected,
    string AccessibleName);

public sealed class ShelfViewModel
{
    public const int MaximumDisplayLabelLength = 80;
    public const int MaximumSourceHintLength = 64;

    private readonly ShelfSession session;
    private readonly IShelfItemActions actions;
    private readonly HashSet<Guid> selectedIds = [];
    private DateTimeOffset now;
    private Guid? focusBeforeCollapse;

    public ShelfViewModel(ShelfSession session, DateTimeOffset now, IShelfItemActions? actions = null)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.actions = actions ?? UnavailableShelfItemActions.Instance;
        this.now = now.ToUniversalTime();
        FocusedItemId = session.Items.Count == 0 ? null : session.Items[0].Id;
        FocusTarget = FocusedItemId is null ? ShelfFocusTarget.DropSurface : ShelfFocusTarget.Item;
        RefreshCards();
    }

    public IReadOnlyList<ShelfCardViewModel> Cards { get; private set; } = [];
    public Guid? FocusedItemId { get; private set; }
    public ShelfFocusTarget FocusTarget { get; private set; }
    public bool IsCollapsed { get; private set; }
    public bool IsActionInProgress { get; private set; }
    public string Announcement { get; private set; } = "Shelf ready.";

    public void Select(Guid id)
    {
        EnsureItem(id);
        selectedIds.Clear();
        _ = selectedIds.Add(id);
        FocusedItemId = id;
        FocusTarget = ShelfFocusTarget.Item;
        RefreshCards();
    }

    public void ToggleSelection(Guid id)
    {
        EnsureItem(id);
        if (!selectedIds.Remove(id))
        {
            _ = selectedIds.Add(id);
        }
        FocusedItemId = id;
        FocusTarget = ShelfFocusTarget.Item;
        Announcement = $"{selectedIds.Count} item{(selectedIds.Count == 1 ? string.Empty : "s")} selected.";
        RefreshCards();
    }

    public void ItemsAdded(IReadOnlyList<ShelfItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        session.AddRange(items);
        selectedIds.Clear();
        foreach (ShelfItem item in items)
        {
            _ = selectedIds.Add(item.Id);
        }
        FocusedItemId = items[^1].Id;
        FocusTarget = ShelfFocusTarget.Item;
        Announcement = $"Added {items.Count} item{(items.Count == 1 ? string.Empty : "s")}.";
        RefreshCards();
    }

    public void ExistingItemsAdded(IReadOnlyList<ShelfItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0 || items.Any(item => !session.Items.Any(existing => existing.Id == item.Id)))
        {
            throw new ArgumentException("Added items must already belong to the session.", nameof(items));
        }
        selectedIds.Clear();
        foreach (ShelfItem item in items)
        {
            _ = selectedIds.Add(item.Id);
        }
        FocusedItemId = items[^1].Id;
        FocusTarget = ShelfFocusTarget.Item;
        Announcement = $"Added {items.Count} item{(items.Count == 1 ? string.Empty : "s")}.";
        RefreshCards();
    }

    public void ItemsReplaced()
    {
        selectedIds.Clear();
        FocusedItemId = session.Items.Count == 0 ? null : session.Items[0].Id;
        FocusTarget = FocusedItemId is null ? ShelfFocusTarget.DropSurface : ShelfFocusTarget.Item;
        Announcement = session.Items.Count == 0
            ? "Shelf is empty. Drop files, text, or URLs here."
            : $"Loaded {session.Items.Count} item{(session.Items.Count == 1 ? string.Empty : "s")}.";
        RefreshCards();
    }

    public void SelectAll()
    {
        selectedIds.Clear();
        foreach (ShelfItem item in session.Items)
        {
            _ = selectedIds.Add(item.Id);
        }
        if (session.Items.Count > 0)
        {
            FocusedItemId ??= session.Items[0].Id;
            FocusTarget = ShelfFocusTarget.Item;
        }
        Announcement = $"{selectedIds.Count} items selected.";
        RefreshCards();
    }

    public int RemoveSelected()
    {
        int firstRemovedIndex = session.Items
            .Select((item, index) => (item, index))
            .Where(pair => selectedIds.Contains(pair.item.Id))
            .Select(pair => pair.index)
            .DefaultIfEmpty(-1)
            .Min();
        int removed = session.Remove(selectedIds);
        selectedIds.Clear();
        if (session.Items.Count == 0)
        {
            FocusedItemId = null;
            FocusTarget = ShelfFocusTarget.DropSurface;
            Announcement = "Shelf is empty. Drop files, text, or URLs here.";
        }
        else
        {
            int focusIndex = Math.Clamp(firstRemovedIndex, 0, session.Items.Count - 1);
            FocusedItemId = session.Items[focusIndex].Id;
            FocusTarget = ShelfFocusTarget.Item;
            Announcement = $"Removed {removed} item{(removed == 1 ? string.Empty : "s")}.";
        }
        RefreshCards();
        return removed;
    }

    public bool MoveSelected(int offset)
    {
        if (offset is not (-1 or 1) || selectedIds.Count == 0)
        {
            return false;
        }

        Guid[] orderedSelection = session.Items
            .Where(item => selectedIds.Contains(item.Id))
            .Select(item => item.Id)
            .ToArray();
        int firstIndex = session.Items.ToList().FindIndex(item => item.Id == orderedSelection[0]);
        int lastIndex = session.Items.ToList().FindIndex(item => item.Id == orderedSelection[^1]);
        if ((offset < 0 && firstIndex == 0) || (offset > 0 && lastIndex == session.Items.Count - 1))
        {
            return false;
        }

        IEnumerable<Guid> movementOrder = offset < 0 ? orderedSelection : orderedSelection.Reverse();
        foreach (Guid id in movementOrder)
        {
            int current = session.Items.ToList().FindIndex(item => item.Id == id);
            session.Reorder(id, current + offset);
        }
        string direction = offset < 0 ? "earlier" : "later";
        Announcement = $"Moved {orderedSelection.Length} selected item{(orderedSelection.Length == 1 ? string.Empty : "s")} {direction}.";
        RefreshCards();
        return true;
    }

    public void TogglePinned()
    {
        IReadOnlyList<ShelfItem> selected = SelectedItems();
        if (selected.Count == 0)
        {
            return;
        }
        bool pin = selected.Any(item => !item.IsPinned);
        foreach (ShelfItem item in selected)
        {
            session.SetPinned(item.Id, pin);
        }
        Announcement = $"{(pin ? "Pinned" : "Unpinned")} {selected.Count} item{(selected.Count == 1 ? string.Empty : "s")}.";
        RefreshCards();
    }

    public Task CopySelectedAsync() => RunActionAsync(actions.CopyAsync, "copy", "Copied");
    public Task OpenSelectedAsync() => RunActionAsync(actions.OpenAsync, "open", "Opened");
    public Task RevealSelectedAsync() => RunActionAsync(actions.RevealAsync, "reveal", "Revealed");

    public void SetCollapsed(bool collapsed)
    {
        if (IsCollapsed == collapsed)
        {
            return;
        }
        IsCollapsed = collapsed;
        if (collapsed)
        {
            focusBeforeCollapse = FocusedItemId;
            FocusedItemId = null;
            FocusTarget = ShelfFocusTarget.ExpandButton;
            Announcement = "Shelf collapsed. Expand button focused.";
        }
        else
        {
            FocusedItemId = focusBeforeCollapse is Guid id && session.Items.Any(item => item.Id == id)
                ? id
                : session.Items.Count == 0 ? null : session.Items[0].Id;
            FocusTarget = FocusedItemId is null ? ShelfFocusTarget.DropSurface : ShelfFocusTarget.Item;
            Announcement = "Shelf expanded.";
        }
    }

    public void Clear()
    {
        int count = session.Items.Count;
        _ = session.Remove(session.Items.Select(item => item.Id));
        selectedIds.Clear();
        FocusedItemId = null;
        FocusTarget = ShelfFocusTarget.DropSurface;
        Announcement = $"Cleared {count} item{(count == 1 ? string.Empty : "s")}. Shelf is empty.";
        RefreshCards();
    }

    public void SetNow(DateTimeOffset value)
    {
        now = value.ToUniversalTime();
        RefreshCards();
    }

    private void EnsureItem(Guid id)
    {
        if (!session.Items.Any(item => item.Id == id))
        {
            throw new ArgumentOutOfRangeException(nameof(id));
        }
    }

    private ShelfItem[] SelectedItems() => session.Items.Where(item => selectedIds.Contains(item.Id)).ToArray();

    private async Task RunActionAsync(Func<IReadOnlyList<ShelfItem>, Task> action, string verb, string completed)
    {
        if (IsActionInProgress)
        {
            Announcement = "Another item action is already in progress.";
            return;
        }
        IReadOnlyList<ShelfItem> selected = SelectedItems();
        if (selected.Count == 0)
        {
            Announcement = $"Select an item to {verb}.";
            return;
        }
        IsActionInProgress = true;
        try
        {
            await action(selected);
            Announcement = $"{completed} {selected.Count} item{(selected.Count == 1 ? string.Empty : "s")}.";
        }
        catch
        {
            Announcement = $"Could not {verb} the selected item{(selected.Count == 1 ? string.Empty : "s")}. Try again.";
        }
        finally
        {
            IsActionInProgress = false;
        }
    }

    private void RefreshCards() => Cards = session.Items.Select(item =>
    {
        string type = item.Kind switch
        {
            ShelfItemKind.FileReference => "File",
            ShelfItemKind.Text => "Text",
            ShelfItemKind.Url => "URL",
            _ => "Item",
        };
        bool missing = item.Payload is FileReferencePayload { Availability: FileAvailability.Missing };
        string age = FormatAge(now - item.CreatedAt);
        string states = string.Join(", ", new[]
        {
            item.IsPinned ? "pinned" : "not pinned",
            missing ? "unavailable" : null,
            selectedIds.Contains(item.Id) ? "selected" : "not selected",
        }.Where(value => value is not null));
        string displayLabel = SafeDisplayLabel(item);
        string? sourceHint = SafeSourceHint(item.SourceHint);
        string source = sourceHint is null ? string.Empty : $", from {sourceHint}";
        return new ShelfCardViewModel(item.Id, type, displayLabel, sourceHint, age, item.IsPinned,
            missing, selectedIds.Contains(item.Id), $"{type}, {displayLabel}{source}, {age}, {states}");
    }).ToArray();

    private static string SafeDisplayLabel(ShelfItem item) => item.Payload switch
    {
        TextPayload => "Text",
        UrlPayload url when url.Url.IsFile => SafeFileName(url.Url.LocalPath, "File URL"),
        UrlPayload url => SafeMetadata(url.Url.Host, MaximumDisplayLabelLength, "URL"),
        FileReferencePayload file => SafeFileName(file.Path, file.ReferenceKind == FileReferenceKind.Directory ? "Folder" : "File"),
        _ => "Item",
    };

    private static string SafeFileName(string path, string fallback)
    {
        string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return SafeMetadata(name, MaximumDisplayLabelLength, fallback);
    }

    private static string? SafeSourceHint(string? sourceHint)
    {
        if (string.IsNullOrWhiteSpace(sourceHint) ||
            sourceHint.Contains(Path.DirectorySeparatorChar) ||
            sourceHint.Contains(Path.AltDirectorySeparatorChar) ||
            sourceHint.Contains('\\') ||
            Uri.TryCreate(sourceHint, UriKind.Absolute, out _))
        {
            return null;
        }
        string safe = SafeMetadata(sourceHint, MaximumSourceHintLength, string.Empty);
        return safe.Length == 0 ? null : safe;
    }

    private static string SafeMetadata(string value, int maximumLength, string fallback)
    {
        StringBuilder safe = new(maximumLength);
        bool previousWasSpace = false;
        foreach (Rune rune in value.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }
            if (Rune.IsWhiteSpace(rune))
            {
                if (safe.Length > 0 && !previousWasSpace && safe.Length < maximumLength)
                {
                    _ = safe.Append(' ');
                }
                previousWasSpace = true;
                continue;
            }

            string text = rune.ToString();
            if (safe.Length + text.Length > maximumLength)
            {
                break;
            }
            _ = safe.Append(text);
            previousWasSpace = false;
        }

        string result = safe.ToString().TrimEnd();
        return result.Length == 0 ? fallback : result;
    }

    private static string FormatAge(TimeSpan age) => age < TimeSpan.FromMinutes(1)
        ? "just now"
        : age < TimeSpan.FromHours(1)
        ? $"{Math.Max(1, (int)age.TotalMinutes)} minutes ago"
        : age < TimeSpan.FromDays(1)
        ? $"{Math.Max(1, (int)age.TotalHours)} hours ago"
        : $"{Math.Max(1, (int)age.TotalDays)} days ago";

    private sealed class UnavailableShelfItemActions : IShelfItemActions
    {
        public static UnavailableShelfItemActions Instance { get; } = new();
        public Task CopyAsync(IReadOnlyList<ShelfItem> items) => Unavailable();
        public Task OpenAsync(IReadOnlyList<ShelfItem> items) => Unavailable();
        public Task RevealAsync(IReadOnlyList<ShelfItem> items) => Unavailable();
        private static Task Unavailable() => Task.FromException(new NotSupportedException("Native integration is unavailable."));
    }
}
