using DropShelf.Core;

namespace DropShelf.Platform.macOS;

public static class MacPasteboardFormats
{
    public const string FileUrl = "public.file-url";
    public const string LegacyFileNames = "NSFilenamesPboardType";
    public const string PlainText = "public.utf8-plain-text";
    public const string Url = "public.url";
}

public sealed class MacPasteboardAdapter : INativeDragDropAdapter
{
    public InboundDropPayload ReadInbound(IReadOnlyDictionary<string, object?> formats)
    {
        ArgumentNullException.ThrowIfNull(formats);
        IReadOnlyList<string>? files = null;
        if (formats.TryGetValue(MacPasteboardFormats.FileUrl, out object? urls))
        {
            files = ReadRequiredStrings(urls, MacPasteboardFormats.FileUrl).Select(ParseFileUrl).ToArray();
        }

        if (files is null && formats.TryGetValue(MacPasteboardFormats.LegacyFileNames, out object? names))
        {
            files = ReadRequiredStrings(names, MacPasteboardFormats.LegacyFileNames);
        }

        string? url = ReadString(formats, MacPasteboardFormats.Url);
        string? text = ReadString(formats, MacPasteboardFormats.PlainText);
        return new(files, url, text);
    }

    public NativeOutboundPayload CreateOutbound(IReadOnlyList<ShelfItem> items)
    {
        ValidateSelection(items);
        if (items.All(item => item.Payload is FileReferencePayload))
        {
            string[] paths = items.Select(item => ((FileReferencePayload)item.Payload).Path).ToArray();
            string[] urls = paths.Select(path => new Uri(path).AbsoluteUri).ToArray();
            return new(new Dictionary<string, object>
            {
                [MacPasteboardFormats.FileUrl] = urls,
                [MacPasteboardFormats.LegacyFileNames] = paths,
            });
        }
        if (items.Single().Payload is UrlPayload url)
        {
            string value = url.Url.AbsoluteUri;
            return new(new Dictionary<string, object>
            {
                [MacPasteboardFormats.Url] = value,
                [MacPasteboardFormats.PlainText] = value,
            });
        }
        string text = ((TextPayload)items.Single().Payload).Text;
        return new(new Dictionary<string, object> { [MacPasteboardFormats.PlainText] = text });
    }

    private static string ParseFileUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.IsFile
        ? uri.LocalPath : value;
    private static string? ReadString(IReadOnlyDictionary<string, object?> formats, string key) =>
        !formats.TryGetValue(key, out object? value)
            ? null
            : value as string ?? throw Malformed(key);
    private static IReadOnlyList<string> ReadRequiredStrings(object? value, string key)
    {
        IReadOnlyList<string> values = value switch
        {
            string[] strings => strings,
            IReadOnlyList<string> strings => strings,
            _ => throw Malformed(key),
        };
        return values.Count is 0 or > DomainLimits.MaxItems ? throw Malformed(key) : values;
    }
    private static ShelfValidationException Malformed(string field) =>
        new(ValidationErrorCode.InvalidPayload, field, "The advertised drag format is malformed.");
    private static void ValidateSelection(IReadOnlyList<ShelfItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0 || (items.Count > 1 && !items.All(item => item.Payload is FileReferencePayload)))
        {
            throw new ArgumentException("A non-empty selection containing either files or one text/URL item is required.", nameof(items));
        }
    }
}
