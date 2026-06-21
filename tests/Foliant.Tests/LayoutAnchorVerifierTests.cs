using Foliant;
using Foliant.Templates;
using Xunit;

namespace Foliant.Tests;

// The by-identity safety gate: a page whose printed labels sit at the template's expected positions is the
// same layout (extract); one whose labels are elsewhere is a different layout/revision (abstain → fall back).
public sealed class LayoutAnchorVerifierTests
{
    private const int W = 1000, H = 1000;
    private static readonly PageImage Img = new(W, H, 150, new byte[W * H * 4]);

    private static TextLine L(string text, float cxNorm, float cyNorm)
    {
        float x = cxNorm * W, y = cyNorm * H;
        return new(new BoundingBox(x - 10, y - 5, x + 10, y + 5), text, 1f, TextSource.Ocr);
    }

    private static FormElement E(string label, float cx, float cy) =>
        new(FormElementKind.Text, 1, new NormalizedRect(cx - 0.02f, cy - 0.01f, cx + 0.02f, cy + 0.01f), label);

    private static readonly FormLayout Template = new("SF30-16c", "SF-30", new[]
    {
        E("2. CONTRACT NUMBER", 0.2f, 0.10f),
        E("5. SOLICITATION NUMBER", 0.6f, 0.10f),
        E("9B. DATED (SEE ITEM 11)", 0.2f, 0.20f),
        E("11. RECEIPT OF OFFERS IS EXTENDED", 0.5f, 0.35f),
        E("13A. CHANGE ORDER PURSUANT TO", 0.3f, 0.60f),
        E("16A. NAME OF CONTRACTING OFFICER", 0.7f, 0.80f),
    });

    [Fact]
    public void MatchingLayout_PrintedLabelsAtExpectedPositions_IsMatch()
    {
        var lines = new[]
        {
            L("CONTRACT NUMBER N0001", 0.2f, 0.10f),
            L("SOLICITATION NUMBER 80TECH", 0.6f, 0.10f),
            L("DATED 05/23/2024", 0.2f, 0.20f),
            L("RECEIPT OF OFFERS IS EXTENDED", 0.5f, 0.35f),
            L("CHANGE ORDER PURSUANT TO FAR", 0.3f, 0.60f),
            L("NAME OF CONTRACTING OFFICER", 0.7f, 0.80f),
        };
        Assert.True(LayoutAnchorVerifier.IsLayoutMatch(Img, lines, Template, 1));
    }

    [Fact]
    public void DifferentLayout_SameTextsElsewhere_AbstainsAsNoMatch()
    {
        // Same printed phrases, but a different layout puts them in the wrong places → positions don't align.
        var lines = new[]
        {
            L("CONTRACT NUMBER", 0.02f, 0.02f),
            L("SOLICITATION NUMBER", 0.03f, 0.03f),
            L("DATED", 0.04f, 0.04f),
            L("RECEIPT OF OFFERS EXTENDED", 0.05f, 0.05f),
            L("CHANGE ORDER PURSUANT", 0.06f, 0.06f),
            L("CONTRACTING OFFICER", 0.07f, 0.07f),
        };
        Assert.False(LayoutAnchorVerifier.IsLayoutMatch(Img, lines, Template, 1));
    }
}
