namespace DropShelf.Core;

public enum ShelfItemKind { FileReference, Text, Url }
public enum FileReferenceKind { File, Directory }
public enum FileAvailability { Available, Missing }
public abstract record ShelfPayload;

public sealed record FileReferencePayload : ShelfPayload
{
    private FileReferencePayload(string path, FileReferenceKind referenceKind, long? sizeBytes, DateTimeOffset? modifiedAt, FileAvailability availability) =>
        (Path, ReferenceKind, SizeBytes, ModifiedAt, Availability) = (path, referenceKind, sizeBytes, modifiedAt, availability);
    public string Path { get; }
    public FileReferenceKind ReferenceKind { get; }
    public long? SizeBytes { get; }
    public DateTimeOffset? ModifiedAt { get; }
    public FileAvailability Availability { get; }

    public static FileReferencePayload Create(string? path, FileReferenceKind referenceKind = FileReferenceKind.File,
        long? sizeBytes = null, DateTimeOffset? modifiedAt = null, FileAvailability availability = FileAvailability.Available)
    {
        string original = path ?? throw Input.Error(ValidationErrorCode.Required, nameof(Path), "A non-empty path is required.");
        return original.Length == 0
            ? throw Input.Error(ValidationErrorCode.Required, nameof(Path), "A non-empty path is required.")
            : original.Length > DomainLimits.MaxPathLength
            ? throw Input.Error(ValidationErrorCode.TooLong, nameof(Path), $"The path exceeds the {DomainLimits.MaxPathLength} character limit.")
            : !Enum.IsDefined(referenceKind) || !Enum.IsDefined(availability)
            ? throw Input.Error(ValidationErrorCode.InvalidPayload, nameof(referenceKind), "File metadata contains an invalid value.")
            : original.Contains('\0') || !System.IO.Path.IsPathFullyQualified(original)
            ? throw Input.Error(ValidationErrorCode.InvalidPath, nameof(Path), "A fully qualified path without null characters is required.")
            : sizeBytes < 0
            ? throw Input.Error(ValidationErrorCode.InvalidPayload, nameof(SizeBytes), "File size cannot be negative.")
            : new(original, referenceKind, sizeBytes, modifiedAt?.ToUniversalTime(), availability);
    }

    public FileReferencePayload WithAvailability(FileAvailability availability) =>
        Create(Path, ReferenceKind, SizeBytes, ModifiedAt, availability);
}

public sealed record TextPayload : ShelfPayload
{
    private TextPayload(string text) => Text = text;
    public string Text { get; }
    public static TextPayload Create(string? text) => new(Input.Required(text, DomainLimits.MaxTextLength, nameof(Text)));
}

public sealed record UrlPayload : ShelfPayload
{
    private UrlPayload(Uri url, string? title) => (Url, Title) = (url, title);
    public Uri Url { get; }
    public string? Title { get; }

    public static UrlPayload Create(string? url, string? title = null)
    {
        string normalized = Input.Required(url, DomainLimits.MaxUrlLength, nameof(Url), true);
        return !Uri.TryCreate(normalized, UriKind.Absolute, out Uri? parsed)
            ? throw Input.Error(ValidationErrorCode.InvalidUrl, nameof(Url), "A valid absolute URL is required.")
            : parsed.Scheme is not ("http" or "https" or "file")
            ? throw Input.Error(ValidationErrorCode.UnsupportedUrlScheme, nameof(Url), "Only HTTP, HTTPS, and file URLs are supported.")
            : string.IsNullOrEmpty(parsed.Host) && !parsed.IsFile
            ? throw Input.Error(ValidationErrorCode.InvalidUrl, nameof(Url), "A valid absolute URL is required.")
            : !string.IsNullOrEmpty(parsed.UserInfo)
            ? throw Input.Error(ValidationErrorCode.InvalidUrl, nameof(Url), "URLs containing credentials are not accepted.")
            : new(parsed, Input.Optional(title, DomainLimits.MaxDisplayLength, nameof(Title)));
    }
}

public sealed record ShelfItem
{
    private ShelfItem(Guid id, ShelfItemKind kind, string displayName, string? sourceHint, DateTimeOffset createdAt,
        DateTimeOffset lastUsedAt, bool isPinned, int ordinal, ShelfPayload payload) =>
        (Id, Kind, DisplayName, SourceHint, CreatedAt, LastUsedAt, IsPinned, Ordinal, Payload) =
        (id, kind, displayName, sourceHint, createdAt, lastUsedAt, isPinned, ordinal, payload);
    public Guid Id { get; }
    public ShelfItemKind Kind { get; }
    public string DisplayName { get; }
    public string? SourceHint { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastUsedAt { get; }
    public bool IsPinned { get; }
    public int Ordinal { get; }
    public ShelfPayload Payload { get; }

    public static ShelfItem Create(Guid id, string? displayName, ShelfPayload payload, DateTimeOffset createdAt,
        DateTimeOffset? lastUsedAt = null, bool isPinned = false, int ordinal = 0, string? sourceHint = null)
    {
        if (id == Guid.Empty)
        {
            throw Input.Error(ValidationErrorCode.InvalidIdentifier, nameof(Id), "A non-empty item identifier is required.");
        }

        ArgumentNullException.ThrowIfNull(payload);
        DateTimeOffset created = createdAt.ToUniversalTime();
        DateTimeOffset used = (lastUsedAt ?? createdAt).ToUniversalTime();
        if (used < created)
        {
            throw Input.Error(ValidationErrorCode.InvalidTimestamp, nameof(LastUsedAt), "Last-used time cannot precede creation time.");
        }

        if (ordinal is < 0 or >= DomainLimits.MaxItems)
        {
            throw Input.Error(ValidationErrorCode.InvalidOrdinal, nameof(Ordinal), "Ordinal is outside the supported range.");
        }

        ShelfItemKind kind = payload switch
        {
            FileReferencePayload => ShelfItemKind.FileReference,
            TextPayload => ShelfItemKind.Text,
            UrlPayload => ShelfItemKind.Url,
            _ => throw Input.Error(ValidationErrorCode.InvalidPayload, nameof(Payload), "Unknown payload type."),
        };
        return new(id, kind, Input.Required(displayName, DomainLimits.MaxDisplayLength, nameof(DisplayName), true),
            Input.Optional(sourceHint, DomainLimits.MaxSourceHintLength, nameof(SourceHint)), created, used, isPinned, ordinal, payload);
    }

    internal ShelfItem WithOrdinal(int ordinal) => new(Id, Kind, DisplayName, SourceHint, CreatedAt, LastUsedAt, IsPinned, ordinal, Payload);
    internal ShelfItem WithPinned(bool pinned) => new(Id, Kind, DisplayName, SourceHint, CreatedAt, LastUsedAt, pinned, Ordinal, Payload);
    internal ShelfItem WithPayload(ShelfPayload payload) => new(Id, Kind, DisplayName, SourceHint, CreatedAt, LastUsedAt, IsPinned, Ordinal, payload);
}

public static class DomainLimits
{
    public const int MaxItems = 1000;
    public const int MaxTextLength = 64 * 1024;
    public const int MaxDisplayLength = 256;
    public const int MaxSourceHintLength = 256;
    public const int MaxPathLength = 4096;
    public const int MaxUrlLength = 8192;
    public const int MaxExportBytes = 16 * 1024 * 1024;
}
