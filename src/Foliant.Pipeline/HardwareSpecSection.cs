using System.Text;

namespace Foliant.Pipeline;

/// <summary>
/// Renders a document-level <see cref="HardwareSpecProfile"/> as an appended Markdown section
/// (ADR-0006 §3.3). Mirrors <see cref="TemplateFieldSection"/>: an HTML provenance comment, a heading,
/// a paragraph GENERATED FROM the profile (dynamic, per the ADR decision — not fixed boilerplate),
/// and a per-component list. APPENDED at the very bottom of the document Markdown, so it is purely
/// additive and cannot reduce recall or reorder existing content.
/// </summary>
internal static class HardwareSpecSection
{
    public static string Render(HardwareSpecProfile profile)
    {
        var sb = new StringBuilder();
        sb.Append("<!-- Foliant: hardware-spec extractor (confidence ")
          .Append(profile.Confidence.ToString("F2")).Append(") -->\n\n");
        sb.Append("## Hardware Specifications (extracted)\n\n");

        sb.Append(Paragraph(profile)).Append("\n\n");

        foreach (var c in profile.Components)
            sb.Append(ComponentLine(c)).Append('\n');

        return sb.ToString();
    }

    // "This document specifies a server (total quantity 10). Key specifications: processor: 64-core,
    //  2.4 GHz; memory: 512 GB DDR4; storage: 8 TB NVMe."
    private static string Paragraph(HardwareSpecProfile profile)
    {
        var sb = new StringBuilder();
        sb.Append("This document specifies ").Append(KindPhrase(profile.SystemKind));

        int totalQty = profile.Components.Sum(c => c.Quantity ?? 0);
        if (totalQty > 0)
            sb.Append(" (total quantity ").Append(totalQty).Append(')');
        sb.Append('.');

        if (profile.SystemAttributes.Count > 0)
        {
            sb.Append(" Key specifications: ");
            sb.Append(string.Join("; ", profile.SystemAttributes.Select(SpecPhrase)));
            sb.Append('.');
        }
        return sb.ToString();
    }

    private static string SpecPhrase(SpecAttribute a)
    {
        string detail = a.Value ?? Trim(a.RawText);
        string? unit = a.Unit;
        // Don't repeat the unit when it is already inside the value/detail text.
        if (unit is not null && detail.Contains(unit, StringComparison.OrdinalIgnoreCase)) unit = null;
        return $"{CategoryLabel(a.Category)}: {detail}{(unit is null ? "" : $" {unit}")}";
    }

    private static string ComponentLine(HardwareComponent c)
    {
        var sb = new StringBuilder("- ");
        if (c.Quantity is int q) sb.Append("**Qty ").Append(q).Append("** — ");
        sb.Append(Trim(c.Description));
        if (!string.IsNullOrWhiteSpace(c.PartNumber)) sb.Append(" · *part ").Append(c.PartNumber!.Trim()).Append('*');
        return sb.ToString();
    }

    private static string KindPhrase(SystemKind kind) => kind switch
    {
        SystemKind.Server => "a server",
        SystemKind.Desktop => "a desktop",
        SystemKind.Laptop => "a laptop",
        SystemKind.Workstation => "a workstation",
        SystemKind.Monitor => "a monitor",
        SystemKind.Component => "hardware components",
        _ => "hardware",
    };

    private static string CategoryLabel(SpecCategory c) => c switch
    {
        SpecCategory.Processor => "processor",
        SpecCategory.Memory => "memory",
        SpecCategory.Storage => "storage",
        SpecCategory.Graphics => "graphics",
        SpecCategory.Display => "display",
        SpecCategory.Network => "network",
        SpecCategory.PowerSupply => "power supply",
        SpecCategory.FormFactor => "form factor",
        SpecCategory.Motherboard => "motherboard",
        SpecCategory.OperatingSystem => "operating system",
        SpecCategory.Warranty => "warranty",
        SpecCategory.Quantity => "quantity",
        SpecCategory.PartNumber => "part number",
        _ => "other",
    };

    private static string Trim(string s)
    {
        s = s.Trim();
        const int max = 160;
        return s.Length <= max ? s : s[..max].TrimEnd() + "…";
    }
}
