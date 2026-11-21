using System;
using System.Collections.Generic;
using NotchPeninsula.Plugins;
using SkiaSharp;

namespace NpsYpmLyrics;

/// <summary>
/// 岛内的一颗爱心：点一下把当前歌曲加入 / 移出「我喜欢的音乐」。
///
/// <para>
/// 图标直接用 YesPlayMusic 播放条里那两个 symbol 的原始 path
/// （<c>#icon-heart</c> / <c>#icon-heart-solid</c>，30×30 坐标系），运行中通过 CDP 取一次后缓存，
/// 所以形状和播放器里那颗完全一致，不是自己画的。颜色统一跟主题文字色走，只在「未知状态」时降透明度。
/// </para>
///
/// <para>
/// 位置：宿主按「插件项在内容顺序里的排位」决定贴媒体条左侧还是右侧，
/// 本插件在 <c>Plugins_Order</c> 里排在 <c>builtin.media</c> 之前，所以会落在媒体条左边。
/// </para>
/// </summary>
internal sealed class LikeWidget : IAutoWidthWidget
{
    /// <summary>爱心本体边长（槽位 = 该值 + 两侧各 1px 留白）。</summary>
    private const float IconSize = 16f;
    private const float SvgViewBox = 30f;

    // 播放器两个爱心的原始 path（#icon-heart / #icon-heart-solid，30×30 坐标系）。
    // 内置一份是为了「第一帧就是对的」：CDP 取图标要等连接建立，那几十毫秒里若没有兜底图形，
    // 就会先闪一个自己画的、带接缝的爱心再跳成真图标。运行时仍会从页面重新取一遍覆盖它。
    private const string DefaultHollowPath =
        "M 9.5449219 3 C 5.3895807 3 2 6.3895806 2 10.544922 C 2 14.283156 4.9005496 18.084723 7.6601562 21.119141 C 10.419763 24.153558 13.171875 26.369141 13.171875 26.369141 A 1.0001 1.0001 0 0 0 13.197266 26.388672 C 13.517448 26.630481 13.956962 26.684854 14.369141 26.785156 A 1.0001 1.0001 0 0 0 15 27 A 1.0001 1.0001 0 0 0 15.630859 26.785156 C 16.043038 26.684854 16.482552 26.630481 16.802734 26.388672 A 1.0001 1.0001 0 0 0 16.828125 26.369141 C 16.828125 26.369141 19.580237 24.153558 22.339844 21.119141 C 25.099451 18.084722 28 14.283156 28 10.544922 C 28 6.3895806 24.610419 3 20.455078 3 C 17.450232 3 15.833405 4.5910542 15 5.5664062 C 14.166595 4.5910543 12.549768 3 9.5449219 3 z M 9.5449219 5 C 12.372924 5 14.069642 7.4290597 14.126953 7.5117188 A 1.0001 1.0001 0 0 0 14.910156 8.0078125 A 1.0001 1.0001 0 0 0 15.003906 8.0117188 A 1.0001 1.0001 0 0 0 15.019531 8.0117188 A 1.0001 1.0001 0 0 0 15.042969 8.0097656 A 1.0001 1.0001 0 0 0 15.119141 8.0039062 A 1.0001 1.0001 0 0 0 15.871094 7.5136719 C 15.925786 7.4347249 17.624838 5 20.455078 5 C 23.529737 5 26 7.4702629 26 10.544922 C 26 13.147688 23.499768 16.870104 20.859375 19.773438 C 18.227966 22.666891 15.607768 24.780451 15.589844 24.794922 C 15.414236 24.925626 15.219097 25 15 25 C 14.780903 25 14.585764 24.925626 14.410156 24.794922 C 14.392232 24.780451 11.772034 22.66689 9.140625 19.773438 C 6.5002316 16.870105 4 13.147688 4 10.544922 C 4 7.4702629 6.470263 5 9.5449219 5 z";

    private const string DefaultSolidPath =
        "M15,26c-0.21,0-0.42-0.066-0.597-0.198C13.938,25.456,3,17.243,3,11c0-3.859,3.141-7,7-7c2.358,0,4.062,1.272,5,2.212 C15.938,5.272,17.642,4,20,4c3.859,0,7,3.14,7,7c0,6.243-10.938,14.456-11.403,14.803C15.42,25.934,15.21,26,15,26z";

    private readonly PlaybackHub _hub;
    private readonly Dictionary<string, SKPath> _pathCache = new(StringComparer.Ordinal);
    private IPluginHost? _host;

    public LikeWidget(PlaybackHub hub) => _hub = hub;

    public string Id => "qwy.nps.ypmlyrics.like";

    public string DisplayName => "YesPlayMusic 喜欢";

    public IDetailPage? DetailPage => null;

    public void OnActivate(IPluginHost host) => _host = host;

    public void OnDeactivate()
    {
        _host = null;
        lock (_pathCache)
        {
            foreach (var p in _pathCache.Values) p.Dispose();
            _pathCache.Clear();
        }
    }

    public Box? Layout(float availableHeight)
    {
        // 没有会话就整颗都不显示，免得岛上一颗点了没反应的爱心
        if (!_hub.Snapshot.HasSession) return null;

        return WidgetKit.Row(WidgetKit.Icon(IconSize));
    }

    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        if (frame.Alpha == 0) return;

        var content = WidgetKit.ContentRect(rect);
        if (content.Width <= 2f || content.Height <= 2f) return;

        int liked = _hub.Liked;

        // 未知状态（没登录 / 读不到）画淡一点，避免误判成「未喜欢」
        byte alpha = liked < 0 ? (byte)(frame.Alpha * 0.5f) : frame.Alpha;
        var color = PluginTone.ColorFor(canvas, content, frame.Theme.TextColor).WithAlpha(alpha);

        // 优先用运行中从播放器取到的 path；还没取到就用内置的那份（同一个图标，避免先闪一个自绘的）
        string source = liked == 1 ? _hub.SolidHeartPath : _hub.HollowHeartPath;
        if (string.IsNullOrEmpty(source)) source = liked == 1 ? DefaultSolidPath : DefaultHollowPath;

        var path = ParseSvg(source);
        if (path is null) return;

        float size = MathF.Min(content.Width, content.Height);
        float scale = size / SvgViewBox;

        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };

        canvas.Save();
        canvas.Translate(content.MidX - SvgViewBox * 0.5f * scale, content.MidY - SvgViewBox * 0.5f * scale);
        canvas.Scale(scale);
        canvas.DrawPath(path, fill);
        canvas.Restore();
    }

    private SKPath? ParseSvg(string source)
    {
        if (string.IsNullOrEmpty(source)) return null;

        lock (_pathCache)
        {
            if (_pathCache.TryGetValue(source, out var cached)) return cached;

            SKPath? parsed;
            try { parsed = SKPath.ParseSvgPathData(source); }
            catch { parsed = null; }

            if (parsed is null) return null;
            _pathCache[source] = parsed;
            return parsed;
        }
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
