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
public sealed class TemplateRouter : IPageTemplateRouter
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
