using System.Text.RegularExpressions;

namespace Foliant.Specs.Hardware;

/// <summary>
/// The deterministic, fully-local <see cref="IHardwareSpecExtractor"/> (ADR-0006). Runs three guarded
/// strategies over the composed document — <see cref="TableSpecStrategy"/>,
/// <see cref="KeyValueSpecStrategy"/>, <see cref="ComponentBulletSpecStrategy"/> — merges and dedupes
/// their components, rolls the attributes up to one headline per category, infers the
/// <see cref="SystemKind"/> from the dominant vocabulary, and scores a confidence. A document that
/// describes no hardware yields <see cref="HardwareSpecProfile.Empty"/>, so nothing is appended.
///
/// <para>Deterministic-first per the ADR: a learned / LLM backend can replace this class behind the
/// seam with zero downstream change (the flag, the renderer, and the append point stay put).</para>
/// </summary>
public sealed class HardwareSpecExtractor : IHardwareSpecExtractor
{
    /// <inheritdoc />
    public HardwareSpecProfile Extract(IReadOnlyList<PageResult> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);

        var components = new List<HardwareComponent>();
        components.AddRange(TableSpecStrategy.Extract(pages));
        components.AddRange(ComponentBulletSpecStrategy.Extract(pages));
        if (KeyValueSpecStrategy.Extract(pages) is { } specSheet)
            components.Add(specSheet);

        components = Dedupe(components);
        if (components.Count == 0) return HardwareSpecProfile.Empty;

        var systemAttributes = RollUp(components);
        var kind = AttributeRecognizer.InferSystemKind(
            components.Select(c => c.Description)
                      .Concat(components.SelectMany(c => c.Attributes ?? Array.Empty<SpecAttribute>())
                                        .Select(a => a.RawText)));

        return new HardwareSpecProfile(kind, components, systemAttributes, Confidence(components, systemAttributes));
    }

    // Merge components describing the same thing (same normalized description). Prefer the richer of
    // each scalar field; union the attributes by category+value.
    private static List<HardwareComponent> Dedupe(List<HardwareComponent> components)
    {
        var merged = new List<HardwareComponent>();
        foreach (var c in components)
        {
            int i = merged.FindIndex(m => Norm(m.Description) == Norm(c.Description));
            if (i < 0) { merged.Add(c); continue; }

            var e = merged[i];
            merged[i] = new HardwareComponent(
                Description: e.Description.Length >= c.Description.Length ? e.Description : c.Description,
                Quantity: e.Quantity ?? c.Quantity,
                PartNumber: e.PartNumber ?? c.PartNumber,
                UnitOfIssue: e.UnitOfIssue ?? c.UnitOfIssue,
                Attributes: UnionAttributes(e.Attributes, c.Attributes));
        }
        return merged;
    }

    private static IReadOnlyList<SpecAttribute>? UnionAttributes(
        IReadOnlyList<SpecAttribute>? a, IReadOnlyList<SpecAttribute>? b)
    {
        if (a is null || a.Count == 0) return b;
        if (b is null || b.Count == 0) return a;
        var list = a.ToList();
        foreach (var attr in b)
            if (!list.Any(x => x.Category == attr.Category && x.Value == attr.Value))
                list.Add(attr);
        return list;
    }

    // One headline attribute per category across every component — the CPU/RAM/Storage/GPU/… summary.
    // Prefer an attribute that parsed a concrete Value; longest RawText breaks ties.
    private static IReadOnlyList<SpecAttribute> RollUp(IEnumerable<HardwareComponent> components)
    {
        return components
            .SelectMany(c => c.Attributes ?? Array.Empty<SpecAttribute>())
            .GroupBy(a => a.Category)
            .Select(g => g
                .OrderByDescending(a => a.Value is not null)
                .ThenByDescending(a => a.RawText.Length)
                .First())
            .OrderBy(a => (int)a.Category)
            .ToList();
    }

    // Deterministic confidence: grows with vocabulary breadth (distinct categories) and corroboration
    // (component count), capped so deterministic extraction never claims certainty.
    private static double Confidence(
        IReadOnlyList<HardwareComponent> components, IReadOnlyList<SpecAttribute> systemAttributes)
    {
        double score = 0.4 + 0.08 * systemAttributes.Count + 0.03 * Math.Min(components.Count, 6);
        return Math.Round(Math.Min(0.95, score), 2);
    }

    private static string Norm(string s) => Regex.Replace(s, @"\s+", " ").Trim().ToLowerInvariant();
}
