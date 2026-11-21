using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NpsYpmLyrics;

internal sealed class LyricLine
{
    public TimeSpan Time;
    public string Text = "";
    public string? Translation;

    /// <summary>该行结束时刻（下一行开始），最后一行用总时长兜底。</summary>
    public TimeSpan EndTime;
}

/// <summary>一段已按时间排好序的歌词。不可变，可安全跨线程共享。</summary>
internal sealed class LyricDoc
{
    public LyricLine[] Lines = Array.Empty<LyricLine>();
    public string Title = "";
    public string Artist = "";
    public long SongId;
    public string Source = "";

    public bool IsEmpty => Lines.Length == 0;

    /// <summary>返回 <paramref name="position"/> 时刻应显示的歌词下标；还没到第一句返回 -1。</summary>
    public int IndexAt(TimeSpan position)
    {
        var lines = Lines;
        if (lines.Length == 0) return -1;
        if (position < lines[0].Time) return -1;

        int lo = 0, hi = lines.Length - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (lines[mid].Time <= position) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return best;
    }
}

/// <summary>LRC 解析：多时间标签、毫秒进位、翻译行合并、片头制作人员名单过滤。</summary>
internal static class LrcParser
{
    private static readonly Regex TimeTag = new(
        @"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);

    // [ti:xxx] [ar:xxx] 这类 ID 标签
    private static readonly Regex IdTag = new(@"^\[[a-zA-Z]+:.*\]$", RegexOptions.Compiled);

    // 网易云歌词开头的制作人员名单，播放时一闪而过很碍眼，直接丢掉
    private static readonly Regex Credits = new(
        @"^\s*(词曲|作词|作曲|编曲|制作人|出品人|出品|监制|混音|母带|录音|人声|合声|和声|和音|配唱|吉他|贝斯|鼓|键盘|弦乐|" +
        @"OP|SP|词|曲|发行|统筹|企划|导演)\s*(工程|助理|室|师|编写|制作)?\s*[:：]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>「歌名 - 歌手 (English Name)」这种片头行。</summary>
    private static readonly Regex TitleLine = new(
        @"^\s*[^\-–—:：]{1,30}\s[-–—]\s[^\-–—]{1,40}(\s*[（(][^）)]*[）)])?\s*$",
        RegexOptions.Compiled);

    public static LyricDoc Parse(string lrc, string? translation, string title, string artist, long id, string source,
        string? knownArtist = null)
    {
        var doc = new LyricDoc { Title = title, Artist = artist, SongId = id, Source = source };
        if (string.IsNullOrWhiteSpace(lrc)) return doc;

        int offsetMs = 0;
        var lines = new List<LyricLine>(128);

        foreach (var raw in lrc.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("[offset:", StringComparison.OrdinalIgnoreCase))
            {
                string num = line.Substring(8).TrimEnd(']').Trim();
                if (int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out int off)) offsetMs = off;
                continue;
            }

            var matches = TimeTag.Matches(line);
            if (matches.Count == 0) continue;

            string text = TimeTag.Replace(line, "").Trim();
            if (text.Length == 0) continue;          // 纯时间标签行（间奏占位）跳过
            if (IdTag.IsMatch(line) && text.Length == 0) continue;
            if (Credits.IsMatch(text)) continue;

            foreach (Match m in matches)
            {
                int min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                int sec = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                int ms = 0;
                if (m.Groups[3].Success)
                {
                    string frac = m.Groups[3].Value;
                    ms = frac.Length switch
                    {
                        1 => int.Parse(frac, CultureInfo.InvariantCulture) * 100,
                        2 => int.Parse(frac, CultureInfo.InvariantCulture) * 10,
                        _ => int.Parse(frac.Substring(0, 3), CultureInfo.InvariantCulture),
                    };
                }

                lines.Add(new LyricLine
                {
                    Time = new TimeSpan(0, 0, min, sec, ms),
                    Text = text,
                });
            }
        }

        if (offsetMs != 0)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                var t = lines[i].Time + TimeSpan.FromMilliseconds(offsetMs);
                lines[i].Time = t < TimeSpan.Zero ? TimeSpan.Zero : t;
            }
        }

        lines.Sort((a, b) => a.Time.CompareTo(b.Time));

        // 同一时刻重复（多见于「词/曲」与正文同刻）只留第一条
        var unique = new List<LyricLine>(lines.Count);
        foreach (var l in lines)
        {
            if (unique.Count > 0 && unique[^1].Time == l.Time && unique[^1].Text == l.Text) continue;
            unique.Add(l);
        }

        if (!string.IsNullOrWhiteSpace(translation))
        {
            var map = new Dictionary<long, string>();
            foreach (var raw in translation.Replace("\r\n", "\n").Split('\n'))
            {
                var ms = TimeTag.Matches(raw.Trim());
                if (ms.Count == 0) continue;
                string text = TimeTag.Replace(raw, "").Trim();
                if (text.Length == 0) continue;
                foreach (Match m in ms)
                {
                    long key = ToMs(m);
                    if (!map.ContainsKey(key)) map[key] = text;
                }
            }

            foreach (var l in unique)
            {
                if (map.TryGetValue((long)Math.Round(l.Time.TotalMilliseconds), out string? tr)) l.Translation = tr;
            }
        }

        for (int i = 0; i < unique.Count; i++)
        {
            unique[i].EndTime = i + 1 < unique.Count ? unique[i + 1].Time : TimeSpan.Zero;
        }

        // 片头（前 35 秒）里出现歌手名的那几句，基本都是「歌名 - 歌手」「合声：某某」这类名单，
        // 正文歌词不会一上来就喊歌手名字。
        var names = new List<string>();
        foreach (var n in new[] { artist, knownArtist })
        {
            if (string.IsNullOrWhiteSpace(n)) continue;
            foreach (var part in n.Split(new[] { '/', '&', ',', '，', '、' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = part.Trim();
                if (trimmed.Length >= 2 && !names.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) names.Add(trimmed);
            }
        }

        int cut = 0;
        while (cut < unique.Count && unique[cut].Time <= TimeSpan.FromSeconds(35))
        {
            string text = unique[cut].Text;

            bool isNameLine = names.Exists(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));
            bool isTitleLine = TitleLine.IsMatch(text);

            if (!isNameLine && !isTitleLine) break;
            cut++;
        }
        if (cut > 0 && cut < unique.Count) unique.RemoveRange(0, cut);

        doc.Lines = unique.ToArray();
        return doc;
    }

    private static long ToMs(Match m)
    {
        int min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int sec = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        int ms = 0;
        if (m.Groups[3].Success)
        {
            string frac = m.Groups[3].Value;
            ms = frac.Length switch
            {
                1 => int.Parse(frac, CultureInfo.InvariantCulture) * 100,
                2 => int.Parse(frac, CultureInfo.InvariantCulture) * 10,
                _ => int.Parse(frac.Substring(0, 3), CultureInfo.InvariantCulture),
            };
        }
        return (min * 60L + sec) * 1000L + ms;
    }

    /// <summary>把一段歌词压成单行文本（岛内空间有限，换行会破坏排版）。</summary>
    public static string Flatten(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            sb.Append(c is '\n' or '\r' or '\t' ? ' ' : c);
        }
        string s = sb.ToString();
        while (s.Contains("  ", StringComparison.Ordinal)) s = s.Replace("  ", " ", StringComparison.Ordinal);
        return s.Trim();
    }
}
