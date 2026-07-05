// Sensitivity-marking detection (advisory): flags pages carrying CUI (32 CFR 2002), legacy
// dissemination-control, or national-security classification BANNER markings, so callers that
// feed extracted text into downstream systems know a page carries controlled content BEFORE
// they ship it somewhere it must not go. Advisory only — extraction is never suppressed; the
// flag rides on PageResult.SensitivityMarking / DocumentResult.SensitivityMarkedPages.
//
// Precision strategy: markings are BANNERS — short lines in the page's top/bottom bands or in
// the header/footer furniture Foliant already separates. Restricting the scan to those
// positions (plus requiring the uppercase token to appear verbatim for classification words)
// keeps ordinary prose — "this information is confidential", "the secret to our success" —
// from flagging. "Company Confidential"-style proprietary markings are intentionally NOT
// detected (they'd flag most proposal pages).

using System.Text.RegularExpressions;

namespace Foliant.Pipeline;

public static partial class SensitivityMarkingDetector
{
    /// <summary>Fraction of page height treated as the banner band at top and bottom.</summary>
    private const float BandFraction = 0.12f;

    /// <summary>Banner lines longer than this are prose, not markings.</summary>
    private const int MaxBannerLength = 80;

    // CUI banner per 32 CFR 2002.20 / GSA CUI Guide (1-31-2024): control marking "CUI" alone
    // (Basic), category markings after "//" with "SP-" prefixes for Specified and single-"/"
    // separators for multiples, limited-dissemination controls as the last "//" element —
    // e.g. "CUI", "CUI//SP-PRVCY", "CUI//SP-PRVCY/PROC//FEDCON".
    [GeneratedRegex(@"(?<![A-Z0-9])CUI(//[A-Z0-9][A-Z0-9/\- ]*)?(?![A-Z0-9])")]
    private static partial Regex CuiPattern();

    // 32 CFR 2002 alternate control banner: "CONTROLLED", same category/dissem structure.
    // Anchored — the word must BE the banner line, not prose containing "controlled".
    [GeneratedRegex(@"^CONTROLLED(//[A-Z0-9][A-Z0-9/\- ]*)?$")]
    private static partial Regex ControlledBannerPattern();

    // Classification banner: the whole (short) line is the marking, e.g. "SECRET//NOFORN",
    // "TOP SECRET", "CONFIDENTIAL". Anchored so the word must BE the banner, not appear in prose.
    [GeneratedRegex(@"^(TOP SECRET|SECRET|CONFIDENTIAL)(//[A-Z0-9][A-Z0-9/\- ]*)?$")]
    private static partial Regex ClassificationPattern();

    private static readonly string[] LegacyMarkings =
    [
        "FOR OFFICIAL USE ONLY", "FOUO",
        "SENSITIVE BUT UNCLASSIFIED",
        "LAW ENFORCEMENT SENSITIVE",
    ];

    /// <summary>
    /// Scans banner-position lines for sensitivity markings. Returns the most severe marking
    /// found (classification &gt; CUI &gt; legacy), normalized to the matched banner text, or
    /// null when the page carries none.
    /// </summary>
    /// <param name="lines">All extracted text lines (raster coordinates).</param>
    /// <param name="pageFurniture">Header/footer lines already separated by composition.</param>
    /// <param name="pageHeightPx">Rendered page height, for the top/bottom band test.</param>
    public static string? Detect(
        IReadOnlyList<TextLine> lines, IReadOnlyList<TextLine> pageFurniture, int pageHeightPx)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(pageFurniture);

        float topBand = pageHeightPx * BandFraction;
        float bottomBand = pageHeightPx * (1 - BandFraction);

        string? best = null;
        int bestRank = 0;

        void Consider(TextLine line)
        {
            string text = line.Text.Trim();
            if (text.Length == 0) return;
            string upper = text.ToUpperInvariant();

            // DoDI 5230.24 distribution statements B–F (restrictive; A = public release, not
            // flagged). Written-out statements exceed the banner-length cap, so test them first.
            // Per the DoD aid these do not by themselves prove CUI → legacy/dissemination rank.
            if (upper.StartsWith("DISTRIBUTION STATEMENT", StringComparison.Ordinal)
                && upper.Length > 23 && upper[23] is >= 'B' and <= 'F')
            {
                if (bestRank < 1) (best, bestRank) = ($"DISTRIBUTION STATEMENT {upper[23]}", 1);
                return;
            }
            if (upper.StartsWith("DISTRIBUTION AUTHORIZED TO", StringComparison.Ordinal))
            {
                if (bestRank < 1) (best, bestRank) = ("DISTRIBUTION STATEMENT", 1);
                return;
            }

            if (text.Length > MaxBannerLength) return;

            // Classification banners: the token must appear VERBATIM in uppercase in the source
            // (a lowercase "confidential" footer is prose, not a banner) and constitute the line.
            var cls = ClassificationPattern().Match(upper);
            if (cls.Success && text.Contains(cls.Groups[1].Value, StringComparison.Ordinal))
            {
                int rank = cls.Groups[1].Value == "TOP SECRET" ? 5 : cls.Groups[1].Value == "SECRET" ? 4 : 3;
                if (rank > bestRank) (best, bestRank) = (text, rank);
                return;
            }

            // CUI: the acronym is uppercase by construction, so a verbatim source match is
            // required. Also: the "CONTROLLED" alternate banner (anchored), the designation
            // indicator ("Controlled by:"), and the "[Contains CUI]" email/file indicator —
            // all from the GSA CUI Guide / 32 CFR 2002.
            // "CUI"/"U//CUI" banners, CUI//category strings, the "CONTROLLED" alternate banner,
            // designation-indicator block lines (DoD aid: "Controlled by:", "CUI Category:",
            // "Limited Dissemination Control:"/"LDC:"), REL TO markings, "[Contains CUI]".
            var cui = CuiPattern().Match(text);
            if (cui.Success || upper.Contains("CONTROLLED UNCLASSIFIED INFORMATION")
                            || ControlledBannerPattern().IsMatch(text)
                            || text.Contains("U//CUI", StringComparison.Ordinal)
                            || upper.StartsWith("CONTROLLED BY:", StringComparison.Ordinal)
                            || upper.StartsWith("CUI CATEGORY:", StringComparison.Ordinal)
                            || upper.StartsWith("LIMITED DISSEMINATION CONTROL:", StringComparison.Ordinal)
                            || upper.StartsWith("LDC:", StringComparison.Ordinal)
                            || text.Contains("REL TO USA", StringComparison.Ordinal)
                            || upper.Contains("[CONTAINS CUI]"))
            {
                // Report the full banner line, not just the matched token — "U//CUI" and
                // "CUI Category: BUDG" are more actionable than a bare "CUI".
                if (bestRank < 2) (best, bestRank) = (text, 2);
                return;
            }

            foreach (string legacy in LegacyMarkings)
            {
                bool wordish = legacy.Contains(' ')
                    ? upper.Contains(legacy)
                    : text.Contains(legacy, StringComparison.Ordinal);   // acronyms verbatim (FOUO)
                if (wordish && bestRank < 1) { (best, bestRank) = (legacy, 1); break; }
            }
            // "SBU" only as the entire banner line — the bare trigram is too collision-prone
            // inside longer banner text (part numbers, org codes).
            if (bestRank < 1 && text == "SBU") (best, bestRank) = ("SBU", 1);
        }

        foreach (var line in pageFurniture) Consider(line);
        foreach (var line in lines)
            if (line.Bounds.Y1 <= topBand || line.Bounds.Y2 >= bottomBand)
                Consider(line);

        return best;
    }
}
