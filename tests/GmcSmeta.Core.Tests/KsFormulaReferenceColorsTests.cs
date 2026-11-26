using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

/// <summary>Python 0.67.9 ks_view.py QtyDelegate REFERENCE_COLORS — паритет палитры,
/// порядка, fill alpha и ширины рамки.</summary>
public sealed class KsFormulaReferenceColorsTests
{
    private static readonly string[] PythonPalette =
    {
        "#2563EB", "#DC2626", "#16803C", "#9333EA",
        "#C2410C", "#087F8C", "#A21CAF", "#946200"
    };

    [Fact]
    public void Palette_ExactPythonOrder()
    {
        Assert.Equal(PythonPalette, KsFormulaReferenceColors.Palette);
        Assert.Equal(8, KsFormulaReferenceColors.Palette.Count);
    }

    [Fact]
    public void ShadeAlpha_MatchesPythonSetAlpha30()
    {
        Assert.Equal(30, KsFormulaReferenceColors.ShadeAlpha);
    }

    [Fact]
    public void BorderThickness_MatchesPythonPenWidth2()
    {
        Assert.Equal(2, KsFormulaReferenceColors.BorderThickness);
    }

    [Fact]
    public void ColorAt_CyclesInTypingOrder()
    {
        // Python: colors = {pid: REFERENCE_COLORS[i % len(...)] for i, pid in enumerate(refs)}
        for (var i = 0; i < 16; i++)
            Assert.Equal(PythonPalette[i % PythonPalette.Length], KsFormulaReferenceColors.ColorAt(i));
    }

    [Fact]
    public void ShadeAt_HasAlpha30AndMatchingRgb()
    {
        for (var i = 0; i < PythonPalette.Length; i++)
        {
            var (a, r, g, b) = KsFormulaReferenceColors.ShadeAt(i);
            Assert.Equal((byte)30, a);
            Assert.Equal(Convert.ToByte(PythonPalette[i].Substring(1, 2), 16), r);
            Assert.Equal(Convert.ToByte(PythonPalette[i].Substring(3, 2), 16), g);
            Assert.Equal(Convert.ToByte(PythonPalette[i].Substring(5, 2), 16), b);
        }
    }

    [Fact]
    public void RgbAt_ParsesHex()
    {
        Assert.Equal((0x25, 0x63, 0xEB), KsFormulaReferenceColors.RgbAt(0));
        Assert.Equal((0x94, 0x62, 0x00), KsFormulaReferenceColors.RgbAt(7));
    }

    [Fact]
    public void ColorAt_NegativeIndex_Wraps()
    {
        Assert.Equal(KsFormulaReferenceColors.ColorAt(0), KsFormulaReferenceColors.ColorAt(-8));
    }
}
