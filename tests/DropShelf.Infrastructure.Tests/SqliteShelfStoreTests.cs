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
        AppSettings settings = AppSettings.Create(DockEdge.Left, TimeSpan.FromDays(3), reduceMotion: true);
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

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
