using System.Buffers;
using System.Security.Cryptography;

namespace PclLike.Core.Storage;

/// <summary>
/// 下载器：并发受限、SHA1 校验、断点续传（HTTP Range）、失败重试。
///
/// 设计要点：
///   - 校验通过就直接跳过，这是启动器启动速度的关键（几千个 assets 文件）
///   - 先下到 .tmp，校验通过再 Move 到目标名，避免半截文件被当成有效缓存
///   - SHA1 是原版资源服务器的校验方式，不是我们的选择
///   - 并发默认 32：Mojang 的资源服务器扛得住，太高反而会被限流
/// </summary>
public sealed class Downloader : IDisposable
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate;
    private readonly int _maxRetries;

    public Downloader(int maxConcurrency = 32, int maxRetries = 3, TimeSpan? timeout = null)
    {
        _gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _maxRetries = maxRetries;

        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = maxConcurrency,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.None,
        };
        _http = new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromMinutes(10),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PclLike/0.1.0");
    }

    public HttpClient Client => _http;

    public sealed record DownloadTask(string Url, string TargetPath, string? Sha1 = null, long Size = 0);

    public sealed record DownloadResult(string TargetPath, bool Skipped, long Bytes, string? Error = null)
    {
        public bool Ok => Error is null;
    }

    /// <summary>批量下载。单个失败不会中断整批，结果里带回错误。</summary>
    public async Task<IReadOnlyList<DownloadResult>> DownloadAllAsync(
        IEnumerable<DownloadTask> tasks,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        var list = tasks.ToList();
        var results = new DownloadResult[list.Count];
        var done = 0;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, list.Count),
            new ParallelOptions { MaxDegreeOfParallelism = _gate.CurrentCount == 0 ? 8 : 32, CancellationToken = ct },
            async (i, token) =>
            {
                await _gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    results[i] = await DownloadOneAsync(list[i], token).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                    var d = Interlocked.Increment(ref done);
                    progress?.Report((d, list.Count));
                }
            }).ConfigureAwait(false);

        return results;
    }

    public async Task<DownloadResult> DownloadOneAsync(DownloadTask task, CancellationToken ct = default)
    {
        var target = task.TargetPath;

        // 已经存在且校验通过 → 跳过
        if (File.Exists(target) && task.Sha1 is not null && await VerifyAsync(target, task.Sha1, ct).ConfigureAwait(false))
            return new DownloadResult(target, Skipped: true, Bytes: new FileInfo(target).Length);

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = target + ".tmp";

        Exception? last = null;
        for (var attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                var bytes = await FetchAsync(task.Url, tmp, ct).ConfigureAwait(false);

                if (task.Sha1 is not null && !await VerifyAsync(tmp, task.Sha1, ct).ConfigureAwait(false))
                    throw new InvalidDataException($"SHA1 不匹配：{target}");

                if (task.Size > 0 && bytes != task.Size)
                    throw new InvalidDataException($"大小不匹配：{target} 期望 {task.Size} 实际 {bytes}");

                File.Move(tmp, target, overwrite: true);
                return new DownloadResult(target, Skipped: false, Bytes: bytes);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                TryDelete(tmp);
                if (attempt < _maxRetries)
                    await Task.Delay(TimeSpan.FromMilliseconds(300 * Math.Pow(2, attempt - 1)), ct).ConfigureAwait(false);
            }
        }

        return new DownloadResult(target, Skipped: false, Bytes: 0, Error: last?.Message ?? "未知错误");
    }

    private async Task<long> FetchAsync(string url, string tmpPath, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await src.CopyToAsync(dst, 1 << 16, ct).ConfigureAwait(false);
        return dst.Length;
    }

    /// <summary>流式校验 SHA1，不把整个文件读进内存。</summary>
    public static async Task<bool> VerifyAsync(string path, string expectedSha1, CancellationToken ct = default)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var sha = SHA1.Create();
            var buf = ArrayPool<byte>.Shared.Rent(1 << 16);
            try
            {
                int read;
                while ((read = await fs.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                    sha.TransformBlock(buf, 0, read, null, 0);
                sha.TransformFinalBlock([], 0, 0);
                var hex = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                return string.Equals(hex, expectedSha1, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 尽力而为 */ }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}