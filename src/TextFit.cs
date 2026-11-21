using System;
using NotchPeninsula.Plugins;
using SkiaSharp;

namespace NpsYpmLyrics;

/// <summary>
/// 文字宽度测量 + 截断。和宿主共用同一份字体面（<see cref="WidgetKit.Typeface"/>），
/// 保证「量出来的宽度」和「画出来的宽度」一致，不会量完还溢出槽位。
/// </summary>
internal static class TextFit
{
    public const string Ellipsis = "…";

    private static readonly SKPaint Paint = new() { IsAntialias = true };
    private static readonly object Gate = new();

    public static float Measure(string? text, float size, TextWeight weight)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        lock (Gate)
        {
            var face = WidgetKit.Typeface(weight);
            if (face != null) Paint.Typeface = face;
            Paint.TextSize = size;
            return Paint.MeasureText(text);
        }
    }

    /// <summary>把文字裁到 <paramref name="maxWidth"/> 以内，超出部分用省略号收尾。</summary>
    public static string Fit(string? text, float size, TextWeight weight, float maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (maxWidth <= 0f) return "";
        if (Measure(text, size, weight) <= maxWidth) return text;

        float budget = maxWidth - Measure(Ellipsis, size, weight);
        if (budget <= 0f) return "";

        int lo = 0, hi = text.Length, best = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Measure(text.Substring(0, mid), size, weight) <= budget) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        if (best <= 0) return "";

        // 别把代理对（emoji）切一半，否则画出方块
        if (best < text.Length && char.IsSurrogate(text[best - 1])) best--;
        if (best <= 0) return "";

        return text.Substring(0, best).TrimEnd() + Ellipsis;
    }
}
