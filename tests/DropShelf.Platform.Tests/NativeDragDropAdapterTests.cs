using DropShelf.Core;
using Mac = DropShelf.Platform.macOS;
using Win = DropShelf.Platform.Windows;
using Xunit;

namespace DropShelf.Platform.Tests;

public sealed class NativeDragDropAdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WindowsSyntheticDataObjectMapsInboundAndOutboundFormats()
    {
        string a = Path.GetFullPath("a.txt");
        string b = Path.GetFullPath("b.txt");
        Win.WindowsDragDropAdapter adapter = new();
        InboundDropPayload inbound = adapter.ReadInbound(new Dictionary<string, object?>
        {
            [Win.WindowsDragDropFormats.FileDrop] = new[] { a, b },
            [Win.WindowsDragDropFormats.UnicodeText] = "ignored",
        });
        DropConversionResult converted = new CanonicalDropConverter().ConvertInbound(inbound, Now);
        NativeOutboundPayload outbound = adapter.CreateOutbound(converted.Items);

        Assert.Equal([a, b], converted.Items.Select(item => ((FileReferencePayload)item.Payload).Path));
        Assert.Equal([a, b], Assert.IsType<string[]>(outbound.Formats[Win.WindowsDragDropFormats.FileDrop]));
        Assert.Equal("FileDrop", Win.WindowsDragDropFormats.FileDrop);
        Assert.Equal("UnicodeText", Win.WindowsDragDropFormats.UnicodeText);
        Assert.Equal("UniformResourceLocatorW", Win.WindowsDragDropFormats.Url);
    }

    [Fact]
    public void MacSyntheticPasteboardMapsInboundAndOutboundFormats()
    {
        Mac.MacPasteboardAdapter adapter = new();
        InboundDropPayload inbound = adapter.ReadInbound(new Dictionary<string, object?>
        {
            [Mac.MacPasteboardFormats.Url] = "https://example.test/a",
            [Mac.MacPasteboardFormats.PlainText] = "ignored",
        });
        ShelfItem item = new CanonicalDropConverter().ConvertInbound(inbound, Now).Items.Single();
        NativeOutboundPayload outbound = adapter.CreateOutbound([item]);

        Assert.Equal("https://example.test/a", ((UrlPayload)item.Payload).Url.AbsoluteUri);
        Assert.Equal("https://example.test/a", outbound.Formats[Mac.MacPasteboardFormats.Url]);
        Assert.Equal("public.file-url", Mac.MacPasteboardFormats.FileUrl);
        Assert.Equal("NSFilenamesPboardType", Mac.MacPasteboardFormats.LegacyFileNames);
        Assert.Equal("public.utf8-plain-text", Mac.MacPasteboardFormats.PlainText);
        Assert.Equal("public.url", Mac.MacPasteboardFormats.Url);
    }

    [Fact]
    public void EachCanonicalKindGetsAppropriatePlatformFormats()
    {
        ShelfItem text = ShelfItem.Create(Guid.NewGuid(), "Text", TextPayload.Create("hello"), Now);
        ShelfItem url = ShelfItem.Create(Guid.NewGuid(), "example.test", UrlPayload.Create("https://example.test/"), Now);

        NativeOutboundPayload windowsText = new Win.WindowsDragDropAdapter().CreateOutbound([text]);
        NativeOutboundPayload windowsUrl = new Win.WindowsDragDropAdapter().CreateOutbound([url]);
        NativeOutboundPayload macText = new Mac.MacPasteboardAdapter().CreateOutbound([text]);
        NativeOutboundPayload macUrl = new Mac.MacPasteboardAdapter().CreateOutbound([url]);

        Assert.Equal("hello", windowsText.Formats[Win.WindowsDragDropFormats.UnicodeText]);
        Assert.Equal("https://example.test/", windowsUrl.Formats[Win.WindowsDragDropFormats.Url]);
        Assert.Equal("hello", macText.Formats[Mac.MacPasteboardFormats.PlainText]);
        Assert.Equal("https://example.test/", macUrl.Formats[Mac.MacPasteboardFormats.Url]);
    }

    [Fact]
    public async Task OutboundConversionPreservesSourcePathAndBytes()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"drop-shelf-out-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "source.bin");
        byte[] bytes = [4, 3, 2, 1, 0, 255];
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            ShelfItem item = ShelfItem.Create(Guid.NewGuid(), "source.bin", FileReferencePayload.Create(path), Now);
            NativeOutboundPayload windows = new Win.WindowsDragDropAdapter().CreateOutbound([item]);
            NativeOutboundPayload mac = new Mac.MacPasteboardAdapter().CreateOutbound([item]);

            Assert.Equal(path, Assert.IsType<string[]>(windows.Formats[Win.WindowsDragDropFormats.FileDrop]).Single());
            Assert.Equal(path, Assert.IsType<string[]>(mac.Formats[Mac.MacPasteboardFormats.LegacyFileNames]).Single());
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void WindowsAdvertisedMalformedHighPrecedenceFormatsAreRejected()
    {
        Win.WindowsDragDropAdapter adapter = new();

        _ = Assert.Throws<ShelfValidationException>(() => adapter.ReadInbound(new Dictionary<string, object?>
        {
            [Win.WindowsDragDropFormats.FileDrop] = "not a collection",
            [Win.WindowsDragDropFormats.Url] = "https://must-not-fall-through.test/",
        }));
        _ = Assert.Throws<ShelfValidationException>(() => adapter.ReadInbound(new Dictionary<string, object?>
        {
            [Win.WindowsDragDropFormats.FileDrop] = Array.Empty<string>(),
            [Win.WindowsDragDropFormats.UnicodeText] = "must not fall through",
        }));
        _ = Assert.Throws<ShelfValidationException>(() => adapter.ReadInbound(new Dictionary<string, object?>
        {
            [Win.WindowsDragDropFormats.Url] = 42,
            [Win.WindowsDragDropFormats.UnicodeText] = "must not fall through",
        }));
    }

    [Fact]
    public void MacAdvertisedMalformedHighPrecedenceFormatsAreRejected()
    {
        Mac.MacPasteboardAdapter adapter = new();

        _ = Assert.Throws<ShelfValidationException>(() => adapter.ReadInbound(new Dictionary<string, object?>
        {
            [Mac.MacPasteboardFormats.FileUrl] = new object(),
            [Mac.MacPasteboardFormats.Url] = "https://must-not-fall-through.test/",
        }));
        _ = Assert.Throws<ShelfValidationException>(() => adapter.ReadInbound(new Dictionary<string, object?>
        {
            [Mac.MacPasteboardFormats.FileUrl] = Array.Empty<string>(),
            [Mac.MacPasteboardFormats.PlainText] = "must not fall through",
        }));
        _ = Assert.Throws<ShelfValidationException>(() => adapter.ReadInbound(new Dictionary<string, object?>
        {
            [Mac.MacPasteboardFormats.Url] = 42,
            [Mac.MacPasteboardFormats.PlainText] = "must not fall through",
        }));
    }

    [Fact]
    public void PlatformAdaptersRejectOversizedNativeFileCollections()
    {
        string[] paths = Enumerable.Range(0, DomainLimits.MaxItems + 1)
            .Select(index => Path.GetFullPath($"{index}.txt"))
            .ToArray();

        _ = Assert.Throws<ShelfValidationException>(() => new Win.WindowsDragDropAdapter().ReadInbound(
            new Dictionary<string, object?> { [Win.WindowsDragDropFormats.FileDrop] = paths }));
        _ = Assert.Throws<ShelfValidationException>(() => new Mac.MacPasteboardAdapter().ReadInbound(
            new Dictionary<string, object?> { [Mac.MacPasteboardFormats.LegacyFileNames] = paths }));
    }
}
