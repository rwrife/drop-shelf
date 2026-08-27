namespace DropShelf.Core;

public sealed class ShelfSession
{
    private readonly List<ShelfItem> items;
    public ShelfSession(IEnumerable<ShelfItem>? items = null)
    {
        this.items = items?.OrderBy(item => item.Ordinal).ThenBy(item => item.Id).ToList() ?? [];
        Items = this.items.AsReadOnly();
        if (this.items.Count > DomainLimits.MaxItems)
        {
            throw Input.Error(ValidationErrorCode.TooLong, nameof(Items), "The shelf contains too many items.");
        }

        if (this.items.Select(item => item.Id).Distinct().Count() != this.items.Count)
        {
            throw Input.Error(ValidationErrorCode.DuplicateIdentifier, nameof(Items), "Shelf item identifiers must be unique.");
        }

        NormalizeOrdinals();
    }
    public IReadOnlyList<ShelfItem> Items { get; }

    public void Add(ShelfItem item, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (items.Count >= DomainLimits.MaxItems)
        {
            throw Input.Error(ValidationErrorCode.TooLong, nameof(Items), "The shelf item limit has been reached.");
        }

        if (items.Any(existing => existing.Id == item.Id))
        {
            throw Input.Error(ValidationErrorCode.DuplicateIdentifier, nameof(item.Id), "The item already exists.");
        }

        int insertionIndex = index ?? items.Count;
        if (insertionIndex < 0 || insertionIndex > items.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        items.Insert(insertionIndex, item);
        NormalizeOrdinals();
    }

    public void AddRange(IReadOnlyList<ShelfItem> newItems)
    {
        ArgumentNullException.ThrowIfNull(newItems);
        if (newItems.Count == 0)
        {
            throw Input.Error(ValidationErrorCode.Required, nameof(newItems), "At least one item is required.");
        }

        if (newItems.Count > DomainLimits.MaxItems - items.Count)
        {
            throw Input.Error(ValidationErrorCode.TooLong, nameof(Items), "The shelf item limit would be exceeded.");
        }

        if (newItems.Any(item => item is null) || newItems.Select(item => item.Id).Distinct().Count() != newItems.Count ||
            newItems.Any(item => items.Any(existing => existing.Id == item.Id)))
        {
            throw Input.Error(ValidationErrorCode.DuplicateIdentifier, nameof(newItems), "The items must be non-null and uniquely identified.");
        }

        items.AddRange(newItems);
        NormalizeOrdinals();
    }

    public void Reorder(Guid id, int newIndex)
    {
        int currentIndex = IndexOf(id);
        if (newIndex < 0 || newIndex >= items.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(newIndex));
        }

        ShelfItem item = items[currentIndex];
        items.RemoveAt(currentIndex);
        items.Insert(newIndex, item);
        NormalizeOrdinals();
    }

    public int Remove(IEnumerable<Guid> ids)
    {
        HashSet<Guid> requested = ids?.ToHashSet() ?? throw new ArgumentNullException(nameof(ids));
        int removed = items.RemoveAll(item => requested.Contains(item.Id));
        NormalizeOrdinals();
        return removed;
    }

    public void SetPinned(Guid id, bool pinned)
    {
        int index = IndexOf(id);
        items[index] = items[index].WithPinned(pinned);
    }

    public int Expire(DateTimeOffset now, TimeSpan retention)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);

        DateTimeOffset boundary = now.ToUniversalTime() - retention;
        int removed = items.RemoveAll(item => !item.IsPinned && item.LastUsedAt <= boundary);
        NormalizeOrdinals();
        return removed;
    }

    public int Expire(IClock clock, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(settings);
        return Expire(clock.UtcNow, settings.Retention);
    }

    public int RefreshFileAvailability(IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        int changed = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Payload is not FileReferencePayload file)
            {
                continue;
            }

            bool exists = file.ReferenceKind == FileReferenceKind.Directory ? fileSystem.DirectoryExists(file.Path) : fileSystem.FileExists(file.Path);
            FileAvailability availability = exists ? FileAvailability.Available : FileAvailability.Missing;
            if (file.Availability == availability)
            {
                continue;
            }

            items[i] = items[i].WithPayload(file.WithAvailability(availability));
            changed++;
        }
        return changed;
    }

    private int IndexOf(Guid id)
    {
        int index = items.FindIndex(item => item.Id == id);
        return index >= 0 ? index : throw Input.Error(ValidationErrorCode.ItemNotFound, nameof(id), "The shelf item was not found.");
    }

    private void NormalizeOrdinals()
    {
        for (int i = 0; i < items.Count; i++)
        {
            items[i] = items[i].WithOrdinal(i);
        }
    }
}
