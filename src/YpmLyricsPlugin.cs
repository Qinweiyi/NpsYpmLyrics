using System;
using System.Collections.Generic;
using NotchPeninsula.Plugins;

namespace NpsYpmLyrics;

/// <summary>
/// YesPlayMusic 时间轴插件。
///
/// <para>
/// 它<b>不是</b>一个独立媒体面板，而是一路「外部媒体源」：把 YesPlayMusic 的真实播放位置与时长
/// 交给宿主，岛上的媒体条、进度、歌词全部由宿主内置的媒体控制渲染（和 mediacdp 插件的定位一致）。
/// </para>
///
/// <para>
/// 宿主原有链路是 SMTC → 媒体面板，但 YesPlayMusic 的 Electron 外壳只注册了歌名歌手、
/// 从不上报时间轴（<c>pos/end</c> 恒为 0）。宿主看到 <c>smtcDuration == 0</c> 时会来找本插件接管，
/// 于是同一个媒体面板就拿到了正确进度。
/// </para>
///
/// <para>
/// 默认不注册任何岛内组件。想要岛内单独显示一行歌词的老玩法，把
/// <c>Plugin.workbuddy.nps.ypmlyrics.showWidget</c> 设成 <c>1</c>。
/// </para>
/// </summary>
public sealed class YpmLyricsPlugin : INotchPlugin, IMediaSessionSource, IDisposable
{
    private PlaybackHub? _hub;
    private IReadOnlyList<string> _keywords = new[] { "yesplay" };

    public string Id => "workbuddy.nps.ypmlyrics";

    public string DisplayName => "YesPlayMusic 时间轴";

    public string Version => "1.1.0";

    public string Author => "QwY";

    public void Initialize(IPluginHost host)
    {
        _keywords = ParseKeywords(host.GetSetting("keywords", "yesplay"));

        bool showWidget = host.GetSetting("showWidget", "0") == "1";

        // 只有需要岛内那一行时才去拉歌词；纯时间轴模式下不发任何歌词请求
        _hub = new PlaybackHub(host, lyricsEnabled: showWidget);

        if (showWidget) host.RegisterWidget(new LyricWidget(_hub));

        host.RegisterUninstallDirectory(Diag.DataDir);

        _hub.Start();

        try
        {
            if (host.GetSetting("welcomed", "0") != "1")
            {
                host.SetSetting("welcomed", "1");
                host.PostReminder(new ReminderData
                {
                    Source = "YesPlayMusic 时间轴",
                    Title = "时间轴已接管",
                    Body = showWidget
                        ? "岛内会多一行歌词；媒体条进度也来自插件。"
                        : "媒体面板里的进度和歌词现在跟着 YesPlayMusic 走了。",
                    Duration = TimeSpan.FromSeconds(8),
                });
            }
        }
        catch (Exception ex)
        {
            Diag.Log("首次提示失败: " + ex.Message);
        }
    }

    private static IReadOnlyList<string> ParseKeywords(string raw)
    {
        var list = new List<string>();
        foreach (string part in (raw ?? "").Split(new[] { ',', '，', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string k = part.Trim();
            if (k.Length > 0 && !list.Contains(k, StringComparer.OrdinalIgnoreCase)) list.Add(k);
        }
        return list;
    }

    // ---- IMediaSessionSource：宿主在 Initialize 之后自动登记这路源 ----

    public string SourceId => Id;

    public string SourceName => "YesPlayMusic 时间轴";

    /// <summary>和别的源（如 mediacdp）撞车时谁大听谁的。</summary>
    public int Priority => 200;

    /// <summary>只接管 SMTC appId 里含这些关键字的会话（YesPlayMusic.exe）。</summary>
    public IReadOnlyList<string> AppIdKeywords => _keywords;

    public bool IsActive => _hub?.Snapshot.HasSession ?? false;

    public bool IsPlaying => _hub?.Snapshot.IsPlaying ?? false;

    public string Title => _hub?.Snapshot.Title ?? "";

    public string Artist => _hub?.Snapshot.Artist ?? "";

    public TimeSpan Position => _hub?.CurrentPosition ?? TimeSpan.Zero;

    public TimeSpan Duration => _hub?.CurrentDuration ?? TimeSpan.Zero;

    public string? CoverUrl => _hub?.Snapshot.CoverUrl;

    public void TogglePlayPause() => _hub?.TogglePlayPause();

    public void Next() => _hub?.Next();

    public void Previous() => _hub?.Previous();

    public void OpenApp() => _hub?.OpenApp();

    /// <summary>换歌 / 播放状态变化时通知宿主重算时间轴锚点。可能来自任意线程。</summary>
    public event Action? Changed
    {
        add { if (_hub is not null) _hub.StateChanged += value; }
        remove { if (_hub is not null) _hub.StateChanged -= value; }
    }

    public void Dispose() => _hub?.Dispose();
}
