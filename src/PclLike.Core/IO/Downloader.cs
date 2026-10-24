using System.Security.Cryptography;

namespace PclLike.Core.IO;

/// <summary>单个下载任务。</summary>
public sealed record DownloadTask
{
    /// <summary>相对下载源的路径，例如 "libraries/xxx.jar" 或 "1.20.1/1.20.1.jar"。</summary>
    public required string RelativePath { get; init; }

    public required string TargetPath { get; init; }

    /// <summary>预期的 SHA1（十六进制小写）。为空则跳过校验。</summary>
    public string? Sha1 { get; init; }

    /// <summary>预期大小（字节）。校验用，未知填 0。</summary>
    public long Size { get; init; }
}

public sealed record DownloadResult(int Completed, int Skipped, int Failed, TimeSpan Elapsed)
{
    public int Total => Completed + Skipped + Failed;
}

/// <summary>
/// 多线程下载器：并发、断点续传、镜像回退、SHA1 校验。
///
/// 关键设计：
///   1. 先写 .tmp 文件，校验通过才原子改名 —— 中断不会留下"看起来正常"的坏文件。
///   2. 已存在且校验通过的文件直接跳过 —— 这就是断点续传，代价最低的实现。
///   3. 镜像按顺序尝试，单个源失败退到下一个。
/// </summary>
public sealed class Downloader
{
    private readonly HttpClient _http;
    private readonly IReadOnlyList<string> _mirrors;
    private readonly int _maxConcurrency;

    public Downloader(HttpClient http, IReadOnlyList<string> mirrors, int maxConcurrency = 16)
    {
        if (mirrors.Count == 0) throw new ArgumentException("至少需要一个下载源", nameof(mirrors));
        _http = http;
        _mirrors = mirrors;
        _maxConcurrency = maxConcurrency;
    }

    public async Task<DownloadResult> RunAsync(
        IReadOnlyList<DownloadTask> tasks,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        int done = 0, skipped = 0, failed = 0;
        var total = tasks.Count;
        var pending = new List<(DownloadTask Task, string Error)>();
        var gate = new object();

        using var sem = new SemaphoreSlim(_maxConcurrency);

        var work = tasks.Select(async task =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (await IsUpToDateAsync(task, ct).ConfigureAwait(false))
                {
                    lock (gate) { skipped++; progress?.Report(new(done + skipped, total, task.RelativePath, ProgressKind.Skipped)); }
                    return;
                }

                var err = await FetchWithMirrorsAsync(task, ct).ConfigureAwait(false);
                lock (gate)
                {
                    if (err is null)
                    {
                        done++;
                        progress?.Report(new(done + skipped, total, task.RelativePath, ProgressKind.Downloaded));
                    }
                    else
                    {
                        failed++;
                        pending.Add((task, err));
                        progress?.Report(new(done + skipped, total, task.RelativePath, ProgressKind.Failed));
                    }
                }
            }
            finally { sem.Release(); }
        });

        await Task.WhenAll(work).ConfigureAwait(false);

        if (pending.Count > 0)
        {
            var sample = string.Join("\n  ", pending.Take(5).Select(p => $"{p.Task.RelativePath}: {p.Error}"));
            throw new AggregateException($"有 {pending.Count}/{total} 个文件下载失败：\n  {sample}");
        }

        return new DownloadResult(done, skipped, failed, DateTime.UtcNow - started);
    }

    /// <summary>文件已存在且哈希匹配 —— 直接跳过。</summary>
    private async Task<bool> IsUpToDateAsync(DownloadTask task, CancellationToken ct)
    {
        if (!File.Exists(task.TargetPath)) return false;
        if (string.IsNullOrEmpty(task.Sha1)) return true;

        var actual = await Sha1OfFileAsync(task.TargetPath, ct).ConfigureAwait(false);
        return string.Equals(actual, task.Sha1, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>依次尝试各镜像，返回 null 表示成功，否则返回最后一个错误。</summary>
    private async Task<string?> FetchWithMirrorsAsync(DownloadTask task, CancellationToken ct)
    {
        string? lastError = null;

        foreach (var mirror in _mirrors)
        {
            var url = CombineUrl(mirror, task.RelativePath);
            var tmp = task.TargetPath + ".tmp";

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(task.TargetPath)!);

                using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();

                    await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                    {
                        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                    }
                }

                // 校验放在临时文件上，通过才改名
                if (!string.IsNullOrEmpty(task.Sha1))
                {
                    var actual = await Sha1OfFileAsync(tmp, ct).ConfigureAwait(false);
                    if (!string.Equals(actual, task.Sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(tmp);
                        lastError = $"SHA1 不匹配: 期望 {task.Sha1}, 实际 {actual}";
                        continue;   // 换源重试
                    }
                }

                File.Move(tmp, task.TargetPath, overwrite: true);
                return null;
            }
            catch (OperationCanceledException) { TryDelete(tmp); throw; }
            catch (Exception ex)
            {
                TryDelete(tmp);
                lastError = $"{mirror} -> {ex.GetType().Name}: {ex.Message}";
            }
        }

        return lastError ?? "所有下载源均失败";
    }

    private static string CombineUrl(string baseUrl, string relative)
        => baseUrl.TrimEnd('/') + "/" + relative.TrimStart('/');

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 清理失败不影响主流程 */ }
    }

    public static async Task<string> Sha1OfFileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        using var sha = SHA1.Create();
        var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static async Task<string> Sha256OfFileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public enum ProgressKind { Downloaded, Skipped, Failed }

public sealed record DownloadProgress(int Done, int Total, string Current, ProgressKind Kind)
{
    public double Percent => Total == 0 ? 0 : Done * 100.0 / Total;
}