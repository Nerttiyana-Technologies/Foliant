using ZD = ZeroDep.Abstractions;

namespace Foliant.Orchestration.Tests;

/// <summary>Factories for constructing ZeroDep analysis inputs and a fake Foliant pipeline — no real PDFs.</summary>
internal static class TestData
{
    public static ZD.PageClassification PC(
        int index, ZD.PageContentClass cls, double confidence = 0.95, int textRuns = 0, int rulingLines = 0,
        double textDecodeConfidence = 1.0)
        => new()
        {
            PageIndex = index, Class = cls, Confidence = confidence,
            Signals = new ZD.PageSignals
            {
                TextRunCount = textRuns, RulingLineCount = rulingLines, TextDecodeConfidence = textDecodeConfidence,
            },
        };

    public static ZD.TextRunInfo Run(
        int pageIndex, string text, double x = 72, double y = 700, double width = 100, double fontSize = 10,
        ZD.TextSource source = ZD.TextSource.Embedded, bool ocrLayer = false)
        => new()
        {
            PageIndex = pageIndex, Text = text, X = x, Y = y, Width = width, FontSize = fontSize,
            Source = source, IsOcrLayer = ocrLayer, Confidence = 1.0,
        };

    public static ZD.FormFieldInfo Field(
        int pageIndex, string fqn, string? value, string type = "Tx", bool? isChecked = null,
        string? label = null, string? partial = null)
        => new()
        {
            FullyQualifiedName = fqn, PartialName = partial, Label = label, FieldType = type,
            Value = value, IsChecked = isChecked, PageIndex = pageIndex,
        };

    public static ZD.DocumentAnalysis Analysis(
        IEnumerable<ZD.PageClassification> pages,
        IEnumerable<ZD.TextRunInfo>? runs = null,
        IEnumerable<ZD.FormFieldInfo>? fields = null,
        bool xfa = false,
        ZD.DocumentStatus status = ZD.DocumentStatus.Processed,
        int? pageCount = null)
    {
        var pageList = pages.ToList();
        var fieldList = (fields ?? Array.Empty<ZD.FormFieldInfo>()).ToList();
        return new ZD.DocumentAnalysis
        {
            Status = status,
            PageCount = pageCount ?? pageList.Count,
            Pages = pageList,
            TextRuns = (runs ?? Array.Empty<ZD.TextRunInfo>()).ToList(),
            Form = new ZD.AcroFormReport { HasAcroForm = fieldList.Count > 0, HasXfa = xfa, Fields = fieldList },
        };
    }

    /// <summary>A stand-in heavy-lane page result (what the Foliant pipeline would return).</summary>
    public static PageResult HeavyPage(int n) => new(
        n, 100, 100, 300, Array.Empty<Region>(), Array.Empty<TextLine>(), Array.Empty<TextLine>(),
        TextSource.Ocr, $"HEAVY p{n}", new PageVerification(0, 0, 0, 0));
}

/// <summary>A fake <see cref="IDocumentProcessor"/> that records how it was called and returns scripted pages.</summary>
internal sealed class FakeDocumentProcessor : IDocumentProcessor
{
    public int Calls { get; private set; }
    public bool StreamCalled { get; private set; }
    public ProcessingOptions? LastOptions { get; private set; }

    /// <summary>Builds the result from the options it received (defaults to whole-doc heavy pages 1..N when set).</summary>
    public Func<ProcessingOptions?, DocumentResult>? Handler { get; init; }

    public Task<DocumentResult> ProcessAsync(byte[] pdf, ProcessingOptions? options = null, CancellationToken ct = default)
    {
        Calls++;
        LastOptions = options;
        return Task.FromResult(Handler?.Invoke(options) ?? new DocumentResult(Array.Empty<PageResult>(), ""));
    }

    public Task<DocumentResult> ProcessAsync(Stream pdf, ProcessingOptions? options = null, CancellationToken ct = default)
    {
        Calls++;
        StreamCalled = true;
        LastOptions = options;
        return Task.FromResult(Handler?.Invoke(options) ?? new DocumentResult(Array.Empty<PageResult>(), ""));
    }
}
