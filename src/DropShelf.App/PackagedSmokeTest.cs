using DropShelf.Core;
using DropShelf.Infrastructure;

namespace DropShelf.App;

public static class PackagedSmokeTest
{
    private static readonly Guid SmokeItemId = Guid.Parse("01c9c26a-f137-4df8-9bad-6dc2774b1baf");

    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        string root = Path.Combine(Path.GetTempPath(), $"drop-shelf-package-smoke-{Guid.NewGuid():N}");
        int result = 0;
        try
        {
            string databasePath = Path.Combine(root, "shelf.db");
            ShelfSession session = new();
            session.Add(ShelfItem.Create(
                SmokeItemId,
                "Packaged smoke item",
                TextPayload.Create("Explicit packaged smoke-test text"),
                DateTimeOffset.UtcNow));

            using (SqliteShelfStore store = new(databasePath))
            {
                ShelfDataService service = new(store);
                await service.SaveAsync(session, AppSettings.Default, cancellationToken);
            }

            using (SqliteShelfStore reopenedStore = new(databasePath))
            {
                ShelfDataService reopenedService = new(reopenedStore);
                ShelfLoadResult restored = await reopenedService.LoadAsync(cancellationToken);
                if (restored.Snapshot.Items.Count != 1 || restored.Snapshot.Items[0].Id != SmokeItemId)
                {
                    throw new InvalidOperationException("The packaged persistence restore check failed.");
                }

                ShelfSession restoredSession = new(restored.Snapshot.Items);
                _ = await reopenedService.ClearAllAsync(restoredSession, cancellationToken);
            }

            using SqliteShelfStore finalStore = new(databasePath);
            StoreSnapshot cleared = await finalStore.LoadAsync(cancellationToken);
            if (cleared.Items.Count != 0 || cleared.Settings != AppSettings.Default)
            {
                throw new InvalidOperationException("The packaged clear check failed.");
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"PACKAGED_SMOKE_TEST_FAIL {exception.GetType().Name}");
            result = 1;
        }

        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("PACKAGED_SMOKE_TEST_CLEANUP_FAILED");
            result = 1;
        }

        if (result == 0)
        {
            Console.WriteLine("PACKAGED_SMOKE_TEST_PASS add=1 restore=1 clear=1 cleanup=1");
        }
        return result;
    }
}
