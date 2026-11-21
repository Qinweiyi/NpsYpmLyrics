using System;
using System.IO;
using System.Text;

namespace NpsYpmLyrics;

/// <summary>插件自己的小日志（宿主日志里看不到插件内部异常，排障时靠它）。</summary>
internal static class Diag
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();
    private static string? _path;

    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NotchPeninsula", "ypm-timeline");

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            _path ??= Path.Combine(DataDir, "plugin.log");

            lock (Gate)
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes) File.Delete(_path);

                File.AppendAllText(_path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch { /* 日志失败绝不能影响播放 */ }
    }
}
