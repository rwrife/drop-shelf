using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DropShelf.Core;

public sealed class MetadataJsonService
{
    public const int CurrentSchemaVersion = 2;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "This is an injectable service boundary.")]
    public byte[] Export(StoreSnapshot snapshot, DateTimeOffset exportedAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Items.Count > DomainLimits.MaxItems)
        {
            throw Input.Error(ValidationErrorCode.TooLong, nameof(snapshot), "The export contains too many items.");
        }

        MetadataDocument document = new(CurrentSchemaVersion, exportedAt.ToUniversalTime(), SettingsDto.From(snapshot.Settings),
            snapshot.Items.OrderBy(item => item.Ordinal).Select(ItemDto.From).ToArray());
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        return data.Length > DomainLimits.MaxExportBytes
            ? throw Input.Error(ValidationErrorCode.TooLong, nameof(snapshot), "The export exceeds the size limit.")
            : data;
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "This is an injectable service boundary.")]
    public StoreSnapshot Import(ReadOnlySpan<byte> json)
    {
        if (json.Length is 0 or > DomainLimits.MaxExportBytes)
        {
            throw Input.Error(ValidationErrorCode.InvalidExport, nameof(json), "Export data is empty or exceeds the size limit.");
        }

        try
        {
            RejectDuplicateProperties(json);
            using JsonDocument parsed = JsonDocument.Parse(json.ToArray());
            int schemaVersion = ReadSchemaVersion(parsed.RootElement);

            return schemaVersion switch
            {
                1 => ImportVersionOne(json),
                CurrentSchemaVersion => ImportCurrent(json),
                _ => throw Input.Error(ValidationErrorCode.InvalidExport, nameof(schemaVersion), "The export schema version is unsupported."),
            };
        }
        catch (ShelfValidationException) { throw; }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or FormatException or OverflowException)
        {
            throw new ShelfValidationException(ValidationErrorCode.InvalidExport, nameof(json), "Export data is malformed.");
        }
    }

    private static int ReadSchemaVersion(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("schemaVersion", out JsonElement schemaElement) &&
        schemaElement.TryGetInt32(out int schemaVersion)
            ? schemaVersion
            : throw Input.Error(ValidationErrorCode.InvalidExport, nameof(root), "The export schema version is required.");

    private static StoreSnapshot ImportCurrent(ReadOnlySpan<byte> json)
    {
        MetadataDocument document = JsonSerializer.Deserialize<MetadataDocument>(json, Options)
            ?? throw Input.Error(ValidationErrorCode.InvalidExport, nameof(json), "Export data is empty.");
        return Validate(document.ExportedAt, document.Settings?.ToDomain(), document.Items);
    }

    private static StoreSnapshot ImportVersionOne(ReadOnlySpan<byte> json)
    {
        MetadataDocumentV1 document = JsonSerializer.Deserialize<MetadataDocumentV1>(json, Options)
            ?? throw Input.Error(ValidationErrorCode.InvalidExport, nameof(json), "Export data is empty.");
        return Validate(document.ExportedAt, document.Settings?.ToDomain(), document.Items);
    }

    private static StoreSnapshot Validate(DateTimeOffset exportedAt, AppSettings? settings, ItemDto[]? items)
    {
        if (exportedAt == default)
        {
            throw Input.Error(ValidationErrorCode.InvalidExport, nameof(exportedAt), "The export timestamp is required.");
        }
        if (items is null || settings is null || items.Length > DomainLimits.MaxItems)
        {
            throw Input.Error(ValidationErrorCode.InvalidExport, nameof(items), "Export content is missing or too large.");
        }

        ShelfSession session = new(items.Select(item => item.ToDomain()));
        return new([.. session.Items], settings);
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> json)
    {
        using JsonDocument document = JsonDocument.Parse(json.ToArray());
        Check(document.RootElement);

        static void Check(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                HashSet<string> names = new(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new JsonException("Duplicate JSON property names are not allowed.");
                    }
                    Check(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in element.EnumerateArray())
                {
                    Check(child);
                }
            }
        }
    }

    private sealed record MetadataDocument([property: JsonRequired] int SchemaVersion, [property: JsonRequired] DateTimeOffset ExportedAt,
        [property: JsonRequired] SettingsDto Settings, [property: JsonRequired] ItemDto[] Items);

    private sealed record MetadataDocumentV1([property: JsonRequired] int SchemaVersion, [property: JsonRequired] DateTimeOffset ExportedAt,
        [property: JsonRequired] SettingsDtoV1 Settings, [property: JsonRequired] ItemDto[] Items);

    private sealed record SettingsDto([property: JsonRequired] DockEdge DockEdge, [property: JsonRequired] long RetentionSeconds,
        [property: JsonRequired] bool StartAtLogin, [property: JsonRequired] bool ReduceMotion, [property: JsonRequired] bool HighContrast,
        [property: JsonRequired] string GlobalShortcut, [property: JsonRequired] bool ExpireOnExit)
    {
        public static SettingsDto From(AppSettings value) => new(value.DockEdge, checked((long)value.Retention.TotalSeconds),
            value.StartAtLogin, value.ReduceMotion, value.HighContrast, value.GlobalShortcut, value.ExpireOnExit);
        public AppSettings ToDomain() => AppSettings.Create(DockEdge, TimeSpan.FromSeconds(RetentionSeconds), StartAtLogin,
            ReduceMotion, HighContrast, GlobalShortcut, ExpireOnExit);
    }

    private sealed record SettingsDtoV1([property: JsonRequired] DockEdge DockEdge, [property: JsonRequired] long RetentionSeconds,
        [property: JsonRequired] bool StartAtLogin, [property: JsonRequired] bool ReduceMotion, [property: JsonRequired] bool HighContrast)
    {
        public AppSettings ToDomain() => AppSettings.Create(DockEdge, TimeSpan.FromSeconds(RetentionSeconds), StartAtLogin,
            ReduceMotion, HighContrast);
    }

    private sealed record ItemDto([property: JsonRequired] Guid Id, [property: JsonRequired] ShelfItemKind Kind,
        [property: JsonRequired] string DisplayName, string? SourceHint, [property: JsonRequired] DateTimeOffset CreatedAt,
        [property: JsonRequired] DateTimeOffset LastUsedAt, [property: JsonRequired] bool IsPinned, [property: JsonRequired] int Ordinal,
        string? Text, string? Url, string? Title, string? Path,
        FileReferenceKind? FileKind, long? SizeBytes, DateTimeOffset? ModifiedAt, FileAvailability? Availability)
    {
        public static ItemDto From(ShelfItem item) => item.Payload switch
        {
            TextPayload text => new(item.Id, item.Kind, item.DisplayName, item.SourceHint, item.CreatedAt, item.LastUsedAt, item.IsPinned, item.Ordinal,
                text.Text, null, null, null, null, null, null, null),
            UrlPayload url => new(item.Id, item.Kind, item.DisplayName, item.SourceHint, item.CreatedAt, item.LastUsedAt, item.IsPinned, item.Ordinal,
                null, url.Url.AbsoluteUri, url.Title, null, null, null, null, null),
            FileReferencePayload file => new(item.Id, item.Kind, item.DisplayName, item.SourceHint, item.CreatedAt, item.LastUsedAt, item.IsPinned, item.Ordinal,
                null, null, null, file.Path, file.ReferenceKind, file.SizeBytes, file.ModifiedAt, file.Availability),
            _ => throw Input.Error(ValidationErrorCode.InvalidPayload, nameof(item.Payload), "Unknown payload type."),
        };

        public ShelfItem ToDomain()
        {
            ShelfPayload payload = Kind switch
            {
                ShelfItemKind.Text when Url is null && Title is null && Path is null && FileKind is null && SizeBytes is null && ModifiedAt is null && Availability is null => TextPayload.Create(Text),
                ShelfItemKind.Url when Text is null && Path is null && FileKind is null && SizeBytes is null && ModifiedAt is null && Availability is null => UrlPayload.Create(Url, Title),
                ShelfItemKind.FileReference when Text is null && Url is null && Title is null && FileKind.HasValue && Availability.HasValue =>
                    FileReferencePayload.Create(Path, FileKind.Value, SizeBytes, ModifiedAt, Availability.Value),
                _ => throw Input.Error(ValidationErrorCode.InvalidExport, nameof(Kind), "Item fields do not match the declared kind."),
            };
            return ShelfItem.Create(Id, DisplayName, payload, CreatedAt, LastUsedAt, IsPinned, Ordinal, SourceHint);
        }
    }
}
