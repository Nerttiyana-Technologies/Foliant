using Foliant;
using Foliant.Pipeline;
using Xunit;

namespace Foliant.Tests;

public sealed class FormIdentifierTests
{
    private static TextLine[] Lines(params string[] texts) =>
        texts.Select((t, i) => new TextLine(new BoundingBox(0, i * 10, 100, i * 10 + 8), t, 1f, TextSource.TextLayer))
             .ToArray();

    [Theory]
    [InlineData("STANDARD FORM 1449 (REV. 11/2021)", "1449")]
    [InlineData("STANDARD FORM 33", "33")]
    [InlineData("STANDARD FORM 25-B", "25B")]
    [InlineData("OPTIONAL FORM 347", "347")]
    public void Identify_ParsesPrintedDesignation(string text, string expected)
    {
        Assert.Equal(expected, FormIdentifier.Identify(Lines("some header", text, "footer")));
    }

    [Fact]
    public void Identify_NonForm_ReturnsNull()
    {
        // No "STANDARD/OPTIONAL FORM <n>" designation → abstain. A bare "30 days" must NOT match.
        Assert.Null(FormIdentifier.Identify(Lines("INVOICE", "Total due in 30 days", "Thank you")));
    }

    [Fact]
    public void IsFederalForm_TrueOnlyWithDesignation()
    {
        Assert.True(FormIdentifier.IsFederalForm(Lines("STANDARD FORM 1449")));
        Assert.False(FormIdentifier.IsFederalForm(Lines("just a memo about form policy")));
    }
}
