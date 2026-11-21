using System;
using System.Threading;
using System.Threading.Tasks;
using NotchPeninsula.Plugins;

namespace NpsYpmLyrics;

/// <summary>
/// 插件的大脑：把「当前在放什么」和「这首歌的歌词」拼在一起，算出此刻该显示哪一句。
///
/// <para>
/// 两路数据源，CDP 优先、SMTC 兜底：
/// <list type="bullet">
///   <item><b>CDP</b>：直连播放器调试端口读 <c>&lt;audio&gt;.currentTime</c>，进度精确到毫秒；</item>
///   <item><b>SMTC</b>：系统媒体会话，拿歌名歌手没问题，但 YesPlayMusic 不上报时间轴，只能兜底。</item>
/// </list>
/// 传输控制（上一首/播放/下一首）统一走 SMTC —— Chromium 把这些按钮接进了系统媒体会话。
/// </para>
///
/// <para>
/// 线程模型：探针在后台线程写快照；宿主渲染线程读快照 + 歌词文档。文档是不可变对象，
/// 换歌时整份替换引用，读侧永远看到一份自洽的歌词。
/// </para>
/// </summary>
internal sealed class PlaybackHub : IDisposable
{
    private readonly IPluginHost _host;
    private readonly SmtcProbe _smtc;
    private readonly CdpProbe _cdp;
    private readonly LyricProvider _provider;
    private readonly IDisposable _ticker;
    private readonly string[] _keywords;
    private readonly bool _lyricsEnabled;

    private volatile LyricDoc? _doc;
    private volatile string _status = "idle";
    private volatile string _songKey = "";
    private volatile string _loadedKey = "";
    private long _loadedSongId;
    private int _lineIndex = int.MinValue;
    private int _offsetMs;
    private CancellationTokenSource? _songCts;

    // 给「外部媒体源」用的状态广播：只在换歌 / 播放状态 / 有无会话变化时触发
    private bool _lastPlaying;
    private bool _lastHasSession;
    private int _hostReads;
    public event Action? StateChanged;

    // 无时间轴时的墙钟估算器
    private readonly object _estGate = new();
    private string _estKey = "";
    private TimeSpan _estPos;
    private DateTime _estAt = DateTime.MinValue;
    private bool _estPlaying;

    public PlaybackHub(IPluginHost host, bool lyricsEnabled)
    {
        _host = host;
        _lyricsEnabled = lyricsEnabled;
        _provider = new LyricProvider(Diag.DataDir);

        _keywords = (host.GetSetting("keywords", "yesplay") ?? "")
            .Split(new[] { ',', '，', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);

        if (!int.TryParse(host.GetSetting("offsetMs", "0"), out _offsetMs)) _offsetMs = 0;

        var ports = new List<int>();
        foreach (string part in (host.GetSetting("cdpPort", "9333,9222") ?? "").Split(new[] { ',', '，', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, out int p) && p > 0 && p < 65536 && !ports.Contains(p)) ports.Add(p);
        }
        if (ports.Count == 0) ports.AddRange(new[] { 9333, 9222 });

        _smtc = new SmtcProbe(_keywords);
        _cdp = new CdpProbe(ports.ToArray());

        _ticker = host.ScheduleRefresh(TimeSpan.FromMilliseconds(250), Tick);

        host.SettingsChanged += OnSettingsChanged;
    }

    /// <summary>CDP 有时用 CDP，没有就退回 SMTC —— 上层不用关心细节。</summary>
    public PlaybackSnapshot Snapshot
    {
        get
        {
            var cdp = _cdp.Snapshot;
            if (cdp.HasSession) return cdp;
            return _smtc.Snapshot;
        }
    }

    public LyricDoc? Doc => _doc;

    public string Status => _status;

    public int OffsetMs => Volatile.Read(ref _offsetMs);

    /// <summary>当前进度来自哪一路（"CDP" / "SMTC"），给详情页标一下。</summary>
    public string TimelineSource => _cdp.Snapshot.HasSession ? "CDP" : _smtc.Snapshot.HasSession ? "SMTC" : "";

    public void Start()
    {
        _cdp.Start();
        _smtc.Start();
        Diag.Log($"插件启动：白名单=[{string.Join(",", _keywords)}] 偏移={_offsetMs}ms 缓存={Diag.DataDir}");
    }

    // ---- 渲染侧读取 ----

    /// <summary>
    /// 当前进度。有时间轴（CDP，或任何会上报 EndTime 的播放器）就直接用；
    /// YesPlayMusic 走 SMTC 时恒为 <c>0 / 0</c>，这时按墙钟累计，至少能让歌词跟着走。
    /// </summary>
    private TimeSpan EffectivePosition(PlaybackSnapshot snap)
    {
        if (!snap.HasSession) return TimeSpan.Zero;
        if (snap.Duration > TimeSpan.Zero || snap.Position > TimeSpan.Zero) return snap.PositionNow();

        lock (_estGate)
        {
            DateTime now = DateTime.UtcNow;
            string key = snap.Key;

            if (key != _estKey)
            {
                _estKey = key;
                _estPos = TimeSpan.Zero;
                _estAt = now;
            }
            else if (_estAt != DateTime.MinValue)
            {
                if (_estPlaying) _estPos += now - _estAt;
                _estAt = now;
                if (_estPos > TimeSpan.FromHours(2)) _estPos = TimeSpan.Zero;   // 明显僵死的会话，别无限累加
            }

            _estPlaying = snap.IsPlaying;
            return _estPos;
        }
    }

    public TimeSpan LyricPosition()
    {
        var pos = EffectivePosition(Snapshot) + TimeSpan.FromMilliseconds(OffsetMs);
        return pos < TimeSpan.Zero ? TimeSpan.Zero : pos;
    }

    /// <summary>
    /// 给宿主「外部媒体源」读的当前位置：<b>冻结快照</b>，不做墙钟外推 ——
    /// 宿主自己会用 <c>Position + (now - 锚点)</c> 推算，两边都外推就会跑快一倍。
    ///
    /// <para>
    /// 宿主只有在「确认接管」之后才会读这个属性（PickExternalSource → ExternalLivePosition），
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

    /// <summary>给宿主读的总时长；未知时 <see cref="TimeSpan.Zero"/>。</summary>
    public TimeSpan CurrentDuration => Snapshot.Duration;

    public int CurrentIndex()
    {
        var doc = _doc;
        return doc is null || doc.IsEmpty ? -1 : doc.IndexAt(LyricPosition());
    }

    /// <summary>岛上那一行文字。任何状态下都返回可直接绘制的内容（空串 = 不显示）。</summary>
    public string CurrentText()
    {
        var snap = Snapshot;
        if (!snap.HasSession) return "";

        var doc = _doc;
        if (doc is null || doc.IsEmpty)
        {
            return _status switch
            {
                "searching" => "同步歌词…",
                "not-found" => "暂无歌词",
                "offline" => "歌词获取失败",
                _ => BuildFallbackTitle(snap),
            };
        }

        int idx = doc.IndexAt(LyricPosition());
        if (idx < 0) return doc.Title;

        var line = doc.Lines[idx];
        return LrcParser.Flatten(line.Text);
    }

    private static string BuildFallbackTitle(PlaybackSnapshot snap)
        => string.IsNullOrWhiteSpace(snap.Artist) ? snap.Title : snap.Title + " · " + snap.Artist;

    // ---- 后台 ----

    /// <summary>
    /// 250ms 节拍，做两件事：发现换歌就起一次歌词解析；发现当前句变了就让宿主重测宽度。
    /// 文字本身由 <see cref="CurrentText"/> 在排版时现算，这里不碰渲染。
    /// </summary>
    private void Tick()
    {
        var snap = Snapshot;

        // 先让墙钟估算器往前走一格（没有时间轴时它是唯一进度来源）
        _ = EffectivePosition(snap);

        bool hasSession = snap.HasSession;
        bool playing = snap.IsPlaying;
        string key = snap.Key;

        bool songChanged = key != _songKey;
        bool stateChanged = songChanged || playing != _lastPlaying || hasSession != _lastHasSession;

        if (songChanged)
        {
            _songKey = key;
            if (_lyricsEnabled) HandleSongChanged(snap);
        }

        _lastPlaying = playing;
        _lastHasSession = hasSession;

        if (stateChanged) RaiseStateChanged();

        if (!_lyricsEnabled) return;

        var doc = _doc;
        if (doc is null || doc.IsEmpty) return;

        int idx = doc.IndexAt(LyricPosition());
        if (idx == _lineIndex) return;

        // 交界处抖动：进度在两句之间来回摆 200ms 时，别让「上一句」闪一下
        if (_lineIndex >= 0 && idx == _lineIndex - 1 && _lineIndex < doc.Lines.Length)
        {
            TimeSpan lineStart = doc.Lines[_lineIndex].Time;
            if (LyricPosition() > lineStart - TimeSpan.FromMilliseconds(350)) return;
        }

        _lineIndex = idx;
        Diag.Log($"行切换 → 第 {idx} 句 @ {LyricPosition():hh\\:mm\\:ss}（源 {TimelineSource}）");
        _host.InvalidateWidgetLayout();
    }

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); }
        catch (Exception ex) { Diag.Log("状态广播异常: " + ex.Message); }
    }

    private void HandleSongChanged(PlaybackSnapshot snap)
    {
        // 换歌：立刻清空旧歌词，避免新歌前几秒还挂着上一首的最后一句
        _doc = null;
        _lineIndex = int.MinValue;
        _songCts?.Cancel();
        _songCts?.Dispose();
        _songCts = null;

        if (!snap.HasSession || string.IsNullOrWhiteSpace(snap.Title))
        {
            _status = "idle";
            _host.InvalidateWidgetLayout();
            return;
        }

        string key = snap.Key;
        if (key == _loadedKey && (snap.SongId <= 0 || _loadedSongId == snap.SongId))
        {
            _host.InvalidateWidgetLayout();
            return;
        }

        _status = "searching";
        _host.InvalidateWidgetLayout();

        var cts = new CancellationTokenSource();
        _songCts = cts;

        // CDP 给到曲目 id 就按 id 直取；只有 SMTC 时才退回「歌名 + 歌手 + 时长」搜索
        var query = new SongQuery(snap.Title, snap.Artist, snap.Duration, snap.SongId);
        string source = TimelineSource;

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _provider.ResolveAsync(query, cts.Token);
                if (cts.IsCancellationRequested) return;

                _doc = result.Doc;
                _status = result.Status;
                _loadedKey = key;
                _loadedSongId = query.SongId;
                _lineIndex = int.MinValue;
                Diag.Log($"歌词[{result.Status}] {query.Title} / {query.Artist} " +
                         $"(id={query.SongId}, 时长 {(int)query.Duration.TotalSeconds}s, 进度源 {source}) → " +
                         $"{result.Doc?.Lines.Length ?? 0} 行{(result.Detail is null ? "" : " · " + result.Detail)}");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _status = "error";
                Diag.Log("解析歌词异常: " + ex);
            }

            _host.InvalidateWidgetLayout();
        });
    }

    private void OnSettingsChanged()
    {
        if (int.TryParse(_host.GetSetting("offsetMs", "0"), out int off)) Volatile.Write(ref _offsetMs, off);
        _lineIndex = int.MinValue;
        _host.InvalidateWidgetLayout();
    }

    public void NudgeOffset(int deltaMs)
    {
        int next = Math.Clamp(OffsetMs + deltaMs, -10000, 10000);
        Volatile.Write(ref _offsetMs, next);
        _host.SetSetting("offsetMs", next.ToString());
        _lineIndex = int.MinValue;
        _host.InvalidateWidgetLayout();
    }

    // ---- 传输控制 ----
    //
    // 优先走 CDP：它直接点播放器自己的按钮，不依赖 SMTC 会话在不在（Chromium 暂停久了会注销会话）。
    // SMTC 只作为兜底。

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

    public void Seek(TimeSpan position) => _smtc.Seek(position);

    public void Dispose()
    {
        StateChanged = null;
        _cdp.Dispose();
        _smtc.Dispose();
        _songCts?.Cancel();
        _songCts?.Dispose();
        _songCts = null;
        _ticker.Dispose();
        try { _host.SettingsChanged -= OnSettingsChanged; } catch { }
    }
}
