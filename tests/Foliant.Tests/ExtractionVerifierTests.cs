using Foliant.Pipeline;
using Xunit;

namespace Foliant.Tests;

public class ExtractionVerifierTests
{
    private static TextLine L(string text) =>
        new(new BoundingBox(0, 0, 10, 10), text, 1f, TextSource.Ocr);

    [Fact]
    public void Normalize_KeepsOnlyAlphanumericsUppercased()
    {
        Assert.Equal("DAWNABLOOME", ExtractionVerifier.Normalize("Dawn.A-Bloome!"));
        Assert.Equal("697DCK25R00302", ExtractionVerifier.Normalize("697DCK-25-R-00302"));
        Assert.Equal("", ExtractionVerifier.Normalize("—–•"));
    }

    [Fact]
    public void CountLostLines_AllPresent_Zero()
    {
        var lines = new[] { L("hello world"), L("second line") };
        Assert.Equal(0, ExtractionVerifier.CountLostLines(
            "hello world\n\nsecond line\n", lines, Array.Empty<TextLine>()));
    }

    [Fact]
    public void CountLostLines_MissingLineCounts()
    {
        var lines = new[] { L("present"), L("missing entirely") };
        Assert.Equal(1, ExtractionVerifier.CountLostLines("present", lines, Array.Empty<TextLine>()));
    }

    [Fact]
    public void CountLostLines_FurnitureIsIntentional()
    {
        var furniture = L("Page 3 of 12");
        var lines = new[] { L("body"), furniture };
        Assert.Equal(0, ExtractionVerifier.CountLostLines("body", lines, new[] { furniture }));
    }

    [Fact]
    public void CountLostLines_PipeEscapedVariantStillCounts()
    {
        var lines = new[] { L("A|B") };
        Assert.Equal(0, ExtractionVerifier.CountLostLines(@"| A\|B |", lines, Array.Empty<TextLine>()));
    }

    [Fact]
    public void CountLostLines_ShortFragmentsIgnored()
    {
        var lines = new[] { L("ab") };   // length ≤ 2 — below the invariant threshold
        Assert.Equal(0, ExtractionVerifier.CountLostLines("", lines, Array.Empty<TextLine>()));
    }

    [Fact]
    public void TextLayerRecall_InvalidPdf_ReturnsUndefinedNotZero()
    {
        var (truthWords, found) = ExtractionVerifier.TextLayerRecall(
            new byte[] { 1, 2, 3 }, 1, "anything");
        Assert.Equal(0, truthWords);
        Assert.Equal(0, found);
    }
}
