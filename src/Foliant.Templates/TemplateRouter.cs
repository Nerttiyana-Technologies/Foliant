using Foliant;
using UglyToad.PdfPig;

namespace Foliant.Templates;

/// <summary>
/// The routing decision for ONE page. <see cref="Matched"/> ⇒ the page was recognized as <see cref="Match"/>
/// and <see cref="Fields"/> are the deterministic, template-bound values; otherwise the caller runs the page
/// through the DEFAULT document pipeline. Per-page routing contains risk: a wrong page can't corrupt others,
/// and any unrecognized page simply falls back.
/// </summary>
public sealed record PageRoute(int Page, TemplateMatch? Match, IReadOnlyList<FormField> Fields)
{
    public bool Matched => Match is not null;

    /// <summary>A page that fell back to default processing (no confident template match).</summary>
    public static PageRoute Fallback(int page) => new(page, null, Array.Empty<FormField>());
}

/// <summary>
/// Routes each page of an upload independently: fingerprint it against the <see cref="TemplateRegistry"/> and,
/// when a template matches with high confidence, bind that page's filled widgets to the template's KNOWN
/// labels (deterministic — no runtime geometric guessing). Unmatched pages are returned as fallbacks for the
/// default pipeline. Real uploads are PACKAGES (cover form + amendments + scanned exhibits), so the decision
/// is per page, never per document. The matcher is conservative — bias to fallback; a false match is the only
/// dangerous failure.
/// </summary>
public sealed class TemplateRouter : IPageTemplateRouter, IScannedFormRouter
{
    private readonly TemplateRegistry _registry;
    private readonly double _threshold;

    public TemplateRouter(TemplateRegistry registry, double threshold = FormMatcher.DefaultThreshold)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _threshold = threshold;
    }

    /// <summary>Core-contract entry point used by the pipeline: match → deterministic fields, else null.</summary>
    public PageTemplateMatch? TryRoute(byte[] pdf, int pageNumber)
    {
        var route = RoutePage(pdf, pageNumber);
        return route.Matched
            ? new PageTemplateMatch(route.Match!.Template.TemplateId, route.Match.Score, route.Fields)
            : null;
    }

    /// <summary>By-identity entry point (flattened/scanned federal forms): find the bundled/customer template
    /// for this designation, confirm the page is the SAME GSA revision (= same printed layout), then bind its
    /// geometry to the rendered page + OCR text.</summary>
    public PageTemplateMatch? TryRouteByDesignation(
        string designation, string? revisionYear, PageImage image, IReadOnlyList<TextLine> lines, int pageNumber)
    {
        if (string.IsNullOrWhiteSpace(designation) || image is null) return null;
        var template = FindByDesignation(designation);
        if (template is null) return null;

        int templatePage = template.Elements.Any(e => e.Page == pageNumber) ? pageNumber : 1;
        bool debug = Environment.GetEnvironmentVariable("FOLIANT_DEBUG_ANCHOR") is not null;

        // PRIMARY TRUST GATE — revision. A federal form prints its GSA revision ("REV. 11/2021"); same form +
        // same revision = same printed layout, even across agencies (only fillable-widget placement differs,
        // which pixel/positional extraction ignores). The widget-Jaccard / label-anchor score cannot separate
        // "same layout, noisy scan" from "different layout, clean OCR" (measured 0.325 vs 0.300), so revision is
        // the reliable key. A different revision ⇒ a different printed layout ⇒ abstain (never apply its coords).
        string? tplRev = TemplateRevisionYear(template.TemplateId);
        string? pageRev = revisionYear is { Length: >= 2 } ? revisionYear[^2..] : null;

        if (tplRev is not null && pageRev is not null)
        {
            if (!string.Equals(tplRev, pageRev, StringComparison.Ordinal))
            {
                if (debug) Console.Error.WriteLine(
                    $"[revision] {template.TemplateId}: page rev '20{pageRev}' ≠ template '20{tplRev}' → abstain");
                return null;
            }
            // Same revision: trust the geometry, with only a light garbage-reject floor (rejects a page that
            // merely *names* the form/revision in body text but whose labels don't sit near the field positions).
            if (!LayoutAnchorVerifier.IsLayoutMatch(image, lines, template, templatePage, minAlignedFraction: 0.15))
                return null;
        }
        else
        {
            // Revision unconfirmed (not printed / illegible on a poor scan) → we have no reliable trust key, so
            // demand STRICT layout alignment. This rarely passes a real scan, i.e. we abstain rather than risk
            // applying coordinates we can't vouch for — a safe false-negative, never a confident-wrong answer.
            if (!LayoutAnchorVerifier.IsLayoutMatch(image, lines, template, templatePage, minAlignedFraction: 0.6))
                return null;
        }

        var fields = ScannedFormExtractor.Extract(image, lines, template, templatePage);
        return fields.Count == 0 ? null : new PageTemplateMatch(template.TemplateId, 1.0, fields);
    }

    // The revision year encoded in a bundled template id, e.g. "SF1449-21" → "21", "SF30-16c" → "16",
    // "SF25A-23a" → "23", "SF18-95a" → "95". Null when the id carries no revision token ("1413", "SF_1410").
    private static string? TemplateRevisionYear(string templateId)
    {
        var m = System.Text.RegularExpressions.Regex.Match(templateId ?? string.Empty, @"-(\d{2})");
        return m.Success ? m.Groups[1].Value : null;
    }

    // Match a template whose id is "SF{designation}" up to a boundary: "30" → SF30-16c, "1449" → SF1449-21,
    // "25A" → SF25A-23a. The char after the number must not continue it (so "30" ≠ SF300...).
    private FormLayout? FindByDesignation(string designation)
    {
        string want = "SF" + designation.ToUpperInvariant();
        foreach (var t in _registry.All())
        {
            string id = t.TemplateId.ToUpperInvariant();
            if (id.StartsWith(want, StringComparison.Ordinal)
                && (id.Length == want.Length || !char.IsLetterOrDigit(id[want.Length])))
                return t;
        }
        return null;
    }

    /// <summary>Routes a single page. Matched ⇒ template-bound fields; else a fallback marker.</summary>
    public PageRoute RoutePage(byte[] uploadPdf, int pageNumber)
    {
        ArgumentNullException.ThrowIfNull(uploadPdf);
        var match = FormMatcher.MatchPage(uploadPdf, pageNumber, _registry.All(), _threshold);
        if (match is null) return PageRoute.Fallback(pageNumber);
        var fields = TemplateExtractor.Extract(uploadPdf, pageNumber, match.Template, match.TemplatePage);
        return new PageRoute(pageNumber, match, fields);
    }

    /// <summary>Routes every page of the upload, in order.</summary>
    public IReadOnlyList<PageRoute> RouteDocument(byte[] uploadPdf)
    {
        ArgumentNullException.ThrowIfNull(uploadPdf);
        int pages;
        using (var doc = PdfDocument.Open(uploadPdf)) pages = doc.NumberOfPages;

        var routes = new List<PageRoute>(pages);
        for (int p = 1; p <= pages; p++) routes.Add(RoutePage(uploadPdf, p));
        return routes;
    }
}
