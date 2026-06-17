// EnumeratorReadingOrder is a pure post-pass over ordered regions + their text. The tests focus on
// the STRICT GUARD: it must reorder a clean numbered mosaic, and must abstain on everything else
// (gaps, duplicates, runs not starting at 1, too few carriers, already-correct geometry).

using Foliant;
using Foliant.Pipeline;
using Xunit;

namespace Foliant.Tests;

public class EnumeratorReadingOrderTests
{
    // A region tagged by a label so tests can assert order independent of geometry.
    private static LayoutRegion Region(string tag) =>
        new(RegionType.Text, tag, 0.9f, new BoundingBox(0, 0, 100, 20));

    private static List<TextLine> Line(string text) =>
        new() { new TextLine(new BoundingBox(0, 0, 100, 12), text, 1f, TextSource.Ocr) };

    // Build (orderedRegions, linesByRegion) from "geometric order" of (tag, firstLineText) pairs.
    private static (IReadOnlyList<LayoutRegion>, Dictionary<LayoutRegion, List<TextLine>>) Build(
        params (string Tag, string Text)[] geo)
    {
        var ordered = new List<LayoutRegion>();
        var map = new Dictionary<LayoutRegion, List<TextLine>>();
        foreach (var (tag, text) in geo)
        {
            var r = Region(tag);
            ordered.Add(r);
            map[r] = Line(text);
        }
        return (ordered, map);
    }

    private static string[] Tags(IReadOnlyList<LayoutRegion> regions) =>
        regions.Select(r => r.RawLabel).ToArray();

    [Fact]
    public void CleanRun_OutOfOrder_IsReorderedByNumber()
    {
        // Geometry read a 2-column quiz column-major: 1,3,5 then 2,4 — true order is 1,2,3,4,5.
        var (ordered, map) = Build(
            ("a", "1. first question"),
            ("b", "3. third question"),
            ("c", "5. fifth question"),
            ("d", "2. second question"),
            ("e", "4. fourth question"));

        var result = EnumeratorReadingOrder.Apply(ordered, map);

        Assert.Equal(new[] { "a", "d", "b", "e", "c" }, Tags(result)); // 1,2,3,4,5
    }

    [Fact]
    public void CleanRun_AlreadyCorrect_IsUnchanged()
    {
        var (ordered, map) = Build(
            ("a", "1. one"), ("b", "2. two"), ("c", "3. three"));

        var result = EnumeratorReadingOrder.Apply(ordered, map);

        Assert.Same(ordered, result); // no-op returns the original instance
    }

    [Fact]
    public void NonEnumeratedRegions_StayInTheirSlots()
    {
        // A heading (no number) sits between numbered items; it must not move.
        var (ordered, map) = Build(
            ("h", "Section heading"),
            ("a", "2. two"),
            ("b", "1. one"),
            ("c", "3. three"));

        var result = EnumeratorReadingOrder.Apply(ordered, map);

        // Slots occupied by carriers were indices 1,2,3 → filled 1,2,3 numerically; heading at 0 stays.
        Assert.Equal(new[] { "h", "b", "a", "c" }, Tags(result));
    }

    [Fact]
    public void RunWithGap_IsNotReordered()
    {
        var (ordered, map) = Build(
            ("a", "1. one"), ("b", "2. two"), ("c", "4. four")); // missing 3

        Assert.Same(ordered, EnumeratorReadingOrder.Apply(ordered, map));
    }

    [Fact]
    public void RunNotStartingAtOne_IsNotReordered()
    {
        var (ordered, map) = Build(
            ("a", "3. three"), ("b", "2. two"), ("c", "4. four")); // 2,3,4 — no 1

        Assert.Same(ordered, EnumeratorReadingOrder.Apply(ordered, map));
    }

    [Fact]
    public void DuplicateNumbers_AreNotReordered()
    {
        var (ordered, map) = Build(
            ("a", "1. one"), ("b", "2. two"), ("c", "2. also two"));

        Assert.Same(ordered, EnumeratorReadingOrder.Apply(ordered, map));
    }

    [Fact]
    public void FewerThanThreeCarriers_AreNotReordered()
    {
        var (ordered, map) = Build(
            ("a", "2. two"), ("b", "1. one")); // only 2 carriers

        Assert.Same(ordered, EnumeratorReadingOrder.Apply(ordered, map));
    }

    [Fact]
    public void DecimalsAndYears_DoNotCountAsEnumerators()
    {
        // "1.5", "2020." and a bare number must not register as leading enumerators, so the
        // page has too few real carriers and geometry is preserved.
        var (ordered, map) = Build(
            ("a", "1.5 million users"),
            ("b", "2020. was a year"),
            ("c", "42 things happened"));

        Assert.Same(ordered, EnumeratorReadingOrder.Apply(ordered, map));
    }

    [Fact]
    public void ParenStyle_Enumerators_AreRecognized()
    {
        var (ordered, map) = Build(
            ("a", "2) two"), ("b", "3) three"), ("c", "1) one"));

        var result = EnumeratorReadingOrder.Apply(ordered, map);

        Assert.Equal(new[] { "c", "a", "b" }, Tags(result)); // 1,2,3
    }
}
