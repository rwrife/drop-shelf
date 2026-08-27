using DropShelf.Core;

namespace DropShelf.Platform.Windows;

public static class WindowsDragDropFormats
{
    public const string FileDrop = "FileDrop";
    public const string UnicodeText = "UnicodeText";
    public const string Url = "UniformResourceLocatorW";
}

public sealed class WindowsDragDropAdapter : INativeDragDropAdapter
{
    public InboundDropPayload ReadInbound(IReadOnlyDictionary<string, object?> formats)
    {
        ArgumentNullException.ThrowIfNull(formats);
        IReadOnlyList<string>? files = formats.TryGetValue(WindowsDragDropFormats.FileDrop, out object? fileValue)
            ? ReadRequiredStrings(fileValue, WindowsDragDropFormats.FileDrop) : null;
        string? url = ReadString(formats, WindowsDragDropFormats.Url);
        string? text = ReadString(formats, WindowsDragDropFormats.UnicodeText);
        return new(files, url, text);
    }

    public NativeOutboundPayload CreateOutbound(IReadOnlyList<ShelfItem> items)
    {
        ValidateSelection(items);
        if (items.All(item => item.Payload is FileReferencePayload))
        {
            string[] paths = items.Select(item => ((FileReferencePayload)item.Payload).Path).ToArray();
            return new(new Dictionary<string, object> { [WindowsDragDropFormats.FileDrop] = paths });
        }
        if (items.Single().Payload is UrlPayload url)
        {
            string value = url.Url.AbsoluteUri;
            return new(new Dictionary<string, object>
            {
                [WindowsDragDropFormats.Url] = value,
                [WindowsDragDropFormats.UnicodeText] = value,
            });
        }
        string text = ((TextPayload)items.Single().Payload).Text;
        return new(new Dictionary<string, object> { [WindowsDragDropFormats.UnicodeText] = text });
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> formats, string key) =>
        !formats.TryGetValue(key, out object? value)
            ? null
            : value as string ?? throw Malformed(key);
    private static IReadOnlyList<string> ReadRequiredStrings(object? value, string key)
    {
        IReadOnlyList<string> values = value switch
        {
            string[] paths => paths,
            IReadOnlyList<string> paths => paths,
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
