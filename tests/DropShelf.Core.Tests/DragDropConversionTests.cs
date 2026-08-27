using Xunit;

namespace DropShelf.Core.Tests;

public sealed class DragDropConversionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void InboundPrecedenceIsFilesThenExplicitUrlThenText()
    {
        string first = Path.GetFullPath("first.txt");
        string second = Path.GetFullPath("second.txt");
        CanonicalDropConverter converter = new();

        DropConversionResult files = converter.ConvertInbound(
            new InboundDropPayload([first, second], "https://ignored.example/", "ignored"), Now);
        DropConversionResult url = converter.ConvertInbound(
            new InboundDropPayload(null, "https://example.test/path", "ignored"), Now);
        DropConversionResult text = converter.ConvertInbound(new InboundDropPayload(null, null, "hello\r\nworld"), Now);

        Assert.Equal(InboundDropFormat.FileList, files.SelectedFormat);
        Assert.Equal([first, second], files.Items.Select(item => ((FileReferencePayload)item.Payload).Path));
        Assert.Equal(InboundDropFormat.Url, url.SelectedFormat);
        Assert.Equal("https://example.test/path", ((UrlPayload)url.Items.Single().Payload).Url.AbsoluteUri);
        Assert.Equal(InboundDropFormat.Text, text.SelectedFormat);
        Assert.Equal("hello\nworld", ((TextPayload)text.Items.Single().Payload).Text);
    }

    [Fact]
    public void InboundFileKindsAreAppliedInOrderAndMismatchedMetadataIsRejectedAtomically()
    {
        string file = Path.GetFullPath("first.txt");
        string directory = Path.GetFullPath("folder");
        ShelfSession session = new();
        DropAdmissionService service = new(new CanonicalDropConverter(), session);

        DropAdmissionResult accepted = service.Admit(
            new InboundDropPayload([file, directory], null, null,
                [FileReferenceKind.File, FileReferenceKind.Directory]), Now);

        Assert.True(accepted.Accepted);
        Assert.Equal(
            [FileReferenceKind.File, FileReferenceKind.Directory],
            accepted.Items.Select(item => ((FileReferencePayload)item.Payload).ReferenceKind));

        ShelfSession rejectedSession = new();
        DropAdmissionResult rejected = new DropAdmissionService(new CanonicalDropConverter(), rejectedSession).Admit(
            new InboundDropPayload([file, directory], null, null, [FileReferenceKind.Directory]), Now);

        Assert.False(rejected.Accepted);
        Assert.Empty(rejectedSession.Items);
    }

    [Fact]
    public void UrlShapedPlainTextBecomesAUrlAndFileUrlRemainsAUrl()
    {
        CanonicalDropConverter converter = new();

        ShelfItem web = converter.ConvertInbound(new InboundDropPayload(null, null, " https://example.test/a "), Now).Items.Single();
        ShelfItem file = converter.ConvertInbound(new InboundDropPayload(null, "file:///tmp/report.txt", null), Now).Items.Single();

        _ = Assert.IsType<UrlPayload>(web.Payload);
        _ = Assert.IsType<UrlPayload>(file.Payload);
    }

    [Fact]
    public void InvalidPayloadsFailAsAWholeWithoutChangingTheSession()
    {
        ShelfSession session = new();
        DropAdmissionService service = new(new CanonicalDropConverter(), session);
        string valid = Path.GetFullPath("valid.txt");

        DropAdmissionResult malformed = service.Admit(new InboundDropPayload([valid, "relative.txt"], null, null), Now);
        DropAdmissionResult empty = service.Admit(new InboundDropPayload(null, null, "   "), Now);
        DropAdmissionResult oversized = service.Admit(new InboundDropPayload(null, null, new string('x', DomainLimits.MaxTextLength + 1)), Now);

        Assert.False(malformed.Accepted);
        Assert.False(empty.Accepted);
        Assert.False(oversized.Accepted);
        Assert.Empty(session.Items);
        Assert.All([malformed, empty, oversized], result => Assert.False(string.IsNullOrWhiteSpace(result.UserMessage)));
    }

    [Fact]
    public async Task ConversionNeverReadsOrChangesSourceFilePathOrBytes()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"drop-shelf-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "secret.bin");
        byte[] original = [0, 1, 2, 3, 254, 255];
        await File.WriteAllBytesAsync(path, original);
        try
        {
            CanonicalDropConverter converter = new();
            ShelfItem item = converter.ConvertInbound(new InboundDropPayload([path], null, null), Now).Items.Single();

            Assert.Equal(path, ((FileReferencePayload)item.Payload).Path);
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData("not a URL")]
    [InlineData("ftp://example.test/private")]
    public void MalformedOrUnsupportedExplicitUrlIsRejectedWithoutEchoingInput(string untrustedUrl)
    {
        DropAdmissionService service = new(new CanonicalDropConverter(), new ShelfSession());

        DropAdmissionResult result = service.Admit(new InboundDropPayload(null, untrustedUrl, "must not fall through"), Now);

        Assert.False(result.Accepted);
        Assert.DoesNotContain(untrustedUrl, result.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void OverCapacityDropIsRejectedAtomicallyWithoutEchoingPath()
    {
        ShelfSession session = new(Enumerable.Range(0, DomainLimits.MaxItems)
            .Select(index => ShelfItem.Create(Guid.NewGuid(), $"item {index}", TextPayload.Create("safe"), Now, ordinal: index)));
        DropAdmissionService service = new(new CanonicalDropConverter(), session);
        string privatePath = Path.GetFullPath(Path.Combine("private", "secret.txt"));

        DropAdmissionResult result = service.Admit(new InboundDropPayload([privatePath], null, null), Now);

        Assert.False(result.Accepted);
        Assert.Equal(DomainLimits.MaxItems, session.Items.Count);
        Assert.DoesNotContain(privatePath, result.UserMessage, StringComparison.Ordinal);
    }
}
