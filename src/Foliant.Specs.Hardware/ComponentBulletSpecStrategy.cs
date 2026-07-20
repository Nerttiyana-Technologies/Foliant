using System.Text.RegularExpressions;

namespace Foliant.Specs.Hardware;

/// <summary>
/// Component-bullet strategy (ADR-0006 §3.2 #3) — bullet lines that carry hardware vocabulary, each run
/// through the recognizer to become a <see cref="HardwareComponent"/>. Covers the RPMS Server SOW
/// (<c>• IBM Power S1014 Server (Power10, 8-core)</c> / <c>• 64GB DDR5 Memory</c>).
///
/// <para>A bullet is kept only when the recognizer finds at least one attribute in it (G-precision:
/// a prose bullet with no hardware vocabulary is never turned into a component). Lines already
/// consumed as <c>Label: value</c> spec-sheet fields are excluded so the two strategies don't
/// double-count the same line.</para>
/// </summary>
internal static partial class ComponentBulletSpecStrategy
{
    // A bullet: leading -, •, *, ▪, or "N." / "N)" enumerator.
    [GeneratedRegex(@"^\s*(?:[-•*▪●]|\d+[.)])\s+(\S.*)$")]
    private static partial Regex BulletRx();

    // A "Label: value" line — handled by the key-value strategy; skip here to avoid double-counting.
    [GeneratedRegex(@"^\s*[-•*]?\s*[A-Za-z][A-Za-z /()]{1,28}?\s*:\s*\S")]
    private static partial Regex KvLineRx();

    public static IEnumerable<HardwareComponent> Extract(IReadOnlyList<PageResult> pages)
    {
        // Over the COMPOSED markdown (ADR-0006 §3.2: "a pass over the already-composed document"), not the
        // raw text lines: in a born-digital text layer the bullet glyph is often a separate TextLine from
        // its text, and only composition re-joins "• <item>" onto one line.
        foreach (var page in pages)
            foreach (var text in ComposedLines.Of(page))
            {
                var m = BulletRx().Match(text);
                if (!m.Success) continue;
                if (KvLineRx().IsMatch(text)) continue;   // owned by the key-value strategy

                string body = Collapse(m.Groups[1].Value);
                var attributes = AttributeRecognizer.Recognize(body);
                if (attributes.Count == 0) continue;      // precision guard

                yield return new HardwareComponent(body, Attributes: attributes);
            }
    }

    private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}
