using System;
using NotchPeninsula.Plugins;
using SkiaSharp;

namespace NpsYpmLyrics;

/// <summary>展开态：整段歌词 + 进度条 + 上一首 / 播放 / 下一首，点某一句可以跳转。</summary>
internal sealed class LyricPage : IDetailPage
{
    private const float W = 420f;
    private const float H = 206f;
    private const float PadX = 20f;

    private const float HeaderBaseline = 26f;
    private const float LinesCenter = 100f;
    private const float LineSpacing = 25f;
    private const int VisibleLines = 5;

    private const float ProgressLabelBaseline = 170f;
    private const float ProgressBarY = 176f;
    private const float ProgressBarH = 3f;

    private const float ControlCy = 192f;
    private const float ControlRadius = 10.5f;
    private const float ControlGap = 56f;

    private static readonly SKColor Accent = new(0xF2, 0x3A, 0x5C);

    private readonly PlaybackHub _hub;

    public LyricPage(PlaybackHub hub) => _hub = hub;

    public float MeasureWidth() => W;

    public float MeasureHeight() => H;

    /// <summary>看歌词时别几秒就收回去。</summary>
    public TimeSpan AutoCollapseDelay => TimeSpan.FromSeconds(20);

    public bool AcceptsRightClick => true;

    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        if (frame.Alpha == 0) return;

        canvas.Save();
        canvas.Translate(rect.Left, rect.Top);

        var theme = frame.Theme;
        var doc = _hub.Doc;
        var snap = _hub.Snapshot;

        DrawHeader(canvas, theme, frame.Alpha, doc, snap);

        if (doc is null || doc.IsEmpty)
        {
            DrawPlaceholder(canvas, theme, frame.Alpha);
            DrawProgress(canvas, theme, frame.Alpha, snap, TimeSpan.Zero, TimeSpan.Zero);
            DrawControls(canvas, theme, frame.Alpha, false);
        }
        else
        {
            int idx = _hub.CurrentIndex();
            DrawLines(canvas, theme, frame.Alpha, doc, idx);
            DrawProgress(canvas, theme, frame.Alpha, snap, snap.PositionNow(), snap.Duration);
            DrawControls(canvas, theme, frame.Alpha, snap.IsPlaying);
        }

        DrawOffsetButtons(canvas, theme, frame.Alpha);

        canvas.Restore();
    }

    // ---- 各区块 ----

    private void DrawHeader(SKCanvas canvas, RenderTheme theme, byte alpha, LyricDoc? doc, PlaybackSnapshot snap)
    {
        string title = doc is { Title.Length: > 0 } ? doc.Title : snap.Title;
        string artist = doc is { Artist.Length: > 0 } ? doc.Artist : snap.Artist;
        if (title.Length == 0) title = "YesPlayMusic";

        using var strong = TextPaint(13f, TextWeight.SemiBold, theme.TextColor, alpha);
        PluginTone.DrawText(canvas, theme, TextFit.Fit(title, 13f, TextWeight.SemiBold, W - 2 * PadX - 80f),
            PadX, HeaderBaseline, strong, sub: false, alpha: alpha);

        float titleWidth = TextFit.Measure(TextFit.Fit(title, 13f, TextWeight.SemiBold, W - 2 * PadX - 80f), 13f, TextWeight.SemiBold);

        if (artist.Length > 0)
        {
            using var weak = TextPaint(11f, TextWeight.Normal, theme.SubTextColor, alpha);
            float maxArtist = W - 2 * PadX - 80f - titleWidth - 8f;
            string shown = TextFit.Fit("· " + artist, 11f, TextWeight.Normal, maxArtist);
            if (shown.Length > 0)
                PluginTone.DrawText(canvas, theme, shown, PadX + titleWidth + 6f, HeaderBaseline, weak, sub: true, alpha: alpha);
        }

        // 右上角来源标记
        using var tag = TextPaint(9.5f, TextWeight.Normal, theme.SubTextColor, (byte)(alpha * 0.75f));
        string source = doc is { Source.Length: > 0 } ? doc.Source : "网易云";
        PluginTone.DrawText(canvas, theme, source,
            W - PadX - TextFit.Measure(source, 9.5f, TextWeight.Normal), HeaderBaseline, tag, sub: true, alpha: (byte)(alpha * 0.75f));
    }

    private void DrawLines(SKCanvas canvas, RenderTheme theme, byte alpha, LyricDoc doc, int current)
    {
        var centerX = W / 2f;
        var accent = PluginTone.AccentColor(canvas, new SKRect(0, LinesCenter - 10f, W, LinesCenter + 10f), Accent);

        for (int offset = -2; offset <= 2; offset++)
        {
            int i = current + offset;
            if (current < 0) i = offset + 1;                    // 前奏：从头开始显示
            if (i < 0 || i >= doc.Lines.Length) continue;

            var line = doc.Lines[i];
            string text = LrcParser.Flatten(line.Text);
            if (text.Length == 0) continue;

            float baseline = LinesCenter + offset * LineSpacing + 4.5f;

            if (i == current)
            {
                string shown = TextFit.Fit(text, 14f, TextWeight.SemiBold, W - 2 * PadX);
                float used = TextFit.Measure(shown, 14f, TextWeight.SemiBold);

                using var paint = TextPaint(14f, TextWeight.SemiBold, accent, alpha);

                // 当前句带翻译时，两句一起居中：原句 + 小一号灰一点的译文
                if (line.Translation is { Length: > 0 } tr)
                {
                    using var sub = TextPaint(10.5f, TextWeight.Normal, theme.SubTextColor, alpha);
                    float room = W - 2 * PadX - used - 14f;
                    string shownTr = TextFit.Fit(" " + LrcParser.Flatten(tr), 10.5f, TextWeight.Normal, room);
                    float trWidth = shownTr.Length > 0 ? TextFit.Measure(shownTr, 10.5f, TextWeight.Normal) : 0f;
                    float startX = MathF.Max(PadX, centerX - (used + trWidth) / 2f);

                    PluginTone.DrawAccentText(canvas, theme, shown, startX, baseline, paint, alpha);
                    if (shownTr.Length > 0)
                        PluginTone.DrawText(canvas, theme, shownTr, startX + used, baseline, sub, sub: true, alpha: alpha);
                    continue;
                }

                float x = centerX - used / 2f;
                PluginTone.DrawAccentText(canvas, theme, shown, x, baseline, paint, alpha);
                continue;
            }

            byte fade = offset switch
            {
                -1 or 1 => (byte)(alpha * 0.72f),
                _ => (byte)(alpha * 0.45f),
            };

            using var dim = TextPaint(12f, TextWeight.Normal, theme.SubTextColor, fade);
            string dimShown = TextFit.Fit(text, 12f, TextWeight.Normal, W - 2 * PadX);
            float dimX = centerX - TextFit.Measure(dimShown, 12f, TextWeight.Normal) / 2f;
            PluginTone.DrawText(canvas, theme, dimShown, dimX, baseline, dim, sub: true, alpha: fade);
        }
    }

    private void DrawPlaceholder(SKCanvas canvas, RenderTheme theme, byte alpha)
    {
        string text = _hub.Status switch
        {
            "searching" => "正在获取歌词…",
            "not-found" => "网易云没有找到这首歌词",
            "offline" => "网络不通，歌词拿不到",
            "error" => "歌词解析出错",
            _ => "YesPlayMusic 没有在播放",
        };

        string hint = _hub.Status is "searching" or "ok" ? "" : "在 YesPlayMusic 里放一首歌试试";

        using var paint = TextPaint(12.5f, TextWeight.Normal, theme.SubTextColor, (byte)(alpha * 0.9f));
        float x = W / 2f - TextFit.Measure(text, 12.5f, TextWeight.Normal) / 2f;
        PluginTone.DrawText(canvas, theme, text, x, LinesCenter + 4f, paint, sub: true, alpha: (byte)(alpha * 0.9f));

        if (hint.Length > 0)
        {
            using var sub = TextPaint(10.5f, TextWeight.Normal, theme.SubTextColor, (byte)(alpha * 0.6f));
            float sx = W / 2f - TextFit.Measure(hint, 10.5f, TextWeight.Normal) / 2f;
            PluginTone.DrawText(canvas, theme, hint, sx, LinesCenter + 24f, sub, sub: true, alpha: (byte)(alpha * 0.6f));
        }
    }

    private static void DrawProgress(SKCanvas canvas, RenderTheme theme, byte alpha,
        PlaybackSnapshot snap, TimeSpan pos, TimeSpan duration)
    {
        using var track = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = theme.SubTextColor.WithAlpha((byte)(alpha * 0.25f)),
        };

        var bar = new SKRect(PadX, ProgressBarY, W - PadX, ProgressBarY + ProgressBarH);
        canvas.DrawRoundRect(bar, ProgressBarH / 2f, ProgressBarH / 2f, track);

        float ratio = 0f;
        if (duration > TimeSpan.Zero)
            ratio = Math.Clamp((float)(pos.TotalSeconds / duration.TotalSeconds), 0f, 1f);
        else if (!snap.HasSession) ratio = 0f;

        if (ratio > 0f)
        {
            var accent = PluginTone.AccentColor(canvas, bar, Accent).WithAlpha(alpha);
            using var fill = new SKPaint { IsAntialias = true, Color = accent, Style = SKPaintStyle.Fill };
            canvas.DrawRoundRect(new SKRect(PadX, ProgressBarY, PadX + (W - 2 * PadX) * ratio, ProgressBarY + ProgressBarH),
                ProgressBarH / 2f, ProgressBarH / 2f, fill);
        }

        using var label = TextPaint(9.5f, TextWeight.Normal, theme.SubTextColor, (byte)(alpha * 0.8f));

        string left = Fmt(pos);
        string right = duration > TimeSpan.Zero ? Fmt(duration) : "--:--";
        PluginTone.DrawText(canvas, theme, left, PadX, ProgressLabelBaseline, label, sub: true, alpha: (byte)(alpha * 0.8f));
        PluginTone.DrawText(canvas, theme, right, W - PadX - TextFit.Measure(right, 9.5f, TextWeight.Normal),
            ProgressLabelBaseline, label, sub: true, alpha: (byte)(alpha * 0.8f));
    }

    private static void DrawControls(SKCanvas canvas, RenderTheme theme, byte alpha, bool playing)
    {
        float cx = W / 2f;
        var color = theme.TextColor.WithAlpha(alpha);

        // 上一首
        DrawSkip(canvas, cx - ControlGap, ControlCy, ControlRadius, color, previous: true);
        // 下一首
        DrawSkip(canvas, cx + ControlGap, ControlCy, ControlRadius, color, previous: false);

        // 播放 / 暂停
        var accent = PluginTone.AccentColor(canvas, new SKRect(cx - ControlRadius, ControlCy - ControlRadius, cx + ControlRadius, ControlCy + ControlRadius), Accent);
        using (var bg = new SKPaint { IsAntialias = true, Color = accent.WithAlpha(alpha), Style = SKPaintStyle.Fill })
        {
            canvas.DrawCircle(cx, ControlCy, ControlRadius, bg);
        }

        using var glyph = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = SKColors.White.WithAlpha(alpha),
        };

        if (playing)
        {
            canvas.DrawRoundRect(new SKRect(cx - 3.6f, ControlCy - 4.6f, cx - 1.1f, ControlCy + 4.6f), 1f, 1f, glyph);
            canvas.DrawRoundRect(new SKRect(cx + 1.1f, ControlCy - 4.6f, cx + 3.6f, ControlCy + 4.6f), 1f, 1f, glyph);
        }
        else
        {
            using var path = new SKPath();
            path.MoveTo(cx - 2.6f, ControlCy - 5f);
            path.LineTo(cx + 5.2f, ControlCy);
            path.LineTo(cx - 2.6f, ControlCy + 5f);
            path.Close();
            canvas.DrawPath(path, glyph);
        }
    }

    private static void DrawSkip(SKCanvas canvas, float cx, float cy, float r, SKColor color, bool previous)
    {
        var ink = PluginTone.ColorFor(canvas, new SKRect(cx - r, cy - r, cx + r, cy + r), color);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = ink.WithAlpha((byte)(ink.Alpha * 0.85f)) };
        using var path = new SKPath();

        float s = previous ? -1f : 1f;
        path.MoveTo(cx + 3.2f * s, cy - 4.6f);
        path.LineTo(cx - 4.2f * s, cy);
        path.LineTo(cx + 3.2f * s, cy + 4.6f);
        path.Close();
        canvas.DrawPath(path, paint);

        canvas.DrawRoundRect(new SKRect(
            previous ? cx - 5.6f : cx + 3.8f,
            cy - 4.8f,
            previous ? cx - 3.8f : cx + 5.6f,
            cy + 4.8f), 0.9f, 0.9f, paint);
    }

    private void DrawOffsetButtons(SKCanvas canvas, RenderTheme theme, byte alpha)
    {
        int offset = _hub.OffsetMs;
        string left = "−0.3s";
        string right = "+0.3s";

        byte a = (byte)(alpha * (offset == 0 ? 0.45f : 0.8f));
        using var paint = TextPaint(9.5f, TextWeight.Normal, theme.SubTextColor, a);

        PluginTone.DrawText(canvas, theme, left, PadX + 2f, ControlCy + 4f, paint, sub: true, alpha: a);
        float rw = TextFit.Measure(right, 9.5f, TextWeight.Normal);
        PluginTone.DrawText(canvas, theme, right, W - PadX - 2f - rw, ControlCy + 4f, paint, sub: true, alpha: a);

        if (offset != 0)
        {
            string value = (offset > 0 ? "+" : "") + (offset / 1000.0).ToString("0.0") + "s";
            using var valuePaint = TextPaint(9.5f, TextWeight.SemiBold, theme.SubTextColor, a);
            PluginTone.DrawText(canvas, theme, value, PadX + 2f, ControlCy - 9f, valuePaint, sub: true, alpha: a);
        }
    }

    // ---- 命中 ----

    public WidgetHit HitTest(float x, float y, SKRect rect)
    {
        float cx = W / 2f;

        if (Dist(x, y, cx - ControlGap, ControlCy) <= ControlRadius + 3f) return new WidgetHit("prev");
        if (Dist(x, y, cx, ControlCy) <= ControlRadius + 3f) return new WidgetHit("toggle");
        if (Dist(x, y, cx + ControlGap, ControlCy) <= ControlRadius + 3f) return new WidgetHit("next");

        if (y >= ProgressBarY - 8f && y <= ProgressBarY + 10f) return new WidgetHit("seek");

        if (y >= ControlCy - 10f && y <= ControlCy + 8f)
        {
            if (x >= PadX && x <= PadX + 44f) return new WidgetHit("offset-");
            if (x >= W - PadX - 44f && x <= W - PadX) return new WidgetHit("offset+");
        }

        var doc = _hub.Doc;
        if (doc is { IsEmpty: false })
        {
            int current = _hub.CurrentIndex();
            for (int offset = -2; offset <= 2; offset++)
            {
                int i = current + offset;
                if (current < 0) i = offset + 1;
                if (i < 0 || i >= doc.Lines.Length) continue;

                float rowCenter = LinesCenter + offset * LineSpacing;
                if (y >= rowCenter - LineSpacing / 2f && y <= rowCenter + LineSpacing / 2f)
                    return new WidgetHit("line:" + i);
            }
        }

        return WidgetHit.None;
    }

    public void OnAction(string? action, float x, float y)
    {
        if (string.IsNullOrEmpty(action)) return;

        switch (action)
        {
            case "toggle": _hub.TogglePlayPause(); break;
            case "prev": _hub.Previous(); break;
            case "next": _hub.Next(); break;
            case "offset-": _hub.NudgeOffset(-300); break;
            case "offset+": _hub.NudgeOffset(300); break;
            case "seek":
            {
                var snap = _hub.Snapshot;
                if (snap.Duration <= TimeSpan.Zero) return;
                float ratio = Math.Clamp((x - PadX) / (W - 2 * PadX), 0f, 1f);
                _hub.Seek(snap.Duration * ratio);
                break;
            }
            default:
                if (action.StartsWith("line:", StringComparison.Ordinal) &&
                    int.TryParse(action.AsSpan(5), out int index))
                {
                    var doc = _hub.Doc;
                    if (doc is { IsEmpty: false } && index >= 0 && index < doc.Lines.Length)
                        _hub.Seek(doc.Lines[index].Time);
                }
                break;
        }
    }

    public void OnRightClick(float x, float y) { }

    // ---- 工具 ----

    private static SKPaint TextPaint(float size, TextWeight weight, SKColor color, byte alpha) => new()
    {
        IsAntialias = true,
        TextSize = size,
        Typeface = WidgetKit.Typeface(weight),
        Color = color.WithAlpha(alpha),
    };

    private static float Dist(float x1, float y1, float x2, float y2)
    {
        float dx = x1 - x2, dy = y1 - y2;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static string Fmt(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }
}
