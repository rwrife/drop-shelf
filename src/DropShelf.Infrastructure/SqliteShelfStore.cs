using DropShelf.Core;
using Microsoft.Data.Sqlite;

namespace DropShelf.Infrastructure;

public enum StoreErrorCode { CorruptData, IncompatibleSchema, PersistenceFailure }

public sealed class ShelfStoreException(StoreErrorCode code, string message, Exception? innerException = null) : Exception(message, innerException)
{
    public StoreErrorCode Code { get; } = code;
}

public sealed class SqliteShelfStore : IShelfStore, ISettingsStore
{
    public const int CurrentSchemaVersion = 1;
    private readonly string connectionString;
    private readonly Func<int, CancellationToken, ValueTask>? beforeInsert;

    public SqliteShelfStore(string databasePath) : this(databasePath, null) { }

    internal SqliteShelfStore(string databasePath, Func<int, CancellationToken, ValueTask>? beforeInsert)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("A database path is required.", nameof(databasePath));
        }

        string fullPath = Path.GetFullPath(databasePath);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("The database path has no directory.", nameof(databasePath)));
        connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        this.beforeInsert = beforeInsert;
    }

    public async Task<StoreSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            AppSettings settings = await ReadSettingsAsync(connection, transaction, cancellationToken);
            List<ShelfItem> items = await ReadItemsAsync(connection, transaction, cancellationToken);
            ShelfSession validated = new(items);
            await transaction.CommitAsync(cancellationToken);
            return new([.. validated.Items], settings);
        }
        catch (ShelfStoreException) { throw; }
        catch (ShelfValidationException exception) { throw new ShelfStoreException(StoreErrorCode.CorruptData, "Stored shelf data is invalid.", exception); }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException or InvalidCastException or OverflowException or FormatException)
        { throw new ShelfStoreException(StoreErrorCode.CorruptData, "The local store could not be read safely.", exception); }
    }

    public async Task SaveAsync(StoreSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ShelfSession validated = new(snapshot.Items);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await ExecuteAsync(connection, transaction, "DELETE FROM shelf_items;", cancellationToken);
            for (int index = 0; index < validated.Items.Count; index++)
            {
                if (beforeInsert is not null)
                {
                    await beforeInsert(index, cancellationToken);
                }
                await InsertItemAsync(connection, transaction, validated.Items[index], cancellationToken);
            }

            await WriteSettingsAsync(connection, transaction, snapshot.Settings, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (ShelfStoreException) { throw; }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException or OverflowException)
        { throw new ShelfStoreException(StoreErrorCode.PersistenceFailure, "The local store could not be saved atomically.", exception); }
    }

    public async Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => (await LoadAsync(cancellationToken)).Settings;

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await WriteSettingsAsync(connection, transaction, settings, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (ShelfStoreException) { throw; }
        catch (SqliteException exception) { throw new ShelfStoreException(StoreErrorCode.PersistenceFailure, "Settings could not be saved atomically.", exception); }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(token);
        return connection;
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using SqliteCommand versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        int version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture);
        if (version > CurrentSchemaVersion)
        {
            throw new ShelfStoreException(StoreErrorCode.IncompatibleSchema, "The local store was created by a newer application version.");
        }

        if (version == CurrentSchemaVersion)
        {
            await ValidateSchemaShapeAsync(connection, token);
            return;
        }

        if (version != 0)
        {
            throw new ShelfStoreException(StoreErrorCode.CorruptData, "The local store schema version is unsupported.");
        }

        await using (SqliteCommand emptyCommand = connection.CreateCommand())
        {
            emptyCommand.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';";
            if (Convert.ToInt64(await emptyCommand.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture) != 0)
            {
                throw new ShelfStoreException(StoreErrorCode.CorruptData, "A version-zero local store must be empty.");
            }
        }

        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        const string sql = """
            CREATE TABLE shelf_items (
              id TEXT NOT NULL PRIMARY KEY, kind INTEGER NOT NULL, display_name TEXT NOT NULL, source_hint TEXT NULL,
              created_at TEXT NOT NULL, last_used_at TEXT NOT NULL, is_pinned INTEGER NOT NULL, ordinal INTEGER NOT NULL UNIQUE,
              text_value TEXT NULL, url_value TEXT NULL, title TEXT NULL, path_value TEXT NULL, file_kind INTEGER NULL,
              size_bytes INTEGER NULL, modified_at TEXT NULL, availability INTEGER NULL);
            CREATE TABLE app_settings (
              singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1), dock_edge INTEGER NOT NULL, retention_seconds INTEGER NOT NULL,
              start_at_login INTEGER NOT NULL, reduce_motion INTEGER NOT NULL, high_contrast INTEGER NOT NULL);
            PRAGMA user_version = 1;
            """;
        await ExecuteAsync(connection, transaction, sql, token);
        await WriteSettingsAsync(connection, transaction, AppSettings.Default, token);
        await transaction.CommitAsync(token);
        await ValidateSchemaShapeAsync(connection, token);
    }

    private static async Task ValidateSchemaShapeAsync(SqliteConnection connection, CancellationToken token)
    {
        Dictionary<string, string> expected = new(StringComparer.Ordinal)
        {
            ["shelf_items"] = """
                CREATE TABLE shelf_items (
                  id TEXT NOT NULL PRIMARY KEY, kind INTEGER NOT NULL, display_name TEXT NOT NULL, source_hint TEXT NULL,
                  created_at TEXT NOT NULL, last_used_at TEXT NOT NULL, is_pinned INTEGER NOT NULL, ordinal INTEGER NOT NULL UNIQUE,
                  text_value TEXT NULL, url_value TEXT NULL, title TEXT NULL, path_value TEXT NULL, file_kind INTEGER NULL,
                  size_bytes INTEGER NULL, modified_at TEXT NULL, availability INTEGER NULL)
                """,
            ["app_settings"] = """
                CREATE TABLE app_settings (
                  singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1), dock_edge INTEGER NOT NULL, retention_seconds INTEGER NOT NULL,
                  start_at_login INTEGER NOT NULL, reduce_motion INTEGER NOT NULL, high_contrast INTEGER NOT NULL)
                """,
        };

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT type,name,sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            string type = reader.GetString(0);
            string name = reader.GetString(1);
            if (!string.Equals(type, "table", StringComparison.Ordinal)
                || !expected.Remove(name, out string? expectedSql)
                || reader.IsDBNull(2)
                || !string.Equals(NormalizeSql(reader.GetString(2)), NormalizeSql(expectedSql), StringComparison.OrdinalIgnoreCase))
            {
                throw new ShelfStoreException(StoreErrorCode.CorruptData, "The local store schema definition is invalid.");
            }
        }

        if (expected.Count != 0)
        {
            throw new ShelfStoreException(StoreErrorCode.CorruptData, "The local store schema definition is incomplete.");
        }

        static string NormalizeSql(string sql)
        {
            return string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
    }

    private static async Task<List<ShelfItem>> ReadItemsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,kind,display_name,source_hint,created_at,last_used_at,is_pinned,ordinal,text_value,url_value,title,path_value,file_kind,size_bytes,modified_at,availability FROM shelf_items ORDER BY ordinal,id LIMIT 1001;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(token);
        List<ShelfItem> result = [];
        while (await reader.ReadAsync(token))
        {
            if (result.Count >= DomainLimits.MaxItems)
            {
                throw new ShelfStoreException(StoreErrorCode.CorruptData, "The local store contains too many items.");
            }

            Guid id = Guid.ParseExact(reader.GetString(0), "D");
            ShelfItemKind kind = CheckedEnum<ShelfItemKind>(ReadInt32(reader, 1));
            ShelfPayload payload = kind switch
            {
                ShelfItemKind.Text when Null(reader, 9, 10, 11, 12, 13, 14, 15) => TextPayload.Create(reader.IsDBNull(8) ? null : reader.GetString(8)),
                ShelfItemKind.Url when Null(reader, 8, 11, 12, 13, 14, 15) => UrlPayload.Create(reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10)),
                ShelfItemKind.FileReference when Null(reader, 8, 9, 10) && !reader.IsDBNull(12) && !reader.IsDBNull(15) => FileReferencePayload.Create(
                    reader.IsDBNull(11) ? null : reader.GetString(11), CheckedEnum<FileReferenceKind>(ReadInt32(reader, 12)),
                    reader.IsDBNull(13) ? null : ReadInt64(reader, 13), reader.IsDBNull(14) ? null : ParseTimestamp(reader.GetString(14)),
                    CheckedEnum<FileAvailability>(ReadInt32(reader, 15))),
                _ => throw new ShelfStoreException(StoreErrorCode.CorruptData, "A stored item payload is inconsistent."),
            };
            int ordinal = ReadInt32(reader, 7);
            if (ordinal != result.Count)
            {
                throw new ShelfStoreException(StoreErrorCode.CorruptData, "Stored item ordinals must be dense and zero-based.");
            }
            result.Add(ShelfItem.Create(id, reader.GetString(2), payload,
                ParseTimestamp(reader.GetString(4)), ParseTimestamp(reader.GetString(5)), ReadBoolean(reader, 6), ordinal,
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return result;
    }

    private static async Task<AppSettings> ReadSettingsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT dock_edge,retention_seconds,start_at_login,reduce_motion,high_contrast FROM app_settings WHERE singleton=1;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(token);
        return !await reader.ReadAsync(token)
            ? throw new ShelfStoreException(StoreErrorCode.CorruptData, "Stored settings are missing.")
            : AppSettings.Create(CheckedEnum<DockEdge>(ReadInt32(reader, 0)), TimeSpan.FromSeconds(ReadInt64(reader, 1)), ReadBoolean(reader, 2), ReadBoolean(reader, 3), ReadBoolean(reader, 4));
    }

    private static async Task InsertItemAsync(SqliteConnection connection, SqliteTransaction transaction, ShelfItem item, CancellationToken token)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO shelf_items(id,kind,display_name,source_hint,created_at,last_used_at,is_pinned,ordinal,text_value,url_value,title,path_value,file_kind,size_bytes,modified_at,availability) VALUES($id,$kind,$display,$source,$created,$used,$pinned,$ordinal,$text,$url,$title,$path,$fileKind,$size,$modified,$availability);";
        Add(command, "$id", item.Id.ToString("D")); Add(command, "$kind", (int)item.Kind); Add(command, "$display", item.DisplayName);
        Add(command, "$source", item.SourceHint); Add(command, "$created", item.CreatedAt.ToString("O")); Add(command, "$used", item.LastUsedAt.ToString("O"));
        Add(command, "$pinned", item.IsPinned); Add(command, "$ordinal", item.Ordinal);
        if (item.Payload is TextPayload text)
        {
            Add(command, "$text", text.Text);
        }
        else
        {
            Add(command, "$text", null);
        }

        if (item.Payload is UrlPayload url) { Add(command, "$url", url.Url.AbsoluteUri); Add(command, "$title", url.Title); } else { Add(command, "$url", null); Add(command, "$title", null); }
        if (item.Payload is FileReferencePayload file)
        { Add(command, "$path", file.Path); Add(command, "$fileKind", (int)file.ReferenceKind); Add(command, "$size", file.SizeBytes); Add(command, "$modified", file.ModifiedAt?.ToString("O")); Add(command, "$availability", (int)file.Availability); }
        else { Add(command, "$path", null); Add(command, "$fileKind", null); Add(command, "$size", null); Add(command, "$modified", null); Add(command, "$availability", null); }
        _ = await command.ExecuteNonQueryAsync(token);
    }

    private static async Task WriteSettingsAsync(SqliteConnection connection, SqliteTransaction transaction, AppSettings settings, CancellationToken token)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO app_settings VALUES(1,$edge,$retention,$startup,$motion,$contrast) ON CONFLICT(singleton) DO UPDATE SET dock_edge=$edge,retention_seconds=$retention,start_at_login=$startup,reduce_motion=$motion,high_contrast=$contrast;";
        Add(command, "$edge", (int)settings.DockEdge); Add(command, "$retention", checked((long)settings.Retention.TotalSeconds));
        Add(command, "$startup", settings.StartAtLogin); Add(command, "$motion", settings.ReduceMotion); Add(command, "$contrast", settings.HighContrast);
        _ = await command.ExecuteNonQueryAsync(token);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken token)
    { await using SqliteCommand command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; _ = await command.ExecuteNonQueryAsync(token); }
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static bool Null(SqliteDataReader reader, params int[] ordinals) => ordinals.All(reader.IsDBNull);
    private static bool ReadBoolean(SqliteDataReader reader, int ordinal) => !string.Equals(reader.GetDataTypeName(ordinal), "INTEGER", StringComparison.OrdinalIgnoreCase)
            ? throw new ShelfStoreException(StoreErrorCode.CorruptData, "Stored boolean data is invalid.")
            : reader.GetInt64(ordinal) switch
            {
                0 => false,
                1 => true,
                _ => throw new ShelfStoreException(StoreErrorCode.CorruptData, "Stored boolean data is invalid."),
            };
    private static int ReadInt32(SqliteDataReader reader, int ordinal)
    {
        long value = ReadInt64(reader, ordinal);
        return value is >= int.MinValue and <= int.MaxValue
            ? (int)value
            : throw new ShelfStoreException(StoreErrorCode.CorruptData, "Stored integer data is invalid.");
    }
    private static long ReadInt64(SqliteDataReader reader, int ordinal) =>
        string.Equals(reader.GetDataTypeName(ordinal), "INTEGER", StringComparison.OrdinalIgnoreCase)
            ? reader.GetInt64(ordinal)
            : throw new ShelfStoreException(StoreErrorCode.CorruptData, "Stored integer data is invalid.");
    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.TryParseExact(value, "O", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset timestamp)
            ? timestamp
            : throw new ShelfStoreException(StoreErrorCode.CorruptData, "Stored timestamp data is invalid.");
    private static T CheckedEnum<T>(int value) where T : struct, Enum => Enum.IsDefined(typeof(T), value) ? (T)(object)value : throw new ShelfStoreException(StoreErrorCode.CorruptData, "Stored enum data is invalid.");
}
