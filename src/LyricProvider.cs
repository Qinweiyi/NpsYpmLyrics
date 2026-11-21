using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NpsYpmLyrics;

internal sealed record SongQuery(string Title, string Artist, TimeSpan Duration, long SongId = 0);

internal sealed class ResolveResult
{
    public LyricDoc? Doc { get; init; }
    /// <summary>ok / searching / not-found / offline / error</summary>
    public string Status { get; init; } = "searching";
    public string? Detail { get; init; }
}

/// <summary>
/// 歌词来源：网易云官方接口。
///
/// <para>
/// YesPlayMusic 本身就是网易云客户端，所以「按歌名 + 歌手 + 时长」在网易云库里反查
/// 命中的是同一条曲目，拿到的是<strong>带毫秒时间戳</strong>的原始 LRC（还能带上翻译），
/// 时间轴比任何第三方聚合接口都准。
/// </para>
///
/// <para>结果落盘缓存，同一首歌二次播放不再发请求（断网也能显示）。</para>
/// </summary>
internal sealed class LyricProvider
{
    private const string SearchApi = "https://music.163.com/api/search/get?s={0}&type=1&limit=30";
    private const string LyricApi = "https://music.163.com/api/song/lyric?id={0}&lv=-1&kv=-1&tv=-1";

    private static readonly HttpClient Http = CreateClient();

    private readonly string _cacheDir;

    public LyricProvider(string cacheDir)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(_cacheDir);
    }

    public string CacheDir => _cacheDir;

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://music.163.com/");
        return client;
    }

    public async Task<ResolveResult> ResolveAsync(SongQuery query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.Title) && query.SongId <= 0)
            return new ResolveResult { Status = "not-found", Detail = "没有歌名" };

        // 0) 从 CDP 拿到了曲目 id —— 直接按 id 取词，不存在「匹配到翻唱」的可能
        if (query.SongId > 0)
        {
            var byId = await ResolveByIdAsync(query, ct);
            if (byId.Doc is { IsEmpty: false }) return byId;
            if (byId.Status == "offline" && query.Title.Length == 0) return byId;
        }

        // 1) 本地缓存
        if (TryLoadCache(query, out LyricDoc? cached) && cached is { IsEmpty: false })
            return new ResolveResult { Doc = cached, Status = "ok" };

        // 2) 搜索候选
        List<Candidate> candidates;
        try
        {
            candidates = await SearchAsync(query, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Diag.Log("搜索失败: " + ex.Message);
            return new ResolveResult { Status = "offline", Detail = ex.Message };
        }

        if (candidates.Count == 0)
            return new ResolveResult { Status = "not-found", Detail = "网易云没有匹配曲目" };

        // 3) 取分数最高的一条的歌词；最高分没有就顺延下一条
        for (int attempt = 0; attempt < Math.Min(2, candidates.Count); attempt++)
        {
            var cand = candidates[attempt];
            try
            {
                var (lrc, tlrc) = await FetchLyricAsync(cand.Id, ct);
                if (string.IsNullOrWhiteSpace(lrc)) continue;

                var doc = LrcParser.Parse(lrc, tlrc, cand.Name, cand.Artist, cand.Id, "网易云", query.Artist);
                if (doc.IsEmpty) continue;

                SaveCache(query, cand, lrc, tlrc);
                return new ResolveResult { Doc = doc, Status = "ok" };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Diag.Log($"取歌词失败 id={cand.Id}: {ex.Message}"); }
        }

        return new ResolveResult { Status = "not-found", Detail = "该曲目没有时间轴歌词" };
    }

    // ---- 按曲目 id 直取 ----

    private async Task<ResolveResult> ResolveByIdAsync(SongQuery q, CancellationToken ct)
    {
        if (TryLoadCache(q, out LyricDoc? cached) && cached is { IsEmpty: false })
            return new ResolveResult { Doc = cached, Status = "ok" };

        try
        {
            var (lrc, tlrc) = await FetchLyricAsync(q.SongId, ct);
            if (string.IsNullOrWhiteSpace(lrc))
                return new ResolveResult { Status = "not-found", Detail = "该曲目没有时间轴歌词" };

            var doc = LrcParser.Parse(lrc, tlrc, q.Title, q.Artist, q.SongId, "网易云", q.Artist);
            if (doc.IsEmpty)
                return new ResolveResult { Status = "not-found", Detail = "歌词为空" };

            SaveCache(q, new Candidate(q.SongId, q.Title, q.Artist, (long)q.Duration.TotalMilliseconds, 100), lrc, tlrc);
            return new ResolveResult { Doc = doc, Status = "ok" };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Diag.Log($"按 id 取歌词失败 id={q.SongId}: {ex.Message}");
            return new ResolveResult { Status = "offline", Detail = ex.Message };
        }
    }

    // ---- 搜索 ----

    private sealed record Candidate(long Id, string Name, string Artist, long DurationMs, double Score);

    private static async Task<List<Candidate>> SearchAsync(SongQuery query, CancellationToken ct)
    {
        string keyword = (query.Title + " " + CleanArtist(query.Artist)).Trim();
        string url = string.Format(CultureInfo.InvariantCulture, SearchApi, Uri.EscapeDataString(keyword));

        using var resp = await Http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("songs", out var songs) ||
            songs.ValueKind != JsonValueKind.Array)
        {
            return new List<Candidate>();
        }

        var list = new List<Candidate>();
        foreach (var s in songs.EnumerateArray())
        {
            try
            {
                long id = s.GetProperty("id").GetInt64();
                string name = s.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";

                var artists = new List<string>();
                if (s.TryGetProperty("artists", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in arr.EnumerateArray())
                        if (a.TryGetProperty("name", out var an) && an.GetString() is { Length: > 0 } nm) artists.Add(nm);
                }

                long dur = s.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64() : 0;
                string artist = string.Join("/", artists);

                double score = Score(query, name, artist, dur);
                if (score < 0) continue;

                list.Add(new Candidate(id, name, artist, dur, score));
            }
            catch { }
        }

        return list.OrderByDescending(c => c.Score).ToList();
    }

    private static double Score(SongQuery q, string name, string artist, long durationMs)
    {
        double title = TextSim(Normalize(q.Title), Normalize(name));
        if (title < 0.86) return -1;

        double artistScore = ArtistSim(q.Artist, artist);

        double durScore = 0;
        if (q.Duration > TimeSpan.Zero && durationMs > 0)
        {
            double diff = Math.Abs(q.Duration.TotalSeconds - durationMs / 1000.0);
            durScore = diff <= 1.5 ? 1.0 : diff <= 4 ? 0.55 : diff <= 10 ? 0.15 : -0.6;
        }

        // 歌手完全对不上要扣分：匿名搜索里同名的翻唱往往一大把，不压一下就会挑错
        double artistPenalty = (q.Artist.Length > 0 && artist.Length > 0 && artistScore <= 0) ? -0.8 : 0;

        // 时长对不上、歌手也对不上的，多半是撞了同名的另一首歌，直接判死
        if (q.Duration > TimeSpan.Zero && durScore < 0 && artistScore <= 0) return -1;

        return title * 3.0 + artistScore * 1.5 + durScore * 1.5 + artistPenalty + VersionPenalty(name);
    }

    private static readonly string[] VersionMarks =
    {
        "伴奏", "纯音乐", "翻唱", "cover", "女声", "男声", "童声", "钢琴", "吉他", "弦乐",
        "dj", "remix", "抖音", "铃声", "片段", "合唱", "live", "现场", "karaoke", "instrumental",
    };

    /// <summary>
    /// 版本惩罚：括号里写着「钢琴版 / 伴奏 / DJ版」这类标记的候选，通常是改编或无词版，
    /// 拿它的时间轴去对原唱必然错位，宁可往后排。
    /// </summary>
    private static double VersionPenalty(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;

        string lowered = name.ToLowerInvariant();
        bool marked = false;

        foreach (System.Text.RegularExpressions.Match m in Brackets.Matches(lowered))
        {
            string inner = m.Groups[1].Value;
            if (inner.Length == 0) continue;
            foreach (string mark in VersionMarks)
            {
                if (inner.Contains(mark, StringComparison.Ordinal)) { marked = true; break; }
            }
            if (marked) break;
        }

        if (!marked)
        {
            foreach (string mark in VersionMarks)
            {
                // 没有括号的裸标题里出现「伴奏」这类词，同样视为改编版
                if (mark.Length >= 2 && lowered.Contains(mark, StringComparison.Ordinal)) { marked = true; break; }
            }
        }

        return marked ? -1.2 : 0;
    }

    private static readonly System.Text.RegularExpressions.Regex Brackets = new(
        @"[（(\[【]([^）)\]】]*)[）)\]】]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static async Task<(string Lrc, string? Translation)> FetchLyricAsync(long id, CancellationToken ct)
    {
        string url = string.Format(CultureInfo.InvariantCulture, LyricApi, id);
        using var resp = await Http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        string lrc = "";
        string? tlrc = null;

        if (doc.RootElement.TryGetProperty("lrc", out var l) &&
            l.TryGetProperty("lyric", out var ly) && ly.GetString() is { } s) lrc = s;

        if (doc.RootElement.TryGetProperty("tlyric", out var t) &&
            t.TryGetProperty("lyric", out var ty) && ty.GetString() is { Length: > 0 } ts) tlrc = ts;

        return (lrc, tlrc);
    }

    // ---- 缓存 ----

    private string CachePath(SongQuery q)
    {
        if (q.SongId > 0) return Path.Combine(_cacheDir, "id_" + q.SongId + ".json");

        string key = Normalize(q.Artist) + "|" + Normalize(q.Title);
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(_cacheDir, Convert.ToHexString(hash).ToLowerInvariant() + ".json");
    }

    private bool TryLoadCache(SongQuery q, out LyricDoc? doc)
    {
        doc = null;
        try
        {
            string path = CachePath(q);
            if (!File.Exists(path)) return false;

            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            string lrc = root.GetProperty("lrc").GetString() ?? "";
            string? tlrc = root.TryGetProperty("tlrc", out var t) ? t.GetString() : null;
            string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? q.Title : q.Title;
            string artist = root.TryGetProperty("artist", out var a) ? a.GetString() ?? q.Artist : q.Artist;
            string qartist = root.TryGetProperty("qartist", out var qa) ? qa.GetString() ?? q.Artist : q.Artist;
            long id = root.TryGetProperty("id", out var i) ? i.GetInt64() : 0;

            doc = LrcParser.Parse(lrc, tlrc, name, artist, id, "网易云（缓存）", qartist);
            return true;
        }
        catch { return false; }
    }

    private void SaveCache(SongQuery q, Candidate c, string lrc, string? tlrc)
    {
        try
        {
            var payload = new
            {
                id = c.Id,
                name = c.Name,
                artist = c.Artist,
                qartist = q.Artist,
                lrc,
                tlrc,
                savedAt = DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
            };
            File.WriteAllText(CachePath(q), JsonSerializer.Serialize(payload));
        }
        catch (Exception ex) { Diag.Log("写缓存失败: " + ex.Message); }
    }

    // ---- 文本相似度 ----

    private static string CleanArtist(string artist)
    {
        if (string.IsNullOrEmpty(artist)) return "";
        int idx = artist.IndexOf(" - ", StringComparison.Ordinal);
        return idx > 0 ? artist.Substring(0, idx) : artist;
    }

    /// <summary>归一化：小写、去括号内容、去标点与空白。</summary>
    private static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        var sb = new StringBuilder(s.Length);
        int depth = 0;
        foreach (char c in s)
        {
            if (c is '（' or '(' or '【' or '[') { depth++; continue; }
            if (c is '）' or ')' or '】' or ']') { if (depth > 0) depth--; continue; }
            if (depth > 0) continue;

            if (char.IsWhiteSpace(c)) continue;
            if (char.IsPunctuation(c) || char.IsSymbol(c)) continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static double TextSim(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1.0;
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)) return 0.92;

        // 双字组 Dice 系数：中文歌名一个字都不能错，英文标题则容忍词序变化
        var ca = Bigrams(a);
        var cb = Bigrams(b);
        if (ca.Count == 0 || cb.Count == 0) return 0;

        int inter = 0;
        foreach (var kv in ca)
            if (cb.TryGetValue(kv.Key, out int n)) inter += Math.Min(n, kv.Value);

        int total = ca.Values.Sum() + cb.Values.Sum();
        return total == 0 ? 0 : 2.0 * inter / total;
    }

    private static Dictionary<string, int> Bigrams(string s)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        if (s.Length == 1) { map[s] = 1; return map; }
        for (int i = 0; i + 1 < s.Length; i++)
        {
            string g = s.Substring(i, 2);
            map[g] = map.TryGetValue(g, out int n) ? n + 1 : 1;
        }
        return map;
    }

    private static double ArtistSim(string want, string got)
    {
        if (string.IsNullOrWhiteSpace(want) || string.IsNullOrWhiteSpace(got)) return 0.5;

        var wa = SplitArtists(want);
        var ga = SplitArtists(got);
        if (wa.Count == 0 || ga.Count == 0) return 0.5;
        if (wa.Overlaps(ga)) return 1.0;

        // 单边包含：歌手串里带了合作艺人
        foreach (var w in wa)
            foreach (var g in ga)
                if (w.Length > 1 && g.Length > 1 && (w.Contains(g, StringComparison.Ordinal) || g.Contains(w, StringComparison.Ordinal)))
                    return 0.75;

        return 0;
    }

    private static HashSet<string> SplitArtists(string s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in s.Split(new[] { '/', '&', ',', '，', '、', ';', '；', '·' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string n = Normalize(part);
            n = n.Replace("feat", "", StringComparison.Ordinal).Replace("ft", "", StringComparison.Ordinal);
            if (n.Length > 0) set.Add(n);
        }
        return set;
    }
}
