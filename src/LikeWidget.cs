using System;
using NotchPeninsula.Plugins;
using SkiaSharp;

namespace NpsYpmLyrics;

/// <summary>
/// 岛内的一颗爱心：点一下把当前歌曲加入 / 移出「我喜欢的音乐」。
///
/// <para>
/// 红色实心 = 已喜欢，空心（跟随主题描边）= 未喜欢，半透明 = 读不到状态（通常没登录）。
/// </para>
///
/// <para>
/// 为什么是「岛内一颗」而不是「宿主媒体条上的一颗」：宿主 2.2.5 的插件 API 只有
/// <see cref="IWidget"/>（岛内组件）和 <see cref="IDetailPage"/>（展开页）两个 UI 面，
/// 内置媒体条不接受插件注入，<see cref="IMediaSessionSource"/> 也只有播放控制、没有喜欢相关成员。
/// 所以这里做成一个 26px 的独立组件，视觉上紧挨着媒体行。
/// </para>
/// </summary>
internal sealed class LikeWidget : IAutoWidthWidget
{
    private const float SlotWidth = 26f;

    /// <summary>红心的固定色，不随主题走（主题色里没有这么鲜艳的红）。</summary>
    private static readonly SKColor LikedRed = new(0xF2, 0x3A, 0x5C);

    private readonly PlaybackHub _hub;
    private IPluginHost? _host;

    public LikeWidget(PlaybackHub hub) => _hub = hub;

    public string Id => "qwy.nps.ypmlyrics.like";

    public string DisplayName => "YesPlayMusic 喜欢";

    public IDetailPage? DetailPage => null;

    public void OnActivate(IPluginHost host) => _host = host;

    public void OnDeactivate() => _host = null;

    public Box? Layout(float availableHeight)
    {
        // 没有会话就整颗都不显示，免得岛上一颗点了没反应的爱心
        if (!_hub.Snapshot.HasSession) return null;

        return WidgetKit.Min(SlotWidth, WidgetKit.Row(WidgetKit.Icon(SlotWidth - 2f * WidgetKit.AutoPadX - 1f)));
    }

    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        if (frame.Alpha == 0) return;

        var content = WidgetKit.ContentRect(rect);
        if (content.Width <= 2f || content.Height <= 2f) return;

        int liked = _hub.Liked;

        // 未知状态（没登录 / 读不到）画淡一点，避免误判成「未喜欢」
        byte alpha = liked < 0 ? (byte)(frame.Alpha * 0.5f) : frame.Alpha;

        float size = MathF.Min(content.Width, content.Height) - 1f;
        if (size <= 3f) return;

        using var path = BuildHeart(content.MidX, content.MidY, size);
        if (path is null) return;

        if (liked == 1)
        {
            var fill = PluginTone.ColorFor(canvas, content, LikedRed).WithAlpha(alpha);
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = fill };
            canvas.DrawPath(path, paint);
            return;
        }

        // 未喜欢 / 未知：空心，描边跟随主题文字色（浅色主题下是深色边）
        var stroke = PluginTone.ColorFor(canvas, content, frame.Theme.TextColor).WithAlpha(alpha);
        using var line = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = MathF.Max(1.2f, size * 0.085f),
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            Color = stroke,
        };
        canvas.DrawPath(path, line);
    }

    /// <summary>两个圆 + 一个三角并集，得到单条轮廓的爱心（描边不会出现内部接缝）。</summary>
    private static SKPath? BuildHeart(float cx, float cy, float size)
    {
        float r = size * 0.5f / 1.02f;

        using var left = new SKPath();
        left.AddCircle(cx - r * 0.5f, cy - r * 0.30f, r * 0.55f);

        using var right = new SKPath();
        right.AddCircle(cx + r * 0.5f, cy - r * 0.30f, r * 0.55f);

        using var tip = new SKPath();
        tip.MoveTo(cx - r * 1.02f, cy - r * 0.16f);
        tip.LineTo(cx + r * 1.02f, cy - r * 0.16f);
        tip.LineTo(cx, cy + r * 0.95f);
        tip.Close();

        using var lobes = new SKPath();
        if (!left.Op(right, SKPathOp.Union, lobes)) return null;

        var heart = new SKPath();
        if (!lobes.Op(tip, SKPathOp.Union, heart))
        {
            heart.Dispose();
            return null;
        }
        return heart;
    }

    public WidgetHit HitTest(float x, float y, SKRect rect)
        => x >= 0f && x <= rect.Width && y >= 0f && y <= rect.Height ? new WidgetHit("like") : WidgetHit.None;

    public void OnLeftClick(string? action, float x, float y)
    {
        if (action != "like") return;

        _hub.ToggleLike();

        // 状态在下一轮轮询（≤350ms）后更新；颜色变化不需要重排，但要确保岛会重画一帧
        _host?.RequestRedraw();
        _host?.InvalidateWidgetLayout();
    }

    public void OnRightClick() { }
}
