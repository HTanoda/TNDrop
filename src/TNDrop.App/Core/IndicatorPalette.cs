using System;
using System.Globalization;

namespace TNDrop.Core;

/// <summary>
/// インジケーターの色を基準色 1 つから一括導出する純粋クラス (v1.5)。塗り (Fill)、
/// 縁取り / Bulge 本体 (Outline)、Bulge 外周リム (Rim) の 3 色は必ずこの Resolve
/// 1 回から得る -- 呼び出し側が独自に色を計算して塗りと縁が静かに矛盾する事故を
/// 防ぐ (one-resolution-per-related-fields)。v1.3 の IndicatorBrightness (白方向
/// ブースト) はこのクラスで置き換えられ削除された: 白方向一辺倒の明るさ補正は
/// 明るい背景でちょうど逆効果だった、という本番フィードバックが v1.5 の出発点。
/// 純粋・static なので WPF なしで直接テストできる (IndicatorPaletteTests)。
/// </summary>
public static class IndicatorPalette
{
    /// <summary>設定 IndicatorColor のデフォルト兼パース不能時のフォールバック。</summary>
    public const string DefaultColorHex = "#5AC8FA";

    /// <summary>Outline = 基準色を黒方向へこの率だけブレンド。実機確認で微調整可だが、
    /// 変えるときはこの定数だけを変える (導出式には触れない)。</summary>
    public const double OutlineBlackBlend = 0.65;

    /// <summary>Rim = 基準色を白方向へこの率だけブレンド。</summary>
    public const double RimWhiteBlend = 0.60;

    public readonly record struct Rgb(byte R, byte G, byte B);

    /// <summary>基準色から (塗り, 縁, リム) を一括導出する。塗りは基準色そのまま。</summary>
    public static (Rgb Fill, Rgb Outline, Rgb Rim) Resolve(byte r, byte g, byte b)
    {
        var fill = new Rgb(r, g, b);
        return (fill, Blend(fill, 0, OutlineBlackBlend), Blend(fill, 255, RimWhiteBlend));
    }

    /// <summary>"#RRGGBB" (大文字小文字不問) のみ受理。alpha 付き 8 桁は透明度設定と
    /// 二重になるため受けない (設計書パート2)。</summary>
    public static bool TryParseHex(string? hex, out Rgb color)
    {
        color = default;
        if (hex is null || hex.Length != 7 || hex[0] != '#')
        {
            return false;
        }

        if (!byte.TryParse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
            !byte.TryParse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
            !byte.TryParse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return false;
        }

        color = new Rgb(r, g, b);
        return true;
    }

    /// <summary>ITU-R BT.601 の知覚輝度 (旧 IndicatorBrightness.Luminance と同じ重み)。
    /// テストが「縁は暗い / リムは明るい」を数値で主張するために公開している。</summary>
    public static double Luminance(Rgb c) =>
        (0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B);

    private static Rgb Blend(Rgb from, byte target, double t)
    {
        byte Ch(byte c) => (byte)Math.Round(c + ((target - c) * t), MidpointRounding.AwayFromZero);
        return new Rgb(Ch(from.R), Ch(from.G), Ch(from.B));
    }
}
