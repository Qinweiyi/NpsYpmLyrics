using System;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NpsYpmLyrics;

/// <summary>
/// 直连 YesPlayMusic 的 Chromium 调试端口，读播放器内部 &lt;audio&gt; 的真实进度。
///
/// <para>
/// 为什么非要这条通道：YesPlayMusic 的 Electron 壳只往 SMTC 注册了「歌名 / 歌手」，
/// 从没调用过 <c>navigator.mediaSession.setPositionState()</c>，所以系统拿到的时间轴恒为
/// <c>pos=0 / end=0</c> —— 光靠 SMTC 根本推不出唱到第几句。
/// </para>
///
/// <para>
/// 要求播放器带 <c>--remote-debugging-port=9222</c> 启动（Electron 原生支持这个开关）。
/// 端口连不上时本通道静默退化，由调用方回落到 SMTC。
/// </para>
/// </summary>
internal sealed class CdpProbe : IDisposable
{
    private const int PollIntervalMs = 350;

    /// <summary>一条表达式把「进度 + 元数据」一次性取回来，避免多次往返。</summary>
    private const string Expression = """
        (() => {
          const a = document.querySelector('audio');
          if (a) {
            const m = navigator.mediaSession && navigator.mediaSession.metadata;
            const cover = m && m.artwork && m.artwork.length ? m.artwork[m.artwork.length - 1].src : '';
            return JSON.stringify({
              t: a.currentTime || 0,
              d: isFinite(a.duration) ? a.duration : 0,
              p: !a.paused && !a.ended,
              title: (m && m.title) || document.title || '',
              artist: (m && m.artist) || '',
              album: (m && m.album) || '',
              cover: cover,
              nid: 0
            });
          }
          const root = document.querySelector('#app');
          const vm = root && root.__vue__;
          const st = (vm && vm.$store && vm.$store.state.player) ||
                     (window.yesplaymusic && window.yesplaymusic.player) || null;
          if (st && st._currentTrack) {
            const tr = st._currentTrack;
            return JSON.stringify({
              t: st._progress || 0,
              d: tr.dt ? tr.dt / 1000 : 0,
              p: !!st._playing,
              title: tr.name || '',
              artist: (tr.ar || []).map(x => x.name).join('/'),
              album: (tr.al && tr.al.name) || '',
              cover: (tr.al && tr.al.picUrl) || '',
              nid: tr.id || 0
            });
          }
          return '';
        })()
        """;

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        UseProxy = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    { Timeout = TimeSpan.FromSeconds(2) };

    private readonly int[] _ports;
    private int _activePort;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    private ClientWebSocket? _ws;
    private int _nextId;
    private PlaybackSnapshot _snapshot = PlaybackSnapshot.None;
    private string _lastError = "";
    private DateTime _lastDataUtc = DateTime.MinValue;

    /// <param name="ports">
    /// 依次尝试的调试端口。第 9333 是我们推荐给 YesPlayMusic 的独占端口 ——
    /// 9222 常被别的插件 / 工具占着（宿主里的 mediacdp 就死盯着 9222），
    /// 两边轮流踢对方的 DevTools 会话，进度就会断续。
    /// </param>
    public CdpProbe(int[] ports)
    {
        _ports = ports is { Length: > 0 } ? ports : new[] { 9333, 9222 };
    }

    public int[] Ports => _ports;

    public PlaybackSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public bool Connected
    {
        get { lock (_gate) return _ws?.State == WebSocketState.Open; }
    }

    public void Start() => _ = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!Connected) await ConnectAsync(ct);
                if (Connected) await PollAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Note(ex.Message);
                Reset();
            }

            try { await Task.Delay(PollIntervalMs, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Note(string error)
    {
        // 只在错误内容变化时记一笔，播放器没开的时候不要刷屏
        if (_lastError == error) return;
        _lastError = error;
        Diag.Log("CDP: " + error);
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        var order = new List<int>();
        if (_activePort > 0) order.Add(_activePort);
        foreach (int p in _ports) if (!order.Contains(p)) order.Add(p);

        foreach (int port in order)
        {
            if (await TryConnectAsync(port, ct)) return;
        }

        // 一路都连不上：如果已经断了很久，就别再拿旧快照当"正在播放"
        if (DateTime.UtcNow - _lastDataUtc > TimeSpan.FromSeconds(12))
            Volatile.Write(ref _snapshot, PlaybackSnapshot.None);
    }

    private async Task<bool> TryConnectAsync(int port, CancellationToken ct)
    {
        string json;
        try
        {
            json = await Http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct);
        }
        catch (Exception ex)
        {
            Note($"端口 {port} 未开放（{ex.GetType().Name}）");
            return false;
        }

        string? wsUrl = PickPageTarget(json);
        if (wsUrl is null)
        {
            Note($"端口 {port} 没有可用的页面目标");
            return false;
        }

        var ws = new ClientWebSocket();
        // 别设 KeepAliveInterval：Chromium 的 DevTools 端点不认客户端 ping，设了会被判超时断开。
        await ws.ConnectAsync(new Uri(wsUrl), ct);

        lock (_gate) { _ws = ws; }
        _activePort = port;
        _lastError = "";
        Diag.Log($"CDP 已连接（端口 {port}）: {wsUrl}");
        return true;
    }

    /// <summary>从 /json/list 里挑出播放器页面（排除 devtools 和空白页）。</summary>
    private static string? PickPageTarget(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        string? fallback = null;
        foreach (var t in doc.RootElement.EnumerateArray())
        {
            if (!t.TryGetProperty("type", out var type) || type.GetString() != "page") continue;
            if (!t.TryGetProperty("webSocketDebuggerUrl", out var wsp) || wsp.GetString() is not { Length: > 0 } url) continue;

            string pageUrl = t.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            if (pageUrl.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase)) continue;

            if (pageUrl.Contains("index.html", StringComparison.OrdinalIgnoreCase) ||
                pageUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return url;

            fallback ??= url;
        }

        return fallback;
    }

    private async Task PollAsync(CancellationToken ct)
    {
        string response = await SendAsync("Runtime.evaluate", new
        {
            expression = Expression,
            returnByValue = true,
            awaitPromise = false,
        }, ct);

        if (response.Length == 0) { Volatile.Write(ref _snapshot, PlaybackSnapshot.None); return; }

        // CDP 把返回值包在 result.result.value 里（我们让 JS 返回 JSON 字符串）
        using var doc = JsonDocument.Parse(response);
        if (!doc.RootElement.TryGetProperty("result", out var result)) return;
        if (!result.TryGetProperty("result", out var inner)) return;
        if (!inner.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String) return;

        string payload = value.GetString() ?? "";
        if (payload.Length == 0) { Volatile.Write(ref _snapshot, PlaybackSnapshot.None); return; }

        using var data = JsonDocument.Parse(payload);
        var root = data.RootElement;

        double t = root.TryGetProperty("t", out var tv) ? tv.GetDouble() : 0;
        double d = root.TryGetProperty("d", out var dv) ? dv.GetDouble() : 0;
        bool playing = root.TryGetProperty("p", out var pv) && pv.ValueKind == JsonValueKind.True;

        string title = root.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "";
        string artist = root.TryGetProperty("artist", out var ar) ? ar.GetString() ?? "" : "";
        string album = root.TryGetProperty("album", out var al) ? al.GetString() ?? "" : "";
        string cover = root.TryGetProperty("cover", out var co) ? co.GetString() ?? "" : "";
        long nid = root.TryGetProperty("nid", out var ni) && ni.ValueKind == JsonValueKind.Number ? ni.GetInt64() : 0;

        if (title.Length == 0 && t <= 0) { Volatile.Write(ref _snapshot, PlaybackSnapshot.None); return; }

        Volatile.Write(ref _snapshot, new PlaybackSnapshot(
            "YesPlayMusic.exe",
            title.Trim(),
            artist.Trim(),
            album.Trim(),
            TimeSpan.FromSeconds(Math.Max(0, t)),
            TimeSpan.FromSeconds(Math.Max(0, d)),
            playing,
            DateTime.UtcNow,
            cover,
            nid));
        _lastDataUtc = DateTime.UtcNow;
    }

    private async Task<string> SendAsync(string method, object parameters, CancellationToken ct)
    {
        ClientWebSocket? ws;
        lock (_gate) ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return "";

        int id = Interlocked.Increment(ref _nextId);
        string request = JsonSerializer.Serialize(new { id, method, @params = parameters });

        await ws.SendAsync(Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, ct);

        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();

        while (true)
        {
            var segment = new ArraySegment<byte>(buffer);
            var result = await ws.ReceiveAsync(segment, ct);
            if (result.MessageType == WebSocketMessageType.Close) { Reset(); return ""; }

            ms.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            string text = Encoding.UTF8.GetString(ms.ToArray());
            ms.SetLength(0);

            // 跳过 CDP 事件（没有 id 的推送），只认我们自己这条请求的回包
            if (!LooksLikeResponse(text, id)) continue;
            return text;
        }
    }

    private static bool LooksLikeResponse(string text, int id)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("id", out var v) && v.TryGetInt32(out int got) && got == id;
        }
        catch { return false; }
    }

    private void Reset()
    {
        lock (_gate)
        {
            try { _ws?.Dispose(); } catch { }
            _ws = null;
        }
    }

    // ---- 传输控制 ----
    //
    // YesPlayMusic 不把播放动作挂在 Vuex 上（_actions 里只有拉数据的），但播放条上的按钮带
    // 稳定的 title 文案，点它和用户手点完全等价，比模拟媒体键可靠。

    private const string ClickExpr = """
        (() => {
          const list = Array.from(document.querySelectorAll('.player button, .player .button-icon'));
          const target = {pattern};
          const hit = list.find(b => target.test((b.title || b.getAttribute('aria-label') || '').trim()));
          if (!hit) return false;
          hit.click();
          return true;
        })()
        """;

    private const string PlayPattern = "/^(播放|暂停|play|pause)$/i";
    private const string NextPattern = "/^(下一首|next)$/i";
    private const string PrevPattern = "/^(上一首|previous|prev)$/i";

    public void TogglePlayPause() => _ = Evaluate(ClickExpr.Replace("{pattern}", PlayPattern));

    public void Next() => _ = Evaluate(ClickExpr.Replace("{pattern}", NextPattern));

    public void Previous() => _ = Evaluate(ClickExpr.Replace("{pattern}", PrevPattern));

    private async Task Evaluate(string expression)
    {
        try { await SendAsync("Runtime.evaluate", new { expression, returnByValue = true }, _cts.Token); }
        catch (Exception ex) { Note("执行失败: " + ex.Message); }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
        Reset();
    }
}
