using TNDrop.Core;

public class IndicatorPaletteTests
{
    // 設計書パート1: 3 色は 1 回の Resolve から導出され、縁は塗りより暗く、
    // リムは塗りより明るい (BT.601 輝度で比較)。
    [Fact]
    public void Resolve_outline_is_darker_and_rim_is_lighter_than_fill()
    {
        var (fill, outline, rim) = IndicatorPalette.Resolve(0x5A, 0xC8, 0xFA);
        Assert.True(IndicatorPalette.Luminance(outline) < IndicatorPalette.Luminance(fill));
        Assert.True(IndicatorPalette.Luminance(rim) > IndicatorPalette.Luminance(fill));
    }

    [Fact]
    public void Resolve_fill_is_the_base_color_unchanged()
    {
        var (fill, _, _) = IndicatorPalette.Resolve(0x0A, 0x84, 0xFF);
        Assert.Equal(new IndicatorPalette.Rgb(0x0A, 0x84, 0xFF), fill);
    }

    // 端の色でも破綻しない: 黒はこれ以上暗くできないので outline == fill (輝度 0)、
    // 白はこれ以上明るくできないので rim == fill (輝度 255)。<= / >= で許容する。
    [Fact]
    public void Resolve_black_and_white_do_not_overflow()
    {
        var black = IndicatorPalette.Resolve(0, 0, 0);
        Assert.True(IndicatorPalette.Luminance(black.Outline) <= IndicatorPalette.Luminance(black.Fill));
        Assert.True(IndicatorPalette.Luminance(black.Rim) > 0);

        var white = IndicatorPalette.Resolve(255, 255, 255);
        Assert.True(IndicatorPalette.Luminance(white.Outline) < 255);
        Assert.True(IndicatorPalette.Luminance(white.Rim) <= IndicatorPalette.Luminance(white.Fill));
    }

    [Theory]
    [InlineData("#5AC8FA", true, 0x5A, 0xC8, 0xFA)]
    [InlineData("#0a84ff", true, 0x0A, 0x84, 0xFF)] // 小文字も受理
    [InlineData("5AC8FA", false, 0, 0, 0)]          // 先頭 # なしは拒否
    [InlineData("#5AC8FA00", false, 0, 0, 0)]       // alpha 付き 8 桁は拒否 (透明度設定と二重になるため)
    [InlineData("#12", false, 0, 0, 0)]
    [InlineData("xyz", false, 0, 0, 0)]
    [InlineData("", false, 0, 0, 0)]
    [InlineData(null, false, 0, 0, 0)]
    public void TryParseHex_accepts_only_rrggbb(string? hex, bool ok, byte r, byte g, byte b)
    {
        Assert.Equal(ok, IndicatorPalette.TryParseHex(hex, out var c));
        if (ok)
        {
            Assert.Equal(new IndicatorPalette.Rgb(r, g, b), c);
        }
    }

    [Fact]
    public void DefaultColorHex_parses()
    {
        Assert.True(IndicatorPalette.TryParseHex(IndicatorPalette.DefaultColorHex, out _));
    }
}
