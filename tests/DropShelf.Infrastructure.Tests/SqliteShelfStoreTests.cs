using DropShelf.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DropShelf.Infrastructure.Tests;

public sealed class SqliteShelfStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "DropShelfTests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(directory, "shelf.db");

    [Fact]
    public async Task NewStoreCreatesSchemaAndReopensCompleteSessionAndSettings()
    {
        SqliteShelfStore store = new(DatabasePath);
        StoreSnapshot empty = await store.LoadAsync();
        Assert.Empty(empty.Items);
        Assert.Equal(AppSettings.Default, empty.Settings);
        DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        ShelfItem file = ShelfItem.Create(Guid.NewGuid(), "reference", FileReferencePayload.Create(Path.GetFullPath(Path.Combine(directory, "untouched.txt"))), now);
        ShelfItem text = ShelfItem.Create(Guid.NewGuid(), "note", TextPayload.Create("hello"), now, ordinal: 1);
        AppSettings settings = AppSettings.Create(DockEdge.Left, TimeSpan.FromDays(3), reduceMotion: true,
            globalShortcut: "Ctrl+Shift+D", expireOnExit: true);
        await store.SaveAsync(new StoreSnapshot([file, text], settings));

        StoreSnapshot reopened = await new SqliteShelfStore(DatabasePath).LoadAsync();
        Assert.Equal([file.Id, text.Id], reopened.Items.Select(item => item.Id));
        Assert.Equal(settings, reopened.Settings);
        Assert.False(File.Exists(Path.Combine(directory, "untouched.txt")));
    }

    [Fact]
    public async Task SettingsSaveIsDurableAndDoesNotReplaceItems()
    {
        SqliteShelfStore store = new(DatabasePath);
        ShelfItem item = ShelfItem.Create(Guid.NewGuid(), "note", TextPayload.Create("hello"), DateTimeOffset.UnixEpoch);
        await store.SaveAsync(new StoreSnapshot([item], AppSettings.Default));
        AppSettings changed = AppSettings.Create(DockEdge.Bottom, TimeSpan.FromHours(4), highContrast: true);
        await store.SaveSettingsAsync(changed);
        StoreSnapshot loaded = await store.LoadAsync();
        _ = Assert.Single(loaded.Items);
        Assert.Equal(changed, loaded.Settings);
    }

    [Fact]
    public async Task FutureSchemaFailsWithSpecificTypedError()
    {
        _ = Directory.CreateDirectory(directory);
        await using SqliteConnection connection = new($"Data Source={DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version=999;";
        _ = await command.ExecuteNonQueryAsync();
        ShelfStoreException error = await Assert.ThrowsAsync<ShelfStoreException>(() => new SqliteShelfStore(DatabasePath).LoadAsync());
        Assert.Equal(StoreErrorCode.IncompatibleSchema, error.Code);
    }

    [Fact]
    public async Task CorruptDatabaseAndInvalidRowsNeverReturnPartialData()
    {
        _ = Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(DatabasePath, "not sqlite"u8.ToArray());
        ShelfStoreException corrupt = await Assert.ThrowsAsync<ShelfStoreException>(() => new SqliteShelfStore(DatabasePath).LoadAsync());
        Assert.Equal(StoreErrorCode.CorruptData, corrupt.Code);

        File.Delete(DatabasePath);
        SqliteShelfStore store = new(DatabasePath);
        _ = await store.LoadAsync();
        await using SqliteConnection connection = new($"Data Source={DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO shelf_items(id,kind,display_name,created_at,last_used_at,is_pinned,ordinal,text_value) VALUES('bad',1,'ok','2026-01-01','2026-01-01',0,0,'valid-looking');";
        _ = await command.ExecuteNonQueryAsync();
        ShelfStoreException invalid = await Assert.ThrowsAsync<ShelfStoreException>(() => store.LoadAsync());
        Assert.Equal(StoreErrorCode.CorruptData, invalid.Code);
    }

    [Fact]
    public async Task EmptyVersionZeroDatabaseMigratesToCurrentVersion()
    {
        _ = Directory.CreateDirectory(directory);
        await using (SqliteConnection connection = new($"Data Source={DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
        }

        _ = await new SqliteShelfStore(DatabasePath).LoadAsync();
        await using SqliteConnection reopened = new($"Data Source={DatabasePath};Pooling=False");
        await reopened.OpenAsync();
        await using SqliteCommand command = reopened.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal(SqliteShelfStore.CurrentSchemaVersion, Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task VersionOneDatabaseMigratesShortcutSettingWithoutLosingItems()
    {
        _ = Directory.CreateDirectory(directory);
        Guid itemId = Guid.NewGuid();
        await using (SqliteConnection connection = new($"Data Source={DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE shelf_items (
                  id TEXT NOT NULL PRIMARY KEY, kind INTEGER NOT NULL, display_name TEXT NOT NULL, source_hint TEXT NULL,
                  created_at TEXT NOT NULL, last_used_at TEXT NOT NULL, is_pinned INTEGER NOT NULL, ordinal INTEGER NOT NULL UNIQUE,
                  text_value TEXT NULL, url_value TEXT NULL, title TEXT NULL, path_value TEXT NULL, file_kind INTEGER NULL,
                  size_bytes INTEGER NULL, modified_at TEXT NULL, availability INTEGER NULL);
                CREATE TABLE app_settings (
                  singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1), dock_edge INTEGER NOT NULL, retention_seconds INTEGER NOT NULL,
                  start_at_login INTEGER NOT NULL, reduce_motion INTEGER NOT NULL, high_contrast INTEGER NOT NULL);
                INSERT INTO app_settings VALUES(1,1,86400,0,0,0);
                INSERT INTO shelf_items(id,kind,display_name,created_at,last_used_at,is_pinned,ordinal,text_value)
                  VALUES($id,1,'note','1970-01-01T00:00:00.0000000+00:00','1970-01-01T00:00:00.0000000+00:00',0,0,'hello');
                PRAGMA user_version=1;
                """;
            _ = command.Parameters.AddWithValue("$id", itemId.ToString("D"));
            _ = await command.ExecuteNonQueryAsync();
        }

        AppSettings migrated = (await new SqliteShelfStore(DatabasePath).LoadAsync()).Settings;
        Assert.Equal("Ctrl+Alt+Space", migrated.GlobalShortcut);

        AppSettings changed = AppSettings.Create(globalShortcut: "Ctrl+Shift+Space");
        await new SqliteShelfStore(DatabasePath).SaveSettingsAsync(changed);
        StoreSnapshot reopened = await new SqliteShelfStore(DatabasePath).LoadAsync();
        Assert.Equal("Ctrl+Shift+Space", reopened.Settings.GlobalShortcut);
        Assert.Equal(itemId, reopened.Items.Single().Id);
    }

    [Fact]
    public async Task VersionTwoDatabaseMigratesExitPolicyWithoutLosingSettings()
    {
        _ = Directory.CreateDirectory(directory);
        await using (SqliteConnection connection = new($"Data Source={DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE shelf_items (
                  id TEXT NOT NULL PRIMARY KEY, kind INTEGER NOT NULL, display_name TEXT NOT NULL, source_hint TEXT NULL,
                  created_at TEXT NOT NULL, last_used_at TEXT NOT NULL, is_pinned INTEGER NOT NULL, ordinal INTEGER NOT NULL UNIQUE,
                  text_value TEXT NULL, url_value TEXT NULL, title TEXT NULL, path_value TEXT NULL, file_kind INTEGER NULL,
                  size_bytes INTEGER NULL, modified_at TEXT NULL, availability INTEGER NULL);
                CREATE TABLE app_settings (
                  singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1), dock_edge INTEGER NOT NULL, retention_seconds INTEGER NOT NULL,
                  start_at_login INTEGER NOT NULL, reduce_motion INTEGER NOT NULL, high_contrast INTEGER NOT NULL,
                  global_shortcut TEXT NOT NULL DEFAULT 'Ctrl+Alt+Space');
                INSERT INTO app_settings VALUES(1,3,604800,0,1,0,'Ctrl+Shift+D');
                PRAGMA user_version=2;
                """;
            _ = await command.ExecuteNonQueryAsync();
        }

        AppSettings migrated = (await new SqliteShelfStore(DatabasePath).LoadAsync()).Settings;

        Assert.Equal(DockEdge.Bottom, migrated.DockEdge);
        Assert.Equal(TimeSpan.FromDays(7), migrated.Retention);
        Assert.Equal("Ctrl+Shift+D", migrated.GlobalShortcut);
        Assert.False(migrated.ExpireOnExit);
    }

    [Fact]
    public async Task CurrentVersionDatabaseWithMalformedShapeIsRejected()
    {
        _ = Directory.CreateDirectory(directory);
        await using SqliteConnection connection = new($"Data Source={DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE shelf_items(id TEXT); CREATE TABLE app_settings(singleton INTEGER); PRAGMA user_version=1;";
        _ = await command.ExecuteNonQueryAsync();
        ShelfStoreException error = await Assert.ThrowsAsync<ShelfStoreException>(() => new SqliteShelfStore(DatabasePath).LoadAsync());
        Assert.Equal(StoreErrorCode.CorruptData, error.Code);
    }

    [Fact]
    public async Task CurrentVersionDatabaseWithUnexpectedSchemaObjectIsRejected()
    {
        SqliteShelfStore store = new(DatabasePath);
        _ = await store.LoadAsync();
        await using SqliteConnection connection = new($"Data Source={DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER unexpected AFTER INSERT ON shelf_items BEGIN SELECT 1; END;";
        _ = await command.ExecuteNonQueryAsync();
        ShelfStoreException error = await Assert.ThrowsAsync<ShelfStoreException>(() => store.LoadAsync());
        Assert.Equal(StoreErrorCode.CorruptData, error.Code);
    }

    [Theory]
    [InlineData("UPDATE shelf_items SET title='extra' WHERE ordinal=0;")]
    [InlineData("UPDATE shelf_items SET is_pinned=2 WHERE ordinal=0;")]
    [InlineData("UPDATE shelf_items SET ordinal=1 WHERE ordinal=0;")]
    public async Task InconsistentPayloadBooleanAndSparseOrdinalRowsAreRejected(string corruption)
    {
        SqliteShelfStore store = new(DatabasePath);
        ShelfItem item = ShelfItem.Create(Guid.NewGuid(), "note", TextPayload.Create("hello"), DateTimeOffset.UnixEpoch);
        await store.SaveAsync(new StoreSnapshot([item], AppSettings.Default));
        await using SqliteConnection connection = new($"Data Source={DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = corruption;
        _ = await command.ExecuteNonQueryAsync();
        ShelfStoreException error = await Assert.ThrowsAsync<ShelfStoreException>(() => store.LoadAsync());
        Assert.Equal(StoreErrorCode.CorruptData, error.Code);
    }

    [Fact]
    public async Task RefreshPersistenceAndExportDoNotMutateAnExistingSourceFile()
    {
        _ = Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "source.bin");
        byte[] bytes = [0, 1, 2, 255, 17];
        await File.WriteAllBytesAsync(sourcePath, bytes);
        DateTime timestamp = new(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(sourcePath, timestamp);
        FileAttributes attributes = File.GetAttributes(sourcePath);
        ShelfSession session = new([ShelfItem.Create(Guid.NewGuid(), "source", FileReferencePayload.Create(sourcePath), DateTimeOffset.UnixEpoch)]);
        _ = session.RefreshFileAvailability(new SystemFileSystem());
        SqliteShelfStore store = new(DatabasePath);
        await store.SaveAsync(new StoreSnapshot([.. session.Items], AppSettings.Default));
        StoreSnapshot loaded = await store.LoadAsync();
        _ = new MetadataJsonService().Export(loaded, DateTimeOffset.UnixEpoch);
        Assert.True(File.Exists(sourcePath));
        Assert.Equal(sourcePath, ((FileReferencePayload)loaded.Items.Single().Payload).Path);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(sourcePath));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(sourcePath));
        Assert.Equal(attributes, File.GetAttributes(sourcePath));
    }

    [Fact]
    public async Task FailedSaveRollsBackExistingSnapshot()
    {
        SqliteShelfStore store = new(DatabasePath);
        ShelfItem original = ShelfItem.Create(Guid.NewGuid(), "original", TextPayload.Create("one"), DateTimeOffset.UnixEpoch);
        await store.SaveAsync(new StoreSnapshot([original], AppSettings.Default));

        SqliteShelfStore interruptedStore = new(DatabasePath, (index, _) => index == 1
            ? ValueTask.FromException(new InvalidOperationException("Simulated interruption after the first insert."))
            : ValueTask.CompletedTask);
        ShelfItem second = ShelfItem.Create(Guid.NewGuid(), "second", TextPayload.Create("two"), DateTimeOffset.UnixEpoch, ordinal: 1);
        ShelfStoreException error = await Assert.ThrowsAsync<ShelfStoreException>(() => interruptedStore.SaveAsync(new StoreSnapshot([original, second], AppSettings.Default)));
        Assert.Equal(StoreErrorCode.PersistenceFailure, error.Code);
        Assert.Equal(original.Id, (await store.LoadAsync()).Items.Single().Id);
    }

    [Fact]
    public async Task ResetWaitsForInFlightSaveAndLeavesARecreatableEmptyStore()
    {
        string path = DatabasePath;
        TaskCompletionSource<bool> insertStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseInsert = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SqliteShelfStore store = new(path, async (index, token) =>
        {
            insertStarted.SetResult(true);
            _ = await releaseInsert.Task.WaitAsync(token);
        });
        StoreSnapshot snapshot = new(
            [ShelfItem.Create(Guid.NewGuid(), "private", TextPayload.Create("private"), DateTimeOffset.UtcNow)],
            AppSettings.Default);

        Task save = store.SaveAsync(snapshot);
        _ = await insertStarted.Task;
        Task reset = store.ResetAsync();
        Assert.False(reset.IsCompleted);

        _ = releaseInsert.TrySetResult(true);
        await save;
        await reset;

        StoreSnapshot loaded = await store.LoadAsync();
        Assert.Empty(loaded.Items);
        Assert.Equal(AppSettings.Default, loaded.Settings);
    }

    [Fact]
    public async Task ExplicitResetClearsOnlyAppMetadataAndRecreatesDefaults()
    {
        _ = Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "source.txt");
        await File.WriteAllTextAsync(sourcePath, "untouched");
        SqliteShelfStore store = new(DatabasePath);
        ShelfItem item = ShelfItem.Create(Guid.NewGuid(), "source",
            FileReferencePayload.Create(sourcePath), DateTimeOffset.UnixEpoch);
        await store.SaveAsync(new StoreSnapshot([item], AppSettings.Create(expireOnExit: true)));

        await store.ResetAsync();
        StoreSnapshot reset = await store.LoadAsync();

        Assert.Empty(reset.Items);
        Assert.Equal(AppSettings.Default, reset.Settings);
        Assert.Equal("untouched", await File.ReadAllTextAsync(sourcePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
