using Foliant;
using Foliant.Templates;
using Xunit;

namespace Foliant.Tests;

// OCR-free checkbox detection: a marked box has dark ink in its interior; an empty box (just its printed
// border) does not. The detector insets past the border so the always-dark outline never counts.
public sealed class CheckboxPixelDetectorTests
{
    private const int W = 100, H = 100;
    private static readonly NormalizedRect Box = new(0.20f, 0.20f, 0.40f, 0.40f);  // pixels (20,20)-(40,40)

    private static byte[] WhitePage()
    {
        var b = new byte[W * H * 4];
        Array.Fill(b, (byte)255);   // BGRA all-white, opaque
        return b;
    }

    private static void SetBlack(byte[] b, int x, int y)
    {
        int p = (y * W + x) * 4;
        b[p] = b[p + 1] = b[p + 2] = 0; b[p + 3] = 255;
    }

    private static void DrawBorder(byte[] b)
    {
        for (int x = 20; x < 40; x++) { SetBlack(b, x, 20); SetBlack(b, x, 39); }
        for (int y = 20; y < 40; y++) { SetBlack(b, 20, y); SetBlack(b, 39, y); }
    }

    [Fact]
    public void EmptyBox_BorderOnly_IsNotChecked()
    {
        var b = WhitePage();
        DrawBorder(b);   // border is outside the inset interior → ignored
        Assert.False(CheckboxPixelDetector.IsChecked(new PageImage(W, H, 150, b), Box));
    }

    [Fact]
    public void MarkedBox_InteriorInk_IsChecked()
    {
        var b = WhitePage();
        DrawBorder(b);
        for (int y = 26; y < 34; y++)        // an 8×8 mark inside the box
            for (int x = 26; x < 34; x++)
                SetBlack(b, x, y);
        Assert.True(CheckboxPixelDetector.IsChecked(new PageImage(W, H, 150, b), Box));
    }
}
