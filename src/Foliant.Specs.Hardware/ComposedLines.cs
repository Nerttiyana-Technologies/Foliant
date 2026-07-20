namespace Foliant.Specs.Hardware;

/// <summary>
/// The text-based strategies read the COMPOSED page Markdown, not the raw <see cref="PageResult.Lines"/>
/// (ADR-0006 §3.2: "a multi-strategy deterministic pass over the already-composed document"). Composition
/// is what re-joins a bullet glyph to its text — in a born-digital text layer the "•" is frequently a
/// separate positioned run from the words beside it, so raw lines split "• 64GB DDR5 Memory" in two.
/// </summary>
internal static class ComposedLines
{
    private static readonly char[] NewLines = { '\n', '\r' };

    public static IEnumerable<string> Of(PageResult page) =>
        (page.Markdown ?? string.Empty)
            .Split(NewLines, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);
}
