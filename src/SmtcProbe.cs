using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace NpsYpmLyrics;

/// <summary>播放快照。所有成员都是不可变值，跨线程传递只需换引用。</summary>
internal sealed record PlaybackSnapshot(
    string AppId,
    string Title,
    string Artist,
    TimeSpan Position,
    TimeSpan Duration,
    bool IsPlaying,
    DateTime CapturedUtc,
    string? CoverUrl = null,
    int Liked = -1)
{
    public static readonly PlaybackSnapshot None =
        new("", "", "", TimeSpan.Zero, TimeSpan.Zero, false, DateTime.MinValue);

    public bool HasSession => CapturedUtc != DateTime.MinValue;

    public string Key => HasSession ? Artist + "\u0001" + Title : "";
}

/// <summary>
/// 系统媒体会话（SMTC）探针。
///
/// <para>
/// 为什么走 SMTC：YesPlayMusic 的 Electron 外壳会把当前曲目与时间轴注册进 SMTC
/// （宿主日志里出现过 <c>[应用图标] YesPlayMusic.exe 取到 96x96</c>），
/// 这条路零侵入 —— 不用改播放器启动参数、不用注入进程。
/// </para>
///
/// <para>
/// 轮询 400ms：Electron 上报 <c>Position</c> 的节奏不固定，靠 <c>LastUpdatedTime</c>
/// 把位置锚到真实时刻，再交给调用方按墙钟外推，歌词才不会越唱越偏。
/// </para>
/// </summary>
internal sealed class SmtcProbe : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly string[] _keywords;
    private readonly object _gate = new();

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private PlaybackSnapshot _snapshot = PlaybackSnapshot.None;

    public SmtcProbe(string[] keywords) => _keywords = keywords;

    public PlaybackSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public void Start() => _ = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

                GlobalSystemMediaTransportControlsSession? session = PickSession(_manager);
                if (!ReferenceEquals(session, _session))
                {
                    lock (_gate) _session = session;
                }

                Publish(session is null ? PlaybackSnapshot.None : await ReadAsync(session));
            }
            catch (Exception ex)
            {
                Diag.Log("SMTC 轮询异常: " + ex.Message);
            }

            try { await Task.Delay(400, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// 挑会话：白名单里正在播的那一路优先；都没在播就退而取白名单里第一个（暂停态仍然显示歌词）。
    /// 白名单为空 = 跟随系统当前会话（任何播放器都能用）。
    /// </summary>
    private GlobalSystemMediaTransportControlsSession? PickSession(
        GlobalSystemMediaTransportControlsSessionManager manager)
    {
        var sessions = manager.GetSessions();
        if (sessions is null || sessions.Count == 0) return null;

        if (_keywords.Length == 0)
        {
            return manager.GetCurrentSession() ?? sessions[0];
        }

        GlobalSystemMediaTransportControlsSession? fallback = null;
        foreach (var s in sessions)
        {
            string id = s.SourceAppUserModelId ?? "";
            if (!_keywords.Any(k => id.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;

            if (IsPlaying(s)) return s;
            fallback ??= s;
        }

        return fallback;
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.GetPlaybackInfo()?.PlaybackStatus
                   == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch { return false; }
    }

    private static async Task<PlaybackSnapshot> ReadAsync(GlobalSystemMediaTransportControlsSession session)
    {
        var props = await session.TryGetMediaPropertiesAsync();

        string appId = "";
        string title = "";
        string artist = "";
        try { appId = session.SourceAppUserModelId ?? ""; } catch { }
        if (props is not null)
        {
            title = props.Title ?? "";
            artist = props.Artist ?? "";
        }

        TimeSpan pos = TimeSpan.Zero, dur = TimeSpan.Zero;
        bool playing = false;

        try
        {
            var info = session.GetPlaybackInfo();
            playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch { }

        try
        {
            var tl = session.GetTimelineProperties();
            if (tl is not null)
            {
                dur = tl.EndTime > TimeSpan.Zero ? tl.EndTime : tl.MaxSeekTime;
                pos = tl.Position > TimeSpan.Zero ? tl.Position : tl.StartTime;

                // 把位置锚到 SMTC 自己记录的时刻，再补上「到现在」这段墙钟：
                // Electron 常常只在换歌 / 拖动时更新 Position，直接信它会让歌词整段落后。
                DateTime nowUtc = DateTime.UtcNow;
                DateTime anchor = tl.LastUpdatedTime.UtcDateTime;
                if (playing && anchor > nowUtc - TimeSpan.FromSeconds(30) && anchor < nowUtc + TimeSpan.FromSeconds(2))
                {
                    TimeSpan delta = nowUtc - anchor;
                    if (delta > TimeSpan.Zero) pos += delta;
                }

                if (dur > TimeSpan.Zero && pos > dur) pos = dur;
            }
        }
        catch { }

        return new PlaybackSnapshot(appId, title.Trim(), artist.Trim(),
            pos < TimeSpan.Zero ? TimeSpan.Zero : pos, dur, playing, DateTime.UtcNow);
    }

    private void Publish(PlaybackSnapshot snap) => Volatile.Write(ref _snapshot, snap);

    // ---- 传输控制 ----

    public void TogglePlayPause() => Fire(s => s.TryTogglePlayPauseAsync());
    public void Next() => Fire(s => s.TrySkipNextAsync());
    public void Previous() => Fire(s => s.TrySkipPreviousAsync());

    public void OpenApp()
    {
        try
        {
            var snap = Snapshot;
            string? exe = ResolveProcessPath(snap.AppId);
            if (exe != null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Exception ex) { Diag.Log("打开播放器失败: " + ex.Message); }
    }

    private static string? ResolveProcessPath(string appId)
    {
        if (string.IsNullOrEmpty(appId)) return null;
        if (File.Exists(appId)) return appId;

        string name = Path.GetFileName(appId);
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";

        foreach (var p in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)))
        {
            try
            {
                string? path = p.MainModule?.FileName;
                if (path != null) return path;
            }
            catch { }
        }
        return null;
    }

    private void Fire(Func<GlobalSystemMediaTransportControlsSession, object?> action)
    {
        GlobalSystemMediaTransportControlsSession? session;
        lock (_gate) session = _session;
        if (session is null) return;

        _ = Task.Run(async () =>
        {
            try { await Task.Yield(); action(session); }
            catch (Exception ex) { Diag.Log("媒体控制失败: " + ex.Message); }
        });
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
        lock (_gate) _session = null;
        _manager = null;
    }
}
