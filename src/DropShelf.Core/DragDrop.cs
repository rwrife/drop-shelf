namespace DropShelf.Core;

/// <summary>Neutral snapshot of native formats. Precedence is files, explicit URL, then text.</summary>
public sealed record InboundDropPayload(
    IReadOnlyList<string>? FilePaths,
    string? Url,
    string? Text,
    IReadOnlyList<FileReferenceKind>? FileKinds = null);
public enum InboundDropFormat { FileList, Url, Text }
public sealed record DropConversionResult(InboundDropFormat SelectedFormat, IReadOnlyList<ShelfItem> Items);

public sealed class CanonicalDropConverter
{
    private readonly Func<Guid> createId = Guid.NewGuid;
    public DropConversionResult ConvertInbound(InboundDropPayload payload, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.FileKinds is not null && payload.FileKinds.Count != (payload.FilePaths?.Count ?? 0))
        {
            throw Input.Error(ValidationErrorCode.InvalidPath, nameof(payload.FileKinds), "File kind metadata does not match the file list.");
        }

        if (payload.FilePaths is { Count: > 0 })
        {
            if (payload.FilePaths.Count > DomainLimits.MaxItems)
            {
                throw Input.Error(ValidationErrorCode.TooLong, nameof(payload.FilePaths), "The drop contains too many items.");
            }

            ShelfItem[] files = payload.FilePaths.Select((path, index) => CreateFile(
                path, payload.FileKinds?[index] ?? FileReferenceKind.File, createdAt, index)).ToArray();
            return new(InboundDropFormat.FileList, files);
        }
        if (payload.Url is not null)
        {
            UrlPayload url = UrlPayload.Create(payload.Url);
            return new(InboundDropFormat.Url, [CreateItem(UrlDisplayName(url.Url), url, createdAt, 0)]);
        }
        if (payload.Text is not null)
        {
            string candidate = payload.Text.Trim();
            if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? parsed) && parsed.Scheme is "http" or "https" or "file")
            {
                UrlPayload url = UrlPayload.Create(candidate);
                return new(InboundDropFormat.Text, [CreateItem(UrlDisplayName(url.Url), url, createdAt, 0)]);
            }
            TextPayload text = TextPayload.Create(payload.Text);
            return new(InboundDropFormat.Text, [CreateItem("Text", text, createdAt, 0)]);
        }
        throw Input.Error(ValidationErrorCode.Required, nameof(payload), "The drop did not contain files, a URL, or plain text.");
    }

    private ShelfItem CreateFile(string path, FileReferenceKind referenceKind, DateTimeOffset createdAt, int ordinal)
    {
        FileReferencePayload file = FileReferencePayload.Create(path, referenceKind);
        string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return CreateItem(string.IsNullOrWhiteSpace(name) ? "File" : name, file, createdAt, ordinal);
    }
    private ShelfItem CreateItem(string displayName, ShelfPayload payload, DateTimeOffset createdAt, int ordinal) =>
        ShelfItem.Create(createId(), displayName, payload, createdAt, ordinal: ordinal);
    private static string UrlDisplayName(Uri url) => url.IsFile
        ? Path.GetFileName(url.LocalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name ? name : "File URL"
        : url.Host;
}

public sealed record DropAdmissionResult(bool Accepted, IReadOnlyList<ShelfItem> Items, string UserMessage)
{
    public static DropAdmissionResult Rejected(string message) => new(false, [], message);
}

public sealed class DropAdmissionService(CanonicalDropConverter converter, ShelfSession session)
{
    public DropAdmissionResult Admit(InboundDropPayload payload, DateTimeOffset createdAt)
    {
        try
        {
            DropConversionResult conversion = converter.ConvertInbound(payload, createdAt);
            session.AddRange(conversion.Items);
            return new(true, conversion.Items, $"Added {conversion.Items.Count} {(conversion.Items.Count == 1 ? "item" : "items")}.");
        }
        catch (ShelfValidationException exception)
        {
            string message = "That drop does not contain supported content.";
            if (exception.Code == ValidationErrorCode.TooLong)
            {
                message = "That drop is too large for the shelf.";
            }
            else if (exception.Code == ValidationErrorCode.UnsupportedUrlScheme)
            {
                message = "That URL type is not supported.";
            }
            else if (exception.Code == ValidationErrorCode.InvalidUrl)
            {
                message = "That URL is malformed.";
            }
            else if (exception.Code == ValidationErrorCode.InvalidPath)
            {
                message = "One of the dropped file references is invalid.";
            }

            return DropAdmissionResult.Rejected(message);
        }
    }
}

/// <summary>Platform-neutral outbound format bag for a native host integration boundary.</summary>
public sealed record NativeOutboundPayload(IReadOnlyDictionary<string, object> Formats);

public interface INativeDragDropAdapter
{
    InboundDropPayload ReadInbound(IReadOnlyDictionary<string, object?> formats);
    NativeOutboundPayload CreateOutbound(IReadOnlyList<ShelfItem> items);
}
