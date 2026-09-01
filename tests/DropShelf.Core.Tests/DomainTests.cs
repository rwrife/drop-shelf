using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace DropShelf.Core.Tests;

public sealed class DomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void PayloadsNormalizeAndValidateUntrustedInput()
    {
        Assert.Equal("line 1\nline 2", TextPayload.Create("line 1\r\nline 2\r\n").Text);
        Assert.Equal("https", UrlPayload.Create(" https://example.test/a ").Url.Scheme);
        Assert.Equal(ValidationErrorCode.UnsupportedUrlScheme, Assert.Throws<ShelfValidationException>(() => UrlPayload.Create("javascript:alert(1)")).Code);
        Assert.Equal(ValidationErrorCode.InvalidUrl, Assert.Throws<ShelfValidationException>(() => UrlPayload.Create("https://user:secret@example.test/")).Code);
        Assert.Equal(ValidationErrorCode.TooLong, Assert.Throws<ShelfValidationException>(() => TextPayload.Create(new string('x', DomainLimits.MaxTextLength + 1))).Code);
        Assert.Equal(ValidationErrorCode.InvalidPath, Assert.Throws<ShelfValidationException>(() => FileReferencePayload.Create("relative.txt")).Code);
        FileReferencePayload file = FileReferencePayload.Create(Path.GetFullPath("source.txt"));
        Assert.Equal(ValidationErrorCode.InvalidPayload,
            Assert.Throws<ShelfValidationException>(() => file.WithAvailability((FileAvailability)999)).Code);
    }

    [Fact]
    public void SessionOperationsAreDeterministicAndOrdinalsStayDense()
    {
        ShelfItem a = Item(1, Now);
        ShelfItem b = Item(2, Now.AddMinutes(1));
        ShelfItem c = Item(3, Now.AddMinutes(2));
        ShelfSession session = new([c.WithTestOrdinal(7), a.WithTestOrdinal(7), b.WithTestOrdinal(3)]);

        Assert.Equal([b.Id, a.Id, c.Id], session.Items.Select(item => item.Id));
        session.Reorder(c.Id, 0);
        session.SetPinned(a.Id, true);
        Assert.Equal(2, session.Remove([b.Id, Guid.NewGuid(), c.Id]));
        Assert.True(session.Items.Single().IsPinned);
        Assert.Equal(0, session.Items.Single().Ordinal);
        Assert.Equal(ValidationErrorCode.ItemNotFound, Assert.Throws<ShelfValidationException>(() => session.SetPinned(b.Id, false)).Code);
    }

    [Fact]
    public void ExpirationIncludesExactBoundaryAndExemptsPinnedItems()
    {
        TimeSpan retention = TimeSpan.FromHours(1);
        ShelfSession session = new([Item(1, Now - retention), Item(2, Now - retention, true), Item(3, Now - retention + TimeSpan.FromTicks(1))]);
        Assert.Equal(1, session.Expire(Now, retention));
        Assert.Equal([GuidFrom(2), GuidFrom(3)], session.Items.Select(item => item.Id));
    }

    [Fact]
    public void PrivacyPolicyPreviewAndClearNeverAffectPinnedItems()
    {
        TimeSpan retention = TimeSpan.FromHours(1);
        ShelfSession session = new([
            Item(1, Now - retention),
            Item(2, Now - retention, true),
            Item(3, Now),
        ]);

        Assert.Equal(1, session.CountExpiring(Now, retention));
        Assert.Equal(2, session.CountUnpinned());
        Assert.Equal(2, session.ClearUnpinned());
        Assert.Equal(GuidFrom(2), Assert.Single(session.Items).Id);
    }

    [Fact]
    public void MissingFilesCanTransitionInBothDirectionsWithoutMutationOperations()
    {
        string path = Path.GetFullPath("source.txt");
        ShelfSession session = new([ShelfItem.Create(GuidFrom(1), "source.txt", FileReferencePayload.Create(path), Now)]);
        FakeFileSystem fileSystem = new(false);
        Assert.Equal(1, session.RefreshFileAvailability(fileSystem));
        Assert.Equal(FileAvailability.Missing, ((FileReferencePayload)session.Items[0].Payload).Availability);
        fileSystem.Exists = true;
        Assert.Equal(1, session.RefreshFileAvailability(fileSystem));
        Assert.Equal(FileAvailability.Available, ((FileReferencePayload)session.Items[0].Payload).Availability);
    }

    [Fact]
    public void MetadataExportRoundTripsAllSettingsWithoutAnyFileContents()
    {
        string path = Path.GetFullPath("private.bin");
        StoreSnapshot snapshot = new([ShelfItem.Create(GuidFrom(1), "private.bin", FileReferencePayload.Create(path, sizeBytes: 123), Now)],
            AppSettings.Create(retention: TimeSpan.FromDays(2), globalShortcut: "Ctrl+Shift+D", expireOnExit: true));
        MetadataJsonService service = new();
        byte[] json = service.Export(snapshot, Now);
        string text = Encoding.UTF8.GetString(json);
        StoreSnapshot imported = service.Import(json);
        Assert.DoesNotContain("fileContents", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"schemaVersion\": 2", text, StringComparison.Ordinal);
        Assert.Equal(path, ((FileReferencePayload)imported.Items.Single().Payload).Path);
        Assert.Equal(TimeSpan.FromDays(2), imported.Settings.Retention);
        Assert.Equal("Ctrl+Shift+D", imported.Settings.GlobalShortcut);
        Assert.True(imported.Settings.ExpireOnExit);
        Assert.Equal(ValidationErrorCode.InvalidExport, Assert.Throws<ShelfValidationException>(() => service.Import("{}"u8)).Code);
        Assert.Equal(ValidationErrorCode.InvalidExport, Assert.Throws<ShelfValidationException>(() => service.Import(new byte[DomainLimits.MaxExportBytes + 1])).Code);
    }

    [Fact]
    public void VersionOneMetadataImportUsesNewPrivacyDefaults()
    {
        const string json = /*lang=json,strict*/ """
            {
              "schemaVersion": 1,
              "exportedAt": "2026-01-02T03:04:05+00:00",
              "settings": {
                "dockEdge": "right",
                "retentionSeconds": 86400,
                "startAtLogin": false,
                "reduceMotion": false,
                "highContrast": false
              },
              "items": []
            }
            """;

        AppSettings settings = new MetadataJsonService().Import(Encoding.UTF8.GetBytes(json)).Settings;

        Assert.Equal(AppSettings.DefaultGlobalShortcut, settings.GlobalShortcut);
        Assert.False(settings.ExpireOnExit);
    }

    [Fact]
    public void SettingsDefaultsAreStableAndBounded()
    {
        Assert.Equal(DockEdge.Right, AppSettings.Default.DockEdge);
        Assert.Equal(TimeSpan.FromHours(24), AppSettings.Default.Retention);
        Assert.False(AppSettings.Default.StartAtLogin);
        Assert.False(AppSettings.Default.ExpireOnExit);
        _ = Assert.Throws<ShelfValidationException>(() => AppSettings.Create(retention: TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task DataServiceLoadsAndPersistsDurationExpiryWithoutTouchingPinnedItems()
    {
        AppSettings settings = AppSettings.Create(retention: TimeSpan.FromHours(1));
        RecordingStore store = new(new StoreSnapshot([
            Item(1, Now - TimeSpan.FromHours(2)),
            Item(2, Now - TimeSpan.FromHours(2), true),
        ], settings));
        ShelfDataService service = new(store, new FixedClock(Now));

        ShelfLoadResult loaded = await service.LoadAsync();

        Assert.Equal(1, loaded.ExpiredItems);
        Assert.Equal(GuidFrom(2), Assert.Single(loaded.Snapshot.Items).Id);
        Assert.Equal(GuidFrom(2), Assert.Single(store.Saved!.Items).Id);
    }

    [Fact]
    public async Task DataServicePreviewsPolicyAndAppliesImportOnlyAfterAtomicSave()
    {
        ShelfSession session = new([Item(1, Now - TimeSpan.FromDays(2)), Item(2, Now, true)]);
        RecordingStore store = new(new StoreSnapshot([], AppSettings.Default));
        ShelfDataService service = new(store, new FixedClock(Now));

        Assert.Equal(1, service.PreviewPolicyChange(session, TimeSpan.FromDays(1), expireOnExit: false));
        Assert.Equal(1, service.PreviewPolicyChange(session, TimeSpan.FromDays(1), expireOnExit: true));
        PolicyChangeResult changed = await service.ChangePolicyAsync(
            session, AppSettings.Default, TimeSpan.FromDays(1), expireOnExit: false);

        Assert.Equal(1, changed.AffectedItems);
        Assert.Equal(GuidFrom(2), Assert.Single(session.Items).Id);

        byte[] hostile = /*lang=json,strict*/ "{\"schemaVersion\":999}"u8.ToArray();
        _ = await Assert.ThrowsAsync<ShelfValidationException>(() => service.ImportAsync(hostile, session));
        Assert.Equal(GuidFrom(2), Assert.Single(session.Items).Id);
    }

    [Fact]
    public async Task ExitAndClearControlsPersistOnlyAppMetadata()
    {
        ShelfSession session = new([Item(1, Now), Item(2, Now, true)]);
        RecordingStore store = new(new StoreSnapshot([], AppSettings.Default));
        ShelfDataService service = new(store, new FixedClock(Now));

        Assert.Equal(1, await service.PrepareForExitAsync(session, AppSettings.Create(expireOnExit: true)));
        Assert.Equal(GuidFrom(2), Assert.Single(session.Items).Id);
        Assert.Equal(0, await service.ClearUnpinnedAsync(session, AppSettings.Default));
        AppSettings reset = await service.ClearAllAsync(session);
        Assert.Empty(session.Items);
        Assert.Equal(AppSettings.Default, reset);
    }

    [Fact]
    public void FileReferencePathsArePreservedAsOpaqueStrings()
    {
        string path = Path.Combine(Path.GetPathRoot(Path.GetFullPath("."))!, "opaque-e\u0301 ");
        FileReferencePayload payload = FileReferencePayload.Create(path);
        Assert.Equal(path, payload.Path);
        Assert.NotEqual(payload.Path.Normalize(), payload.Path);
    }

    [Fact]
    public void SessionItemsCannotBeCastBackToAMutableList()
    {
        ShelfSession session = new([Item(1, Now)]);
        Assert.False(session.Items is List<ShelfItem>);
        _ = Assert.Throws<NotSupportedException>(() => ((IList<ShelfItem>)session.Items).Add(Item(2, Now)));
    }

    [Fact]
    public void MetadataImportRejectsHostileObjectShapes()
    {
        MetadataJsonService service = new();
        JsonObject root = JsonNode.Parse(service.Export(new StoreSnapshot([Item(1, Now)], AppSettings.Default), Now))!.AsObject();
        JsonObject unknown = (JsonObject)root.DeepClone();
        unknown["unexpected"] = true;
        AssertInvalid(unknown.ToJsonString());
        string valid = root.ToJsonString();
        AssertInvalid(valid.Replace("\"schemaVersion\":2", "\"schemaVersion\":2,\"schemaVersion\":2", StringComparison.Ordinal));
        AssertInvalid(valid.Replace("\"dockEdge\":\"right\"", "\"dockEdge\":\"right\",\"dockEdge\":\"right\"", StringComparison.Ordinal));
        JsonObject missing = (JsonObject)root.DeepClone();
        _ = missing.Remove("exportedAt");
        AssertInvalid(missing.ToJsonString());
        JsonObject missingItemMetadata = (JsonObject)root.DeepClone();
        _ = missingItemMetadata["items"]![0]!.AsObject().Remove("createdAt");
        AssertInvalid(missingItemMetadata.ToJsonString());
        JsonObject defaulted = (JsonObject)root.DeepClone();
        defaulted["exportedAt"] = "0001-01-01T00:00:00+00:00";
        AssertInvalid(defaulted.ToJsonString());
        JsonObject numericEnum = (JsonObject)root.DeepClone();
        numericEnum["settings"]!["dockEdge"] = 1;
        AssertInvalid(numericEnum.ToJsonString());

        foreach (string field in new[] { "url", "title", "path", "fileKind", "sizeBytes", "modifiedAt", "availability" })
        {
            JsonObject hostile = (JsonObject)root.DeepClone();
            hostile["items"]![0]!.AsObject()[field] = field switch
            {
                "fileKind" => "file",
                "sizeBytes" => 1,
                "modifiedAt" => Now.ToString("O"),
                "availability" => "available",
                _ => "x",
            };
            AssertInvalid(hostile.ToJsonString());
        }

        void AssertInvalid(string json)
        {
            Assert.Equal(ValidationErrorCode.InvalidExport,
            Assert.Throws<ShelfValidationException>(() => service.Import(Encoding.UTF8.GetBytes(json))).Code);
        }
    }

    private static ShelfItem Item(int id, DateTimeOffset time, bool pinned = false) =>
        ShelfItem.Create(GuidFrom(id), id.ToString(System.Globalization.CultureInfo.InvariantCulture), TextPayload.Create("text"), time, isPinned: pinned);
    private static Guid GuidFrom(int id) => new(id, 0, 0, new byte[8]);

    private sealed class FakeFileSystem(bool exists) : IFileSystem
    {
        public bool Exists { get; set; } = exists;
        public bool FileExists(string path) => Exists;
        public bool DirectoryExists(string path) => Exists;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class RecordingStore(StoreSnapshot snapshot) : IShelfStore
    {
        public StoreSnapshot? Saved { get; private set; }
        public Task<StoreSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
        public Task SaveAsync(StoreSnapshot value, CancellationToken cancellationToken = default)
        {
            Saved = value;
            return Task.CompletedTask;
        }
    }
}

internal static class TestItemExtensions
{
    public static ShelfItem WithTestOrdinal(this ShelfItem item, int ordinal) => ShelfItem.Create(item.Id, item.DisplayName, item.Payload,
        item.CreatedAt, item.LastUsedAt, item.IsPinned, ordinal, item.SourceHint);
}
