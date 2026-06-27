using Xunit;
using ZeroDep.Abstractions;

namespace Foliant.Orchestration.Tests;

/// <summary>
/// Confirms the one intentional ZeroDep binding — the <see cref="PageContentClass"/> → <see cref="PageKind"/>
/// map — compiles against the published package and is total (every enum value maps, unknown → escalate).
/// If the ZeroDep enum shape changed, these tests fail loudly at the single binding point.
/// </summary>
public sealed class PageKindMappingTests
{
    [Theory]
    [InlineData(PageContentClass.Empty, PageKind.Empty)]
    [InlineData(PageContentClass.DigitalText, PageKind.DigitalText)]
    [InlineData(PageContentClass.FormPage, PageKind.FormPage)]
    [InlineData(PageContentClass.TableOrComplexLayout, PageKind.TableOrComplexLayout)]
    [InlineData(PageContentClass.ScannedImageOnly, PageKind.ScannedImageOnly)]
    [InlineData(PageContentClass.ScannedWithOcr, PageKind.ScannedWithOcr)]
    [InlineData(PageContentClass.Mixed, PageKind.Mixed)]
    public void Maps_each_ZeroDep_class(PageContentClass input, PageKind expected)
        => Assert.Equal(expected, PageKindMapping.FromZeroDep(input));

    [Fact]
    public void Every_ZeroDep_class_value_is_mapped()
    {
        foreach (PageContentClass cls in Enum.GetValues<PageContentClass>())
        {
            // Must not throw and must yield a defined PageKind for every published value.
            var kind = PageKindMapping.FromZeroDep(cls);
            Assert.True(Enum.IsDefined(kind));
        }
    }
}
