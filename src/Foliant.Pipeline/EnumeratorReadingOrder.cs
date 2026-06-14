// Enumerator-aware reading-order post-pass.
//
// Pure geometry (XY-Cut++) orders regions by position, which scrambles numbered MOSAICS — magazine
// quiz pages, instructional step grids — where the true order is the printed NUMBER, a cue geometry
// cannot see (the documented Gate 6 τ≈0.733 boundary). This pass reorders such regions by their
// leading number, under a deliberately strict guard so it never disturbs a normally-ordered page:
//
//   • a region "carries an enumerator" when its TOPMOST visual line begins with "N." or "N)";
//   • the carrying regions must number ≥3, be distinct, and form a consecutive run STARTING AT 1
//     (1,2,3,…,k — no gaps, no duplicates); random leading numerals essentially never do this;
//   • only if geometry already disagrees with that numeric order are the carriers permuted into
//     numeric order WITHIN THE SLOTS THEY ALREADY OCCUPY — every non-enumerated region stays
//     exactly where geometry put it.
//
// Anything short of a clean 1..k run leaves the geometric order completely untouched. This is the
// conservative cut: it fixes the clean quiz/step cases and abstains on everything ambiguous, so it
// cannot regress the reference corpus.

using System.Text.RegularExpressions;

namespace Foliant.Pipeline;

internal static class EnumeratorReadingOrder
{
    // "N." or "N)" (1–3 digits) at the very start, followed by whitespace then content. The trailing
    // \s rejects decimals ("1.5") and the 1–3 digit cap rejects years ("2020.") and ID-like runs.
    private static readonly Regex Leading = new(@"^\s*(\d{1,3})[.)]\s", RegexOptions.Compiled);

    private const int MinCarriers = 3;     // a run shorter than this is too weak a signal
    private const int MaxEnumerator = 199; // sane ceiling for list/step numbering

    public static IReadOnlyList<LayoutRegion> Apply(
        IReadOnlyList<LayoutRegion> ordered,
        IReadOnlyDictionary<LayoutRegion, List<TextLine>> linesByRegion)
    {
        if (ordered.Count < MinCarriers) return ordered;

        // Enumerator value per carrying region, walked in current (geometric) order.
        var carriers = new List<LayoutRegion>();
        var value = new Dictionary<LayoutRegion, int>();
        foreach (var r in ordered)
        {
            if (!linesByRegion.TryGetValue(r, out var ls) || ls.Count == 0) continue;
            if (LeadingEnumerator(ls) is int n) { carriers.Add(r); value[r] = n; }
        }
        if (carriers.Count < MinCarriers) return ordered;

        var vals = carriers.Select(r => value[r]).ToList();
        if (vals.Distinct().Count() != vals.Count) return ordered;       // duplicate numbers → ambiguous
        if (vals.Min() != 1 || vals.Max() != vals.Count) return ordered; // not a 1..k run (1,2,…,count)

        var byNumber = carriers.OrderBy(r => value[r]).ToList();
        if (carriers.SequenceEqual(byNumber)) return ordered;            // geometry already correct

        // Permute carriers into numeric order within exactly the slots they already occupy.
        var result = ordered.ToList();
        var slots = new List<int>();
        for (int i = 0; i < result.Count; i++)
            if (value.ContainsKey(result[i])) slots.Add(i);
        for (int k = 0; k < slots.Count; k++)
            result[slots[k]] = byNumber[k];
        return result;
    }

    /// <summary>The leading arabic enumerator of a region's topmost visual line, or null.</summary>
    private static int? LeadingEnumerator(List<TextLine> regionLines)
    {
        var rows = LineGrouping.GroupIntoVisualLines(regionLines);
        if (rows.Count == 0) return null;
        var m = Leading.Match(rows[0].Text);
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) && n is >= 1 and <= MaxEnumerator
            ? n : null;
    }
}
