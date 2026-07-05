using Foliant.Pipeline;
using Xunit;

namespace Foliant.Tests;

// Sensitivity-marking detection (advisory CUI/legacy/classification flag). Patterns follow the
// GSA CUI Guide (1-31-2024) and the DoD CUI Markings training aid (Dec 2024): banner markings at
// page top/bottom, CUI//category strings, the designation-indicator block, legacy dissemination
// controls, DoDI 5230.24 distribution statements, and classification banners. Precision matters
// as much as recall here: ordinary prose containing "confidential" or "controlled" must NOT flag.
public class SensitivityMarkingDetectorTests
{
    private const int PageH = 1000;

    private static TextLine Banner(string text, float y = 10) =>
        new(new BoundingBox(400, y, 600, y + 20), text, 0.95f, TextSource.TextLayer);

    private static TextLine Body(string text, float y = 500) =>
        new(new BoundingBox(50, y, 900, y + 20), text, 0.95f, TextSource.TextLayer);

    private static readonly IReadOnlyList<TextLine> NoFurniture = Array.Empty<TextLine>();

    private static string? Detect(params TextLine[] lines) =>
        SensitivityMarkingDetector.Detect(lines, NoFurniture, PageH);

    // ── CUI (32 CFR 2002 / GSA / DoD) ────────────────────────────────────────

    [Theory]
    [InlineData("CUI", "CUI")]
    [InlineData("CUI//SP-PRVCY", "CUI//SP-PRVCY")]
    [InlineData("CUI//SP-PRVCY/PROC//FEDCON", "CUI//SP-PRVCY/PROC//FEDCON")]
    [InlineData("CONTROLLED UNCLASSIFIED INFORMATION", "CONTROLLED UNCLASSIFIED INFORMATION")]
    [InlineData("CONTROLLED", "CONTROLLED")]
    [InlineData("U//CUI", "U//CUI")]
    public void CuiBanners_AtPageTop_AreDetected(string banner, string expectedContains)
    {
        string? marking = Detect(Banner(banner));
        Assert.NotNull(marking);
        Assert.Contains(expectedContains, marking);
    }

    [Fact]
    public void CuiBanner_AtPageBottom_IsDetected()
    {
        Assert.NotNull(Detect(Banner("CUI", y: 975)));
    }

    [Fact]
    public void CuiBanner_InPageFurniture_IsDetected()
    {
        // Furniture is considered wherever it sits — headers/footers are the banner's home.
        string? marking = SensitivityMarkingDetector.Detect(
            Array.Empty<TextLine>(), new[] { Banner("CUI//BUDG", y: 480) }, PageH);
        Assert.Equal("CUI//BUDG", marking);
    }

    [Theory]
    [InlineData("Controlled by: DDI(CL&S)/IAP")]
    [InlineData("CUI Category: BUDG, PSI")]
    [InlineData("Limited Dissemination Control: FEDCON")]
    [InlineData("LDC: REL TO USA, FVEY")]
    public void DesignationIndicatorLines_AreDetected(string line)
    {
        Assert.NotNull(Detect(Banner(line, y: 950)));
    }

    // ── Legacy + distribution statements ─────────────────────────────────────

    [Theory]
    [InlineData("FOR OFFICIAL USE ONLY")]
    [InlineData("UNCLASSIFIED//FOUO")]
    [InlineData("LAW ENFORCEMENT SENSITIVE")]
    [InlineData("SBU")]
    public void LegacyMarkings_AreDetected(string banner)
    {
        Assert.NotNull(Detect(Banner(banner)));
    }

    [Fact]
    public void DistributionStatementC_IsDetected_DespiteLength()
    {
        string stmt = "Distribution Statement C: Distribution authorized to U.S. Government " +
                      "agencies and their contractors. Other requests shall be referred to the office.";
        Assert.Equal("DISTRIBUTION STATEMENT C", Detect(Banner(stmt, y: 960)));
    }

    [Fact]
    public void DistributionStatementA_PublicRelease_IsNotFlagged()
    {
        Assert.Null(Detect(Banner("Distribution Statement A: Approved for public release.", y: 960)));
    }

    // ── Classification banners ───────────────────────────────────────────────

    [Theory]
    [InlineData("TOP SECRET")]
    [InlineData("SECRET//NOFORN")]
    [InlineData("CONFIDENTIAL")]
    public void ClassificationBanners_AreDetected(string banner)
    {
        Assert.Equal(banner, Detect(Banner(banner)));
    }

    [Fact]
    public void Severity_ClassificationOutranksCui()
    {
        string? marking = Detect(Banner("CUI"), Banner("SECRET", y: 975));
        Assert.Equal("SECRET", marking);
    }

    // ── Precision: prose and body text must NOT flag ─────────────────────────

    [Theory]
    [InlineData("This proposal is confidential and proprietary.")]      // lowercase prose
    [InlineData("the secret to our success is quality")]                // prose containing "secret"
    [InlineData("Controlled substances are regulated by the DEA")]      // "controlled" in prose
    [InlineData("SECURITY DEPOSIT")]                                    // all-caps but not a marking
    public void OrdinaryBannerText_DoesNotFlag(string banner)
    {
        Assert.Null(Detect(Banner(banner)));
    }

    [Fact]
    public void CuiWordInBodyText_MidPage_DoesNotFlag()
    {
        // "CUI" mentioned in body prose (e.g. this very documentation) — not in the banner band.
        Assert.Null(Detect(Body("The CUI program is described in 32 CFR 2002.")));
    }

    [Fact]
    public void CleanPage_ReturnsNull()
    {
        Assert.Null(Detect(Banner("Quarterly Report 2026"), Body("Nothing sensitive here.")));
    }
}
