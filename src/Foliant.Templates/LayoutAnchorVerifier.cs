using Foliant;

namespace Foliant.Templates;

/// <summary>
/// Confirms that a scanned/flattened page actually has the SAME printed layout as a template before its
/// geometry is trusted — the safety gate for by-identity extraction. A template must not be applied just
/// because the page says "STANDARD FORM 30": agencies lay the fillable fields out differently and use
/// different revisions, so blindly applying one layout's coordinates to another would read checkboxes at the
/// wrong spots (a confident-but-wrong answer).
///
/// How: each template element carries a label whose words are printed near that position on the real form
/// (the labels were paired from the blank's printed text). We check how many of those labels' distinctive
/// words actually appear in the page's OCR NEAR the expected position. High alignment ⇒ same printed layout
/// (any agency, same revision) ⇒ safe to extract. Low ⇒ abstain and fall back. Note this GENERALIZES: a
/// different agency's same-revision form passes; a different revision fails.
/// </summary>
public static class LayoutAnchorVerifier
{
    public static bool IsLayoutMatch(
        PageImage image, IReadOnlyList<TextLine> lines, FormLayout template, int templatePage,
        float positionTolerance = 0.05f, double minAlignedFraction = 0.6, int minAnchors = 6)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(template);
        if (lines is null || lines.Count == 0) return false;

        var anchors = template.Elements
            .Where(e => e.Page == templatePage)
            .Select(e => (e.Rect, Tokens: DistinctiveTokens(e.Label)))
            .Where(a => a.Tokens.Count > 0)
            .ToList();
        if (anchors.Count < minAnchors) return false;   // too little printed text to verify → abstain (safe)

        var ocr = lines.Select(l => (
            CX: (l.Bounds.X1 + l.Bounds.X2) / 2f / image.Width,
            CY: (l.Bounds.Y1 + l.Bounds.Y2) / 2f / image.Height,
            Text: (l.Text ?? string.Empty).ToUpperInvariant())).ToList();

        int aligned = 0;
        foreach (var (rect, tokens) in anchors)
        {
            float acx = rect.CenterX, acy = rect.CenterY;
            bool hit = ocr.Any(o =>
                Math.Abs(o.CX - acx) <= positionTolerance &&
                Math.Abs(o.CY - acy) <= positionTolerance &&
                tokens.Any(t => o.Text.Contains(t, StringComparison.Ordinal)));
            if (hit) aligned++;
        }
        double fraction = (double)aligned / anchors.Count;
        if (Environment.GetEnvironmentVariable("FOLIANT_DEBUG_ANCHOR") is not null)
            Console.Error.WriteLine(
                $"[anchor] {template.TemplateId} p{templatePage}: aligned {aligned}/{anchors.Count} = " +
                $"{fraction:F3} (threshold {minAlignedFraction:F2}) → {(fraction >= minAlignedFraction ? "MATCH" : "abstain")}");
        return fraction >= minAlignedFraction;
    }

    // Words ≥4 letters from a label — the distinctive printed tokens (skips "OF", "TO", numbers, punctuation).
    private static List<string> DistinctiveTokens(string? label) =>
        (label ?? string.Empty)
        .ToUpperInvariant()
        .Split(new[] { ' ', '—', '-', '/', '.', '(', ')', ',', ':', '\t' }, StringSplitOptions.RemoveEmptyEntries)
        .Where(w => w.Length >= 4 && w.Any(char.IsLetter))
        .Distinct(StringComparer.Ordinal)
        .ToList();
}
