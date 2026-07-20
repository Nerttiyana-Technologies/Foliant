using Foliant.Mcp.Shaping;
using Xunit;

namespace Foliant.Mcp.Tests;

public class ShapingTests
{
    private static PageResult MakePage(
        int pageNumber,
        string markdown = "some page text",
        string? sensitivityMarking = null,
        bool needsReview = false,
        int truthWords = 10,
        int truthWordsFound = 9)
        => new(
            PageNumber: pageNumber,
            WidthPx: 2550,
            HeightPx: 3300,
            Dpi: 300,
            Regions: Array.Empty<Region>(),
            Lines: Array.Empty<TextLine>(),
            PageFurniture: Array.Empty<TextLine>(),
            Source: TextSource.Ocr,
            Markdown: markdown,
            Verification: new PageVerification(0, truthWords, truthWordsFound, 0.1),
            Notice: needsReview ? "synthetic notice" : null,
            NeedsReview: needsReview,
            SensitivityMarking: sensitivityMarking);

    private static DocumentResult MakeDocument(params PageResult[] pages) =>
        new(pages, string.Join("\n\n", pages.Select(p => p.Markdown)));

    // ── Window clamps (the guardrail lives in code, not the prompt) ─────────────

    [Fact]
    public void Window_ClampsPageCount_ToMaxWindowPages()
    {
        var doc = MakeDocument(Enumerable.Range(1, 30).Select(i => MakePage(i)).ToArray());

        var window = Shape.BuildWindow(doc, fromPage: 1, pageCount: 500, blockSensitivePages: false);

        Assert.Equal(Shape.MaxWindowPages, window.Returned);
        Assert.Equal(30, window.TotalPages);
    }

    [Fact]
    public void Window_ClampsFromPage_IntoDocumentRange()
    {
        var doc = MakeDocument(Enumerable.Range(1, 5).Select(i => MakePage(i)).ToArray());

        var below = Shape.BuildWindow(doc, fromPage: -3, pageCount: 2, blockSensitivePages: false);
        var above = Shape.BuildWindow(doc, fromPage: 99, pageCount: 2, blockSensitivePages: false);

        Assert.Equal(1, below.FromPage);
        Assert.Equal(1, below.Pages[0].PageNumber);
        Assert.Equal(5, above.FromPage);
        Assert.Single(above.Pages);
        Assert.Equal(5, above.Pages[0].PageNumber);
    }

    [Fact]
    public void Window_SlicesRequestedPages_InOrder()
    {
        var doc = MakeDocument(Enumerable.Range(1, 10).Select(i => MakePage(i)).ToArray());

        var window = Shape.BuildWindow(doc, fromPage: 4, pageCount: 3, blockSensitivePages: false);

        Assert.Equal(new[] { 4, 5, 6 }, window.Pages.Select(p => p.PageNumber));
        Assert.Equal(3, window.Returned);
    }

    // ── Honesty flags carry through to the MCP boundary ─────────────────────────

    [Fact]
    public void Window_SurfacesNeedsReviewAndSensitivityLists()
    {
        var doc = MakeDocument(
            MakePage(1),
            MakePage(2, needsReview: true, truthWords: 0, truthWordsFound: 0),
            MakePage(3, sensitivityMarking: "CUI//SP-PRVCY"));

        var window = Shape.BuildWindow(doc, 1, 10, blockSensitivePages: false);

        Assert.Contains(2, window.PagesNeedingReview);
        Assert.Contains(3, window.SensitivityMarkedPages);
        Assert.True(window.Pages.Single(p => p.PageNumber == 2).NeedsReview);
        Assert.Equal("CUI//SP-PRVCY", window.Pages.Single(p => p.PageNumber == 3).SensitivityMarking);
    }

    [Fact]
    public void Window_AverageRecall_IgnoresPagesWithoutTruth()
    {
        var doc = MakeDocument(
            MakePage(1, truthWords: 100, truthWordsFound: 90),   // 90%
            MakePage(2, truthWords: 0, truthWordsFound: 0));     // no text layer → null recall

        var window = Shape.BuildWindow(doc, 1, 10, blockSensitivePages: false);

        Assert.Equal(90.0, window.AverageRecallPercent);
        Assert.Null(window.Pages.Single(p => p.PageNumber == 2).RecallPercent);
    }

    // ── Privacy gate (ADR-0005 D9) ──────────────────────────────────────────────

    [Fact]
    public void Window_RedactsMarkedPages_WhenGateOn()
    {
        var doc = MakeDocument(
            MakePage(1, markdown: "public content"),
            MakePage(2, markdown: "controlled content", sensitivityMarking: "SECRET//NOFORN"));

        var window = Shape.BuildWindow(doc, 1, 10, blockSensitivePages: true);

        Assert.Equal("public content", window.Pages[0].Markdown);
        Assert.DoesNotContain("controlled content", window.Pages[1].Markdown);
        Assert.Contains("SECRET//NOFORN", window.Pages[1].Markdown);   // notice names the marking
        Assert.Contains(2, window.SensitivityMarkedPages);             // flag still surfaced
    }

    [Fact]
    public void Window_KeepsMarkedPages_WhenGateOff()
    {
        var doc = MakeDocument(MakePage(1, markdown: "controlled", sensitivityMarking: "FOUO"));

        var window = Shape.BuildWindow(doc, 1, 10, blockSensitivePages: false);

        Assert.Equal("controlled", window.Pages[0].Markdown);
        Assert.Contains(1, window.SensitivityMarkedPages);
    }

    [Fact]
    public void Summary_RedactsMarkedPages_WhenGateOn()
    {
        var doc = MakeDocument(
            MakePage(1, markdown: "public"),
            MakePage(2, markdown: "controlled", sensitivityMarking: "CUI"));

        var summary = Shape.BuildSummary(doc, blockSensitivePages: true);

        Assert.Contains("public", summary.Markdown);
        Assert.DoesNotContain("controlled", summary.Markdown);
        Assert.Contains(2, summary.SensitivityMarkedPages);
    }

    // ── Verification-only mode (includeContent=false) ───────────────────────────

    [Fact]
    public void Window_OmitsMarkdown_WhenContentExcluded()
    {
        var doc = MakeDocument(
            MakePage(1, markdown: "big content"),
            MakePage(2, markdown: "more content", needsReview: true, truthWords: 0, truthWordsFound: 0));

        var window = Shape.BuildWindow(doc, 1, 10, blockSensitivePages: false, includeContent: false);

        Assert.All(window.Pages, p => Assert.Null(p.Markdown));
        Assert.All(window.Pages, p => Assert.False(p.MarkdownTruncated));
        // Honesty metadata still fully present.
        Assert.Contains(2, window.PagesNeedingReview);
        Assert.Equal(2, window.TotalPages);
        Assert.NotNull(window.AverageRecallPercent);
    }

    [Fact]
    public void Summary_OmitsMarkdown_WhenContentExcluded()
    {
        var doc = MakeDocument(
            MakePage(1, markdown: "content"),
            MakePage(2, markdown: "controlled", sensitivityMarking: "CUI"));

        var summary = Shape.BuildSummary(doc, blockSensitivePages: false, includeContent: false);

        Assert.Null(summary.Markdown);
        Assert.False(summary.MarkdownTruncated);
        Assert.Equal(2, summary.TotalPages);
        Assert.Contains(2, summary.SensitivityMarkedPages);
    }

    // ── Char caps ───────────────────────────────────────────────────────────────

    [Fact]
    public void Cap_TruncatesLongText_AndFlagsIt()
    {
        var (text, truncated) = Shape.Cap(new string('x', 25_000), Shape.MaxPageMarkdownChars);

        Assert.True(truncated);
        Assert.Contains("[truncated]", text);
        Assert.True(text.Length < 25_000);
    }

    [Fact]
    public void Cap_LeavesShortText_Unflagged()
    {
        var (text, truncated) = Shape.Cap("short", Shape.MaxPageMarkdownChars);

        Assert.False(truncated);
        Assert.Equal("short", text);
    }

    [Fact]
    public void Window_CapsPerPageMarkdown()
    {
        var doc = MakeDocument(MakePage(1, markdown: new string('y', 30_000)));

        var window = Shape.BuildWindow(doc, 1, 1, blockSensitivePages: false);

        Assert.True(window.Pages[0].MarkdownTruncated);
        Assert.True(window.Pages[0].Markdown.Length <= Shape.MaxPageMarkdownChars + 32);
    }
}
