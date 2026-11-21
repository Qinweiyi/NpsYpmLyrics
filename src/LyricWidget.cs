using System;
using NotchPeninsula.Plugins;
using SkiaSharp;

namespace NpsYpmLyrics;

/// <summary>折叠态：岛内一行当前歌词 + 三条跳动的频谱。左键展开歌词页。</summary>
internal sealed class LyricWidget : IAutoWidthWidget
{
    private const float TextSize = 12.5f;
    private const float IconWidth = 12f;
    private const float IconGap = 5f;
    private const float MaxSlotWidth = 232f;
    private const float MinSlotWidth = 92f;

    private readonly PlaybackHub _hub;
    private readonly LyricPage _page;

    private IPluginHost? _host;
    private volatile string _display = "";

    public LyricWidget(PlaybackHub hub)
    {
        _hub = hub;
        _page = new LyricPage(hub);
    }

    public string Id => "workbuddy.nps.ypmlyrics.widget";

    public string DisplayName => "YesPlayMusic 歌词";

    public IDetailPage? DetailPage => _page;

    public bool WidthVaries => true;

    public bool AcceptsDoubleClick => true;

    public void OnActivate(IPluginHost host) => _host = host;

    public void OnDeactivate() => _host = null;

    public Box? Layout(float availableHeight)
    {
        string text = _hub.CurrentText();
        if (text.Length == 0) { _display = ""; return null; }

        float budget = MaxSlotWidth;
        if (_host is not null)
        {
            float slot = _host.GetSlotBudget(Id);
            if (slot > 0f && !float.IsInfinity(slot) && !float.IsNaN(slot)) budget = MathF.Min(slot, MaxSlotWidth);
        }

        // 槽位里去掉两侧留白、图标和间距，剩下的才是文字能占的宽度
        float textMax = budget - 2f * WidgetKit.AutoPadX - IconWidth - IconGap;
        string display = TextFit.Fit(text, TextSize, TextWeight.SemiBold, textMax);

        _display = display;
        if (display.Length == 0) return null;

        return WidgetKit.Min(MinSlotWidth,
            WidgetKit.Row(
                WidgetKit.Icon(IconWidth),
                WidgetKit.Gap(IconGap),
                WidgetKit.Text(display, TextSize, TextWeight.SemiBold)));
    }

    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        if (frame.Alpha == 0) return;

        string text = _display;
        if (text.Length == 0) return;

        var content = WidgetKit.ContentRect(rect);
        float cy = content.MidY + frame.TextOffsetY;

        byte alpha = _hub.Snapshot.IsPlaying ? frame.Alpha : (byte)(frame.Alpha * 0.62f);
        var ink = frame.Theme.TextColor.WithAlpha(alpha);

        DrawLevels(canvas, content, cy, frame, ink);

        using var paint = new SKPaint
        {
            IsAntialias = true,
            TextSize = TextSize,
            Typeface = WidgetKit.Typeface(TextWeight.SemiBold),
            Color = ink,
        };

        PluginTone.DrawText(canvas, frame.Theme, text,
            content.Left + IconWidth + IconGap, cy + 4.5f, paint, sub: false, alpha: alpha);
    }

    /// <summary>三根竖条：有频谱就跟宿主给的 5 段能量联动，没音频就摆一个静态的小波形。</summary>
    private static void DrawLevels(SKCanvas canvas, SKRect content, float cy, WidgetFrame frame, SKColor ink)
    {
        const float barWidth = 2.4f;
        const float barGap = 1.6f;
        const float maxHeight = 10f;
        const float minHeight = 2.6f;

        var bars = frame.Bars;
        var area = new SKRect(content.Left, cy - maxHeight / 2f, content.Left + IconWidth, cy + maxHeight / 2f);
        var color = PluginTone.ColorFor(canvas, area, ink);

        using var paint = new SKPaint { IsAntialias = true, Color = color, Style = SKPaintStyle.Fill };

        for (int i = 0; i < 3; i++)
        {
            float energy = 0f;
            if (bars is { Length: > 0 }) energy = bars[Math.Min(i * 2, bars.Length - 1)];

            float h = energy > 0.02f
                ? minHeight + MathF.Min(1f, energy) * (maxHeight - minHeight)
                : minHeight + (i == 1 ? 3.4f : i == 2 ? 1.8f : 0f);

            float x = content.Left + i * (barWidth + barGap);
            canvas.DrawRoundRect(new SKRect(x, cy - h / 2f, x + barWidth, cy + h / 2f), barWidth / 2f, barWidth / 2f, paint);
        }
    }

    public WidgetHit HitTest(float x, float y, SKRect rect)
        => x >= 0f && x <= rect.Width && y >= 0f && y <= rect.Height ? new WidgetHit("open") : WidgetHit.None;

    public void OnLeftClick(string? action, float x, float y)
    {
        if (action != "open") return;
        _host?.ToggleDetailPage(Id);
    }

    public void OnLeftDoubleClick(float x, float y)
    {
        if (_hub.Snapshot.HasSession) _hub.OpenApp();
    }

    public void OnRightClick() => _host?.ToggleDetailPage(Id);
}
