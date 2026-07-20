using System.Text.RegularExpressions;

namespace Foliant.Specs.Hardware;

/// <summary>
/// Key-value strategy (ADR-0006 §3.2 #2) — parses <c>- Label: value</c> / <c>Label: value</c> lines
/// into <see cref="SpecAttribute"/>s under a single rolled-up component. Covers the A04 labeled
/// spec-sheet (<c>- Processor: Intel® Xeon® W-3335 …</c> / <c>- Memory: 512GB DDR4 …</c>).
///
/// <para>The label maps the value to a category directly (so <c>Processor: Intel Xeon W-3335</c> is a
/// Processor even without a clock/core token), and the recognizer additionally mines the value for
/// capacity/frequency/medium. A line whose label is not a known hardware label is ignored (precision).</para>
/// </summary>
internal static partial class KeyValueSpecStrategy
{
    // "- Label: value" or "Label: value" (leading bullet optional). Label is letters/spaces/() only,
    // short, so prose sentences with a colon ("Note: the vendor shall…") don't masquerade as fields.
    [GeneratedRegex(@"^\s*[-•*]?\s*([A-Za-z][A-Za-z /()]{1,28}?)\s*:\s*(\S.*)$")]
    private static partial Regex KvLineRx();

    private static readonly (Regex Label, SpecCategory Category)[] LabelMap =
    {
        (Rx(@"processor|cpu"), SpecCategory.Processor),
        (Rx(@"memory|ram"), SpecCategory.Memory),
        (Rx(@"storage|ssd|nvme|hard\s*drive|disk|hdd"), SpecCategory.Storage),
        (Rx(@"graphics|gpu|video\s*card"), SpecCategory.Graphics),
        (Rx(@"display|monitor|screen"), SpecCategory.Display),
        (Rx(@"network|ethernet|nic"), SpecCategory.Network),
        (Rx(@"power\s*supply|psu|power"), SpecCategory.PowerSupply),
        (Rx(@"form\s*factor|chassis|enclosure"), SpecCategory.FormFactor),
        (Rx(@"motherboard|mainboard|system\s*board"), SpecCategory.Motherboard),
        (Rx(@"operating\s*system|^os$"), SpecCategory.OperatingSystem),
        (Rx(@"warranty|support"), SpecCategory.Warranty),
        (Rx(@"part\s*(no|number|#)?|model|sku|nsn"), SpecCategory.PartNumber),
        (Rx(@"quantity|qty"), SpecCategory.Quantity),
    };

    private static Regex Rx(string p) => new($@"^\s*(?:{p})\s*$", RegexOptions.IgnoreCase);

    public static HardwareComponent? Extract(IReadOnlyList<PageResult> pages)
    {
        var attrs = new List<SpecAttribute>();
        foreach (var page in pages)
            foreach (var line in ComposedLines.Of(page))   // composed document, not raw lines (ADR-0006 §3.2)
            {
                var m = KvLineRx().Match(line);
                if (!m.Success) continue;

                string label = m.Groups[1].Value.Trim();
                string value = m.Groups[2].Value.Trim();
                var category = MapLabel(label);
                if (category is null) continue;   // not a hardware label — ignore

                // The label fixes the category; the recognizer sharpens value/unit from the value text.
                var recognized = AttributeRecognizer.Recognize(value)
                    .FirstOrDefault(a => a.Category == category);
                attrs.Add(recognized ?? new SpecAttribute(category.Value, $"{label}: {value}", value));
            }

        // Two or more labeled hardware fields = a spec sheet worth rolling up (one field alone is too
        // thin to trust as a whole-system description).
        return attrs.Count >= 2
            ? new HardwareComponent("System (spec sheet)", Attributes: attrs)
            : null;
    }

    private static SpecCategory? MapLabel(string label)
    {
        foreach (var (rx, category) in LabelMap)
            if (rx.IsMatch(label)) return category;
        return null;
    }
}
