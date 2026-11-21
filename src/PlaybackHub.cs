using System;
using System.Threading;
using NotchPeninsula.Plugins;

namespace NpsYpmLyrics;

/// <summary>
/// 两路数据源的汇合点：CDP 优先、SMTC 兜底。这里只做「时间轴」一件事 ——
/// 不提供歌词、不画任何东西，进度与时长由 <see cref="IMediaSessionSource"/> 交给宿主。
/// </summary>
internal sealed class PlaybackHub : IDisposable
{
    private readonly SmtcProbe _smtc;
    private readonly CdpProbe _cdp;
    private readonly IDisposable _ticker;

    private string _songKey = "";
    private bool _lastPlaying;
    private bool _lastHasSession;
    private int _lastLiked = int.MinValue;
    private int _hostReads;

    /// <summary>换歌 / 播放状态 / 有无会话变化时触发，作为 <see cref="IMediaSessionSource.Changed"/> 的来源。</summary>
    public event Action? StateChanged;

    public PlaybackHub(IPluginHost host, string[] keywords, int[] ports)
    {
        _smtc = new SmtcProbe(keywords);
        _cdp = new CdpProbe(ports);

        _ticker = host.ScheduleRefresh(TimeSpan.FromMilliseconds(250), Tick);
    }

    /// <summary>CDP 有数据就以它为准（进度精确到毫秒）；否则退回 SMTC。</summary>
    public PlaybackSnapshot Snapshot
    {
        get
        {
            var cdp = _cdp.Snapshot;
            return cdp.HasSession ? cdp : _smtc.Snapshot;
        }
    }

    /// <summary>当前进度来自哪一路（"CDP" / "SMTC"），只用于日志。</summary>
    public string TimelineSource => _cdp.Snapshot.HasSession ? "CDP" : _smtc.Snapshot.HasSession ? "SMTC" : "";

    public void Start()
    {
        _cdp.Start();
        _smtc.Start();
        Diag.Log($"时间轴源已启动（CDP 端口 {string.Join("/", _cdp.Ports)}，当前源 {TimelineSource}）");
    }

    /// <summary>
    /// 给宿主「外部媒体源」读的当前位置：<b>冻结快照</b>，不做墙钟外推 ——
    /// 宿主自己会用 <c>Position + (now - 锚点)</c> 推算，两边都外推会跑快一倍。
    ///
    /// <para>
    /// 宿主只有在确认接管之后才会读这个属性（PickExternalSource → ExternalLivePosition），
    /// 所以这里被读到本身就是接管成功的信号，顺手记一笔方便排障。
    /// </para>
    /// </summary>
    public TimeSpan CurrentPosition
    {
        get
        {
            int n = Interlocked.Increment(ref _hostReads);
            if (n == 1 || n % 1800 == 0)
            {
                var snap = Snapshot;
                Diag.Log($"宿主读取时间轴 #{n} pos={snap.Position:hh\\:mm\\:ss\\.ff} " +
                         $"dur={snap.Duration.TotalSeconds:0}s playing={snap.IsPlaying}");
            }
            return Snapshot.Position;
        }
    }

    /// <summary>给宿主读的总时长；未知时 <see cref="TimeSpan.Zero"/>（宿主据此决定要不要接管）。</summary>
    public TimeSpan CurrentDuration => Snapshot.Duration;

    /// <summary>1 = 已喜欢，0 = 未喜欢，-1 = 读不到（没登录 / 播放条不在）。</summary>
    public int Liked => Snapshot.Liked;

    /// <summary>把当前歌曲加入或移出「我喜欢的音乐」。只有 CDP 能点，SMTC 没有这个能力。</summary>
    public void ToggleLike()
    {
        if (_cdp.Connected) _cdp.ToggleLike();
    }

    // ---- 后台：只做状态广播 ----

    private void Tick()
    {
        var snap = Snapshot;

        bool hasSession = snap.HasSession;
        bool playing = snap.IsPlaying;
        string key = snap.Key;

        bool songChanged = key != _songKey;
        if (songChanged) _songKey = key;

        bool stateChanged = songChanged || playing != _lastPlaying || hasSession != _lastHasSession;

        _lastPlaying = playing;
        _lastHasSession = hasSession;

        // 喜欢状态变了记一笔：爱心颜色切换没有别的痕迹，排障时全靠这行
        int liked = snap.Liked;
        if (liked != _lastLiked)
        {
            _lastLiked = liked;
            Diag.Log($"喜欢状态 → {(liked == 1 ? "已喜欢" : liked == 0 ? "未喜欢" : "未知")}（{snap.Title}）");
        }

        if (stateChanged) RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); }
        catch (Exception ex) { Diag.Log("状态广播异常: " + ex.Message); }
    }

    // ---- 传输控制 ----
    //
    // 优先走 CDP：直接点播放器自己的按钮，不依赖 SMTC 会话在不在（Chromium 暂停久了会注销会话）。

    public void TogglePlayPause()
    {
        if (_cdp.Connected) _cdp.TogglePlayPause();
        else _smtc.TogglePlayPause();
    }

    public void Next()
    {
        if (_cdp.Connected) _cdp.Next();
        else _smtc.Next();
    }

    public void Previous()
    {
        if (_cdp.Connected) _cdp.Previous();
        else _smtc.Previous();
    }

    public void OpenApp() => _smtc.OpenApp();

    public void Dispose()
    {
        StateChanged = null;
        _cdp.Dispose();
        _smtc.Dispose();
        _ticker.Dispose();
    }
}
