namespace Foliant;

/// <summary>Where a text line's characters came from.</summary>
public enum TextSource
{
    /// <summary>Recognized from pixels by an OCR engine.</summary>
    Ocr,

    /// <summary>Taken verbatim from the PDF's embedded text layer (born-digital fast path).</summary>
    TextLayer,
}

/// <summary>A fragment of text with its bounds in page raster coordinates.</summary>
public sealed record TextLine(
    BoundingBox Bounds,
    string Text,
    float Confidence,
    TextSource Source);
