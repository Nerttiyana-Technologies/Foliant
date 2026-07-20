using Foliant;
using Foliant.Pipeline;
using Foliant.Specs.Hardware;
using Xunit;

namespace Foliant.Tests;

/// <summary>
/// ADR-0006 hardware-spec extraction. Exercises the shared attribute recognizer, the three strategies,
/// the merging extractor, and the appended-section renderer over synthetic composed pages — no ONNX
/// models or PDFs required (the golden-Markdown-per-fixture tests need the gitignored data/price/ corpus
/// and run in the verification harness).
/// </summary>
public class HardwareSpecTests
{
    // ── Test helpers ─────────────────────────────────────────────────────────
    private static readonly BoundingBox Box = new(0, 0, 100, 20);

    private static TextLine Line(string text) => new(Box, text, 1f, TextSource.TextLayer);

    private static PageResult Page(IEnumerable<string> lines, IEnumerable<Region>? regions = null) =>
        new(
            PageNumber: 1, WidthPx: 1000, HeightPx: 1300, Dpi: 300,
            Regions: (regions ?? Array.Empty<Region>()).ToList(),
            Lines: lines.Select(Line).ToList(),
            PageFurniture: Array.Empty<TextLine>(),
            Source: TextSource.TextLayer,
            Markdown: string.Join("\n", lines),
            Verification: new PageVerification(0, 0, 0, 0));

    // A page whose composed Markdown differs from its raw Lines (the born-digital split-bullet case).
    private static PageResult PageMd(string markdown) =>
        new(
            PageNumber: 1, WidthPx: 1000, HeightPx: 1300, Dpi: 300,
            Regions: Array.Empty<Region>(), Lines: Array.Empty<TextLine>(),
            PageFurniture: Array.Empty<TextLine>(), Source: TextSource.TextLayer,
            Markdown: markdown, Verification: new PageVerification(0, 0, 0, 0));

    private static Region TableRegion(string[][] rows)
    {
        var cells = new List<TableCell>();
        int cols = rows.Max(r => r.Length);
        for (int r = 0; r < rows.Length; r++)
            for (int c = 0; c < rows[r].Length; c++)
                cells.Add(new TableCell(r, c, rows[r][c], Box));
        return new Region(RegionType.Table, "", Box, "", new TableStructure(rows.Length, cols, cells), 1f);
    }

    // ── Attribute recognizer: unit normalization ─────────────────────────────
    [Theory]
    [InlineData("64GB DDR5 Memory", "64 GB", "DDR5")]
    [InlineData("512GB DDR4 ECC memory", "512 GB", "DDR4")]
    [InlineData("Memory: 32 GB RAM", "32 GB", null)]
    public void Recognizer_normalizes_memory_capacity_and_generation(string text, string value, string? unit)
    {
        var mem = Assert.Single(AttributeRecognizer.Recognize(text).Where(a => a.Category == SpecCategory.Memory));
        Assert.Equal(value, mem.Value);
        Assert.Equal(unit, mem.Unit);
    }

    [Fact]
    public void Recognizer_reads_storage_capacity_and_medium()
    {
        var attr = Assert.Single(AttributeRecognizer.Recognize("102TB NVMe U.2 storage")
            .Where(a => a.Category == SpecCategory.Storage));
        Assert.Equal("102 TB", attr.Value);
        Assert.Equal("NVMe", attr.Unit);
    }

    [Fact]
    public void Recognizer_reads_processor_cores_and_clock()
    {
        var cpu = Assert.Single(AttributeRecognizer.Recognize("Intel Xeon W-3335, 16-core, 3.4 GHz")
            .Where(a => a.Category == SpecCategory.Processor));
        Assert.Contains("16-core", cpu.Value);
        Assert.Contains("3.4 GHz", cpu.Value);
    }

    [Fact]
    public void Recognizer_reads_rack_form_factor()
    {
        var ff = Assert.Single(AttributeRecognizer.Recognize("3U rack-mounted server chassis")
            .Where(a => a.Category == SpecCategory.FormFactor));
        Assert.Equal("3U", ff.Value);
    }

    // ── Attribute recognizer: precision (no fabrication) ─────────────────────
    [Theory]
    [InlineData("The Contractor shall deliver all items FOB destination.")]
    [InlineData("Period of performance is 12 months from award.")]
    [InlineData("See Section C for the statement of work.")]
    public void Recognizer_finds_nothing_in_hardware_free_prose(string text)
    {
        Assert.Empty(AttributeRecognizer.Recognize(text));
        Assert.False(AttributeRecognizer.HasHardwareVocabulary(text));
    }

    [Fact]
    public void Recognizer_does_not_claim_bare_capacity_without_context()
    {
        // "8 GB" with no memory/storage keyword must not be fabricated into a spec.
        Assert.Empty(AttributeRecognizer.Recognize("The file is 8 GB in size."));
    }

    [Fact]
    public void InferSystemKind_votes_server_from_dominant_vocabulary()
    {
        var kind = AttributeRecognizer.InferSystemKind(new[]
        {
            "IBM Power S1014 Server", "3U rack-mount chassis", "redundant power supply",
        });
        Assert.Equal(SystemKind.Server, kind);
    }

    [Fact]
    public void InferSystemKind_is_Unknown_with_no_signal() =>
        Assert.Equal(SystemKind.Unknown, AttributeRecognizer.InferSystemKind(new[] { "prose", "more prose" }));

    // ── Table strategy ───────────────────────────────────────────────────────
    [Fact]
    public void TableStrategy_reads_a_clin_row_into_a_component()
    {
        var page = Page(Array.Empty<string>(), new[]
        {
            TableRegion(new[]
            {
                new[] { "CLIN", "Description", "Qty", "Unit", "Part No" },
                new[] { "0005", "64GB MEMORY DIMM, RDIMM, DDR5", "8", "EA", "MEM-64-D5" },
            }),
        });

        var component = Assert.Single(TableSpecStrategy.Extract(new[] { page }));
        Assert.Equal(8, component.Quantity);
        Assert.Equal("EA", component.UnitOfIssue);
        Assert.Equal("MEM-64-D5", component.PartNumber);
        Assert.Contains(component.Attributes!, a => a.Category == SpecCategory.Memory && a.Value == "64 GB");
    }

    [Fact]
    public void TableStrategy_finds_a_header_below_a_blank_spacer_row()
    {
        // Real fixture (C07): a blank spacer row sits above the real "Description | Part Number | Qty"
        // header, so the header is not row 0.
        var page = Page(Array.Empty<string>(), new[]
        {
            TableRegion(new[]
            {
                new[] { "", "", "", "" },                            // blank spacer row (row 0)
                new[] { "", "Description", "Part Number", "Qty" },   // real header (row 1)
                new[] { "", "Portable Rugged Dual 24\" 4K Display Monitor Workstation, 2U hardcase", "FAK-1", "20" },
            }),
        });

        var component = Assert.Single(TableSpecStrategy.Extract(new[] { page }));
        Assert.Equal(20, component.Quantity);
        Assert.Equal("FAK-1", component.PartNumber);
        Assert.Contains(component.Attributes!, a => a.Category == SpecCategory.Display);
        Assert.Contains(component.Attributes!, a => a.Category == SpecCategory.FormFactor);
    }

    [Fact]
    public void TableStrategy_skips_rows_with_no_hardware_vocabulary()
    {
        var page = Page(Array.Empty<string>(), new[]
        {
            TableRegion(new[]
            {
                new[] { "Item", "Description", "Qty" },
                new[] { "1", "Project management services", "1" },
                new[] { "2", "Travel and per diem", "1" },
            }),
        });
        Assert.Empty(TableSpecStrategy.Extract(new[] { page }));
    }

    // ── Key-value strategy ───────────────────────────────────────────────────
    [Fact]
    public void KeyValueStrategy_rolls_labeled_fields_into_one_component()
    {
        var page = Page(new[]
        {
            "- Processor: Intel Xeon W-3335 (16-core)",
            "- Memory: 512GB DDR4 ECC",
            "- Storage: 2TB NVMe SSD",
            "- Notes: contractor shall provide installation",   // not a hardware label → ignored
        });

        var component = KeyValueSpecStrategy.Extract(new[] { page });
        Assert.NotNull(component);
        var cats = component!.Attributes!.Select(a => a.Category).ToList();
        Assert.Contains(SpecCategory.Processor, cats);
        Assert.Contains(SpecCategory.Memory, cats);
        Assert.Contains(SpecCategory.Storage, cats);
        Assert.DoesNotContain(SpecCategory.Other, cats);
    }

    [Fact]
    public void KeyValueStrategy_needs_two_hardware_fields()
    {
        var page = Page(new[] { "- Processor: something", "- Delivery: 30 days ARO" });
        Assert.Null(KeyValueSpecStrategy.Extract(new[] { page }));
    }

    // ── Component-bullet strategy ─────────────────────────────────────────────
    [Fact]
    public void BulletStrategy_reads_component_bullets()
    {
        var page = Page(new[]
        {
            "2.1 Hardware Implementation",
            "• IBM Power S1014 Server (Power10, 8-core)",
            "• 64GB DDR5 Memory",
            "• The vendor shall coordinate delivery",   // no hardware vocabulary → skipped
        });

        var components = ComponentBulletSpecStrategy.Extract(new[] { page }).ToList();
        Assert.Equal(2, components.Count);
        Assert.Contains(components, c => c.Description.Contains("Power S1014"));
        Assert.Contains(components, c => c.Attributes!.Any(a => a.Category == SpecCategory.Memory));
    }

    [Fact]
    public void BulletStrategy_reads_from_composed_markdown_not_raw_lines()
    {
        // Real fixture (RPMS SOW): the born-digital text layer emits the bullet glyph as a separate run,
        // so raw Lines split "• <item>" in two; only the composed Markdown re-joins them. Lines are empty
        // here — the bullets live only in the Markdown.
        var page = PageMd("• IBM Power S1014 Server (Power10, 8-core processor)\n• 64GB DDR5 Memory");
        var components = ComponentBulletSpecStrategy.Extract(new[] { page }).ToList();
        Assert.Equal(2, components.Count);
        Assert.Contains(components, c => c.Attributes!.Any(a => a.Category == SpecCategory.Processor));
        Assert.Contains(components, c => c.Attributes!.Any(a => a.Category == SpecCategory.Memory));
    }

    [Fact]
    public void BulletStrategy_ignores_key_value_lines()
    {
        // A "- Label: value" line is the key-value strategy's turf; the bullet strategy must skip it.
        var page = Page(new[] { "- Processor: Intel Xeon 8-core" });
        Assert.Empty(ComponentBulletSpecStrategy.Extract(new[] { page }));
    }

    // ── Full extractor: merge + roll-up + system kind ────────────────────────
    [Fact]
    public void Extractor_produces_empty_profile_for_hardware_free_document()
    {
        var page = Page(new[]
        {
            "SECTION C — STATEMENT OF WORK",
            "The Contractor shall provide program management support.",
            "Period of performance: 12 months.",
        });
        var profile = new HardwareSpecExtractor().Extract(new[] { page });
        Assert.Empty(profile.Components);
        Assert.Equal(SystemKind.Unknown, profile.SystemKind);
    }

    [Fact]
    public void Extractor_rolls_up_headline_attributes_and_infers_server()
    {
        var page = Page(new[]
        {
            "2.1 Hardware Implementation",
            "• IBM Power S1014 Server (Power10, 8-core)",
            "• 64GB DDR5 Memory",
            "• 3U rack-mount chassis with redundant power supply",
        });

        var profile = new HardwareSpecExtractor().Extract(new[] { page });
        Assert.NotEmpty(profile.Components);
        Assert.Equal(SystemKind.Server, profile.SystemKind);
        var cats = profile.SystemAttributes.Select(a => a.Category).ToList();
        Assert.Contains(SpecCategory.Processor, cats);
        Assert.Contains(SpecCategory.Memory, cats);
        Assert.Contains(SpecCategory.FormFactor, cats);
        Assert.InRange(profile.Confidence, 0.4, 0.95);
    }

    [Fact]
    public void Extractor_dedupes_components_with_the_same_description()
    {
        // Same bullet on two pages → one merged component, not two.
        var lines = new[] { "• 64GB DDR5 Memory" };
        var profile = new HardwareSpecExtractor().Extract(new[] { Page(lines), Page(lines) });
        Assert.Single(profile.Components);
    }

    // ── Renderer golden ──────────────────────────────────────────────────────
    [Fact]
    public void Renderer_produces_the_expected_section()
    {
        var profile = new HardwareSpecProfile(
            SystemKind.Server,
            new[] { new HardwareComponent("RS3700 Series Server", Quantity: 2, PartNumber: "RS3700") },
            new[] { new SpecAttribute(SpecCategory.Memory, "512GB DDR4", "512 GB", "DDR4") },
            0.86);

        string expected =
            "<!-- Foliant: hardware-spec extractor (confidence 0.86) -->\n\n" +
            "## Hardware Specifications (extracted)\n\n" +
            "This document specifies a server (total quantity 2). " +
            "Key specifications: memory: 512 GB DDR4.\n\n" +
            "- **Qty 2** — RS3700 Series Server · *part RS3700*\n";

        Assert.Equal(expected, HardwareSpecSection.Render(profile));
    }

    [Fact]
    public void Renderer_omits_quantity_and_part_when_absent()
    {
        var profile = new HardwareSpecProfile(
            SystemKind.Unknown,
            new[] { new HardwareComponent("64 GB DDR5 memory") },
            Array.Empty<SpecAttribute>(),
            0.5);

        string md = HardwareSpecSection.Render(profile);
        Assert.Contains("This document specifies hardware.", md);
        Assert.DoesNotContain("total quantity", md);
        Assert.Contains("- 64 GB DDR5 memory\n", md);
        Assert.DoesNotContain("part", md);
    }
}
