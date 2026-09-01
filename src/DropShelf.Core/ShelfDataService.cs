namespace DropShelf.Core;

public sealed record ShelfLoadResult(StoreSnapshot Snapshot, int ExpiredItems);
public sealed record PolicyChangeResult(AppSettings Settings, int AffectedItems);

public sealed class ShelfDataService(IShelfStore store, IClock? clock = null, MetadataJsonService? metadata = null)
{
    private readonly IShelfStore store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IClock clock = clock ?? new SystemClock();
    private readonly MetadataJsonService metadata = metadata ?? new MetadataJsonService();

    public async Task<ShelfLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        StoreSnapshot loaded = await store.LoadAsync(cancellationToken);
        ShelfSession candidate = new(loaded.Items);
        int expired = loaded.Settings.ExpireOnExit ? 0 : candidate.Expire(clock.UtcNow, loaded.Settings.Retention);
        StoreSnapshot result = new([.. candidate.Items], loaded.Settings);
        if (expired > 0)
        {
            await store.SaveAsync(result, cancellationToken);
        }
        return new(result, expired);
    }

    public int PreviewPolicyChange(ShelfSession session, TimeSpan retention, bool expireOnExit)
    {
        ArgumentNullException.ThrowIfNull(session);
        return expireOnExit ? session.CountUnpinned() : session.CountExpiring(clock.UtcNow, retention);
    }

    public async Task<PolicyChangeResult> ChangePolicyAsync(ShelfSession session, AppSettings current,
        TimeSpan retention, bool expireOnExit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(current);
        AppSettings changed = CopySettings(current, retention, expireOnExit);
        ShelfSession candidate = new(session.Items);
        int affected = expireOnExit ? candidate.CountUnpinned() : candidate.Expire(clock.UtcNow, retention);
        await SaveAndReplaceAsync(session, candidate, changed, cancellationToken);
        return new(changed, affected);
    }

    public async Task<int> ClearUnpinnedAsync(ShelfSession session, AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(settings);
        ShelfSession candidate = new(session.Items);
        int removed = candidate.ClearUnpinned();
        await SaveAndReplaceAsync(session, candidate, settings, cancellationToken);
        return removed;
    }

    public async Task<AppSettings> ClearAllAsync(ShelfSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ShelfSession candidate = new();
        await SaveAndReplaceAsync(session, candidate, AppSettings.Default, cancellationToken);
        return AppSettings.Default;
    }

    public async Task<int> PrepareForExitAsync(ShelfSession session, AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(settings);
        ShelfSession candidate = new(session.Items);
        int removed = settings.ExpireOnExit ? candidate.ClearUnpinned() : candidate.Expire(clock.UtcNow, settings.Retention);
        await SaveAndReplaceAsync(session, candidate, settings, cancellationToken);
        return removed;
    }

    public byte[] Export(ShelfSession session, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(settings);
        return metadata.Export(new StoreSnapshot([.. session.Items], settings), clock.UtcNow);
    }

    public async Task<AppSettings> ImportAsync(ReadOnlyMemory<byte> json, ShelfSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        StoreSnapshot imported = metadata.Import(json.Span);
        ShelfSession candidate = new(imported.Items);
        await SaveAndReplaceAsync(session, candidate, imported.Settings, cancellationToken);
        return imported.Settings;
    }

    public Task SaveAsync(ShelfSession session, AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(settings);
        return SaveSnapshotAsync(new StoreSnapshot([.. session.Items], settings), cancellationToken);
    }

    public Task SaveSnapshotAsync(StoreSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return store.SaveAsync(snapshot, cancellationToken);
    }

    private async Task SaveAndReplaceAsync(ShelfSession current, ShelfSession candidate, AppSettings settings,
        CancellationToken cancellationToken)
    {
        await store.SaveAsync(new StoreSnapshot([.. candidate.Items], settings), cancellationToken);
        current.ReplaceAll(candidate.Items);
    }

    private static AppSettings CopySettings(AppSettings current, TimeSpan retention, bool expireOnExit) =>
        AppSettings.Create(current.DockEdge, retention, current.StartAtLogin, current.ReduceMotion,
            current.HighContrast, current.GlobalShortcut, expireOnExit);
}
