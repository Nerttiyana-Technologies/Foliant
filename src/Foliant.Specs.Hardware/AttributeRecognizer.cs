using System.Text.RegularExpressions;

namespace Foliant.Specs.Hardware;

/// <summary>
/// The shared vocabulary recognizer (ADR-0006 §3.2): the vocabulary is consistent across every
/// federal-solicitation layout even though the layout is not, so a single set of category regexes +
/// a unit normalizer runs over whatever text fragment each strategy yields. A fragment like
/// <c>"64GB DDR5 Memory"</c> becomes <c>SpecAttribute(Memory, "64GB DDR5 Memory", "64 GB", "DDR5")</c>
/// regardless of which strategy found it.
///
/// <para>Conservative by design (the G-precision gate): a capacity-bearing category (Memory / Storage)
/// claims a capacity token only when its OWN keyword is present in the same fragment, so a bare
/// <c>"64 GB"</c> with no context is never fabricated into a spec.</para>
/// </summary>
public static partial class AttributeRecognizer
{
    // ── Capacity / frequency / count primitives ──────────────────────────────
    [GeneratedRegex(@"\b(\d+(?:\.\d+)?)\s*(GB|TB|MB)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CapacityRx();

    [GeneratedRegex(@"\b(\d+(?:\.\d+)?)\s*(GHz|MHz)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ClockRx();

    [GeneratedRegex(@"\b(\d+)\s*[- ]?\s*core[s]?\b", RegexOptions.IgnoreCase)]
    private static partial Regex CoresRx();

    [GeneratedRegex(@"\b(DDR[2-5])\b", RegexOptions.IgnoreCase)]
    private static partial Regex DdrRx();

    [GeneratedRegex(@"\b(\d+)\s*U\b")]   // rack units — case-sensitive 'U' to avoid matching words
    private static partial Regex RackUnitRx();

    [GeneratedRegex(@"\b(\d+)\s*W(?:att)?s?\b", RegexOptions.IgnoreCase)]
    private static partial Regex WattRx();

    [GeneratedRegex(@"\b(\d+(?:\.\d+)?)\s*(?:""|-?inch|-?in\b)", RegexOptions.IgnoreCase)]
    private static partial Regex DisplaySizeRx();

    // ── Category keyword triggers ────────────────────────────────────────────
    [GeneratedRegex(@"\b(processor|cpu|xeon|epyc|ryzen|threadripper|core\s*i[3579]|power\s*1?[0-9]|altra|pentium|celeron|opteron)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessorKwRx();

    [GeneratedRegex(@"\b(memory|ram|dimm|rdimm|udimm|lrdimm|so-?dimm|ecc)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MemoryKwRx();

    [GeneratedRegex(@"\b(ssd|nvme|sata|sas|hdd|m\.2|u\.2|storage|hard\s*drive|solid[- ]state|disk)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StorageKwRx();

    [GeneratedRegex(@"\b(gpu|graphics|video\s*card|rtx|gtx|radeon|geforce|quadro|nvidia|vram|blackwell)\b", RegexOptions.IgnoreCase)]
    private static partial Regex GraphicsKwRx();

    [GeneratedRegex(@"\b(rack[- ]?mount|rackmount|form\s*factor|tower|sff|small\s*form|rugged|desktop\s*chassis|chassis)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FormFactorKwRx();

    [GeneratedRegex(@"\b(power\s*supply|psu|redundant\s*power|hot[- ]?swap)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PowerKwRx();

    [GeneratedRegex(@"\b(ethernet|network\s*interface|network\s*card|nic|10\s*gbe|25\s*gbe|1\s*gbe|sfp\+?|rj-?45)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NetworkKwRx();

    // "resolution" alone is dropped — it collides with legal prose ("dispute resolution"); the screen
    // vocabulary (monitor/display/UHD/…) is specific enough.
    [GeneratedRegex(@"\b(monitor|display|uhd|4k|1080p|1440p|wqhd|led\s*panel)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DisplayKwRx();

    // Bare "windows" is dropped — it collides with building prose ("windows and doors"); a version or
    // "server" qualifier (or another OS name) is required.
    [GeneratedRegex(@"\b(windows\s*(?:11|10|server)|linux|rhel|red\s*hat|ubuntu|operating\s*system)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OsKwRx();

    [GeneratedRegex(@"\b(\d+)[- ]?year[s]?\b(?=[^.]*warrant)|warrant(?:y|ies)", RegexOptions.IgnoreCase)]
    private static partial Regex WarrantyKwRx();

    // ── System-kind vocabulary (ADR-0006 §3.2 — dominant-vocabulary vote) ─────
    [GeneratedRegex(@"\b(server|rack[- ]?mount|rackmount|\d+U\b|blade|poweredge|proliant|power\s*s?1[0-9])\b", RegexOptions.IgnoreCase)]
    private static partial Regex ServerVocabRx();

    [GeneratedRegex(@"\b(workstation|precision|thinkstation)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WorkstationVocabRx();

    [GeneratedRegex(@"\b(laptop|notebook|latitude|elitebook|thinkpad|mobile\s*workstation)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LaptopVocabRx();

    [GeneratedRegex(@"\b(monitor|display\s*panel)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MonitorVocabRx();

    [GeneratedRegex(@"\b(desktop|optiplex|tower\s*pc|mini\s*pc)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DesktopVocabRx();

    /// <summary>
    /// Recognize every hardware attribute expressed in one text fragment (a bullet, a spec-sheet value,
    /// a table cell). Returns an empty list when the fragment carries no recognizable hardware vocabulary.
    /// </summary>
    public static IReadOnlyList<SpecAttribute> Recognize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<SpecAttribute>();
        var raw = text.Trim();
        var attrs = new List<SpecAttribute>();

        // Processor — value = cores + clock when present, else the trigger keyword's neighbourhood.
        if (ProcessorKwRx().IsMatch(raw))
        {
            var parts = new List<string>();
            var cores = CoresRx().Match(raw);
            if (cores.Success) parts.Add($"{cores.Groups[1].Value}-core");
            var clock = ClockRx().Match(raw);
            if (clock.Success) parts.Add(NormalizeUnit(clock.Groups[1].Value, clock.Groups[2].Value));
            attrs.Add(new SpecAttribute(SpecCategory.Processor, raw,
                parts.Count > 0 ? string.Join(", ", parts) : null));
        }

        // Memory — capacity claimed only under a memory keyword; DDR generation is the unit.
        if (MemoryKwRx().IsMatch(raw))
        {
            string? value = FirstCapacity(raw);
            var ddr = DdrRx().Match(raw);
            attrs.Add(new SpecAttribute(SpecCategory.Memory, raw, value,
                ddr.Success ? ddr.Groups[1].Value.ToUpperInvariant() : null));
        }

        // Storage — capacity claimed only under a storage keyword; medium (NVMe/SSD/…) is the unit.
        if (StorageKwRx().IsMatch(raw))
        {
            string? value = FirstCapacity(raw);
            attrs.Add(new SpecAttribute(SpecCategory.Storage, raw, value, StorageMedium(raw)));
        }

        if (GraphicsKwRx().IsMatch(raw))
            attrs.Add(new SpecAttribute(SpecCategory.Graphics, raw, FirstCapacity(raw)));

        // Form factor — a rack-unit height ("3U") is the strongest signal; else the keyword.
        var rack = RackUnitRx().Match(raw);
        if (rack.Success)
            attrs.Add(new SpecAttribute(SpecCategory.FormFactor, raw, $"{rack.Groups[1].Value}U"));
        else if (FormFactorKwRx().IsMatch(raw))
            attrs.Add(new SpecAttribute(SpecCategory.FormFactor, raw));

        if (PowerKwRx().IsMatch(raw))
        {
            var watt = WattRx().Match(raw);
            attrs.Add(new SpecAttribute(SpecCategory.PowerSupply, raw,
                watt.Success ? $"{watt.Groups[1].Value} W" : null));
        }

        if (NetworkKwRx().IsMatch(raw))
            attrs.Add(new SpecAttribute(SpecCategory.Network, raw));

        if (DisplayKwRx().IsMatch(raw))
        {
            var size = DisplaySizeRx().Match(raw);
            attrs.Add(new SpecAttribute(SpecCategory.Display, raw,
                size.Success ? $"{size.Groups[1].Value}\"" : null));
        }

        if (OsKwRx().IsMatch(raw))
            attrs.Add(new SpecAttribute(SpecCategory.OperatingSystem, raw));

        if (WarrantyKwRx().IsMatch(raw))
        {
            var yr = Regex.Match(raw, @"\b(\d+)[- ]?year", RegexOptions.IgnoreCase);
            attrs.Add(new SpecAttribute(SpecCategory.Warranty, raw,
                yr.Success ? $"{yr.Groups[1].Value}-year" : null));
        }

        return attrs;
    }

    /// <summary>True when the fragment carries any recognizable hardware attribute.</summary>
    public static bool HasHardwareVocabulary(string? text) => Recognize(text).Count > 0;

    /// <summary>
    /// Infer the kind of system procured from the dominant vocabulary across all the document's text
    /// (ADR-0006 §3.2). Ties and no-signal fall back to <see cref="SystemKind.Unknown"/>.
    /// </summary>
    public static SystemKind InferSystemKind(IEnumerable<string> texts)
    {
        int server = 0, workstation = 0, laptop = 0, monitor = 0, desktop = 0;
        foreach (var t in texts)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            server += ServerVocabRx().Matches(t).Count;
            workstation += WorkstationVocabRx().Matches(t).Count;
            laptop += LaptopVocabRx().Matches(t).Count;
            monitor += MonitorVocabRx().Matches(t).Count;
            desktop += DesktopVocabRx().Matches(t).Count;
        }

        var scores = new (SystemKind Kind, int Score)[]
        {
            (SystemKind.Server, server),
            (SystemKind.Workstation, workstation),
            (SystemKind.Laptop, laptop),
            (SystemKind.Monitor, monitor),
            (SystemKind.Desktop, desktop),
        };
        int best = scores.Max(s => s.Score);
        if (best == 0) return SystemKind.Unknown;
        // A unique winner only; a tie is ambiguous → Unknown (conservative).
        var winners = scores.Where(s => s.Score == best).ToList();
        return winners.Count == 1 ? winners[0].Kind : SystemKind.Unknown;
    }

    // First capacity token in the fragment, normalized to "<n> <UNIT>". Null when none.
    private static string? FirstCapacity(string text)
    {
        var m = CapacityRx().Match(text);
        return m.Success ? NormalizeUnit(m.Groups[1].Value, m.Groups[2].Value) : null;
    }

    private static string? StorageMedium(string text)
    {
        foreach (var medium in new[] { "NVMe", "SSD", "SATA", "SAS", "HDD", "M.2", "U.2" })
            if (Regex.IsMatch(text, $@"\b{Regex.Escape(medium)}\b", RegexOptions.IgnoreCase))
                return medium;
        return null;
    }

    // Canonical spacing + casing: "64GB" → "64 GB", "3.5ghz" → "3.5 GHz".
    private static string NormalizeUnit(string number, string unit)
    {
        string u = unit.ToUpperInvariant() switch
        {
            "GHZ" => "GHz",
            "MHZ" => "MHz",
            var other => other,   // GB / TB / MB already canonical upper
        };
        return $"{number} {u}";
    }
}
