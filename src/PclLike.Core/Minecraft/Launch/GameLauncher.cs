using System.Diagnostics;
using System.Text;

namespace PclLike.Core.Minecraft.Launch;

/// <summary>
/// 游戏进程启动。
///
/// 几个必须处理的细节：
///   1. 工作目录设成 .minecraft，否则游戏找不到 saves/options.txt
///   2. 参数里的空格必须自己拼引号，ProcessStartInfo.ArgumentList 在
///      Windows 上对含空格路径的处理比手拼可靠，优先用它
///   3. stdout/stderr 全都要读，否则管道填满会把游戏卡死
///   4. 退出码 0 也可能伴随崩溃（有些崩溃是 JVM 正常退出），
///      所以还要扫日志里的崩溃关键字
/// </summary>
public sealed class GameLauncher
{
    public sealed record LaunchRequest(
        string JavaExecutable,
        IReadOnlyList<string> JvmArgs,
        IReadOnlyList<string> GameArgs,
        string MainClass,
        string WorkingDirectory,
        IReadOnlyDictionary<string, string>? EnvironmentVariables = null);

    public sealed record LaunchHandle(Process Process, string CommandLine, string LogPath);

    /// <summary>把完整命令行拼出来，用于日志与排错。</summary>
    public static string RenderCommandLine(LaunchRequest req)
    {
        var sb = new StringBuilder();
        sb.Append(Quote(req.JavaExecutable));
        foreach (var a in req.JvmArgs) { sb.Append(' ').Append(Quote(a)); }
        sb.Append(' ').Append(Quote(req.MainClass));
        foreach (var a in req.GameArgs) { sb.Append(' ').Append(Quote(a)); }
        return sb.ToString();
    }

    private static string Quote(string s)
        => s.Contains(' ') && !s.StartsWith('"') ? $"\"{s}\"" : s;

    public static LaunchHandle Start(LaunchRequest req, string logPath, Action<string>? onStdout = null,
        Action<string>? onStderr = null)
    {
        Directory.CreateDirectory(req.WorkingDirectory);

        var psi = new ProcessStartInfo
        {
            FileName = req.JavaExecutable,
            WorkingDirectory = req.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var a in req.JvmArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(req.MainClass);
        foreach (var a in req.GameArgs) psi.ArgumentList.Add(a);

        if (req.EnvironmentVariables is not null)
            foreach (var (k, v) in req.EnvironmentVariables) psi.Environment[k] = v;

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var logStream = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
        logStream.WriteLine($"# {DateTimeOffset.Now:O}");
        logStream.WriteLine($"# {RenderCommandLine(req)}");
        logStream.WriteLine();

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            logStream.WriteLine(e.Data);
            onStdout?.Invoke(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            logStream.WriteLine(e.Data);
            onStderr?.Invoke(e.Data);
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        return new LaunchHandle(proc, RenderCommandLine(req), logPath);
    }

    /// <summary>等待退出并扫描日志判定是否崩溃。</summary>
    public static async Task<(int ExitCode, bool LikelyCrashed, string? Reason)> WaitAsync(
        LaunchHandle handle, CancellationToken ct = default)
    {
        await handle.Process.WaitForExitAsync(ct).ConfigureAwait(false);
        var code = handle.Process.ExitCode;
        var (crashed, reason) = InspectLog(handle.LogPath);
        return (code, crashed || code != 0, reason);
    }

    private static readonly string[] CrashMarkers =
    [
        "A fatal error has been detected by the Java Runtime Environment",
        "Netty Epoll",
        "java.lang.UnsatisfiedLinkError",
        "Could not find or load main class",
        "Exception in thread \"main\"",
        "java.lang.ClassNotFoundException",
        "The game crashed whilst",
        "---- Minecraft Crash Report ----",
    ];

    /// <summary>扫描日志判断崩溃。返回的 reason 只保留首条命中行，便于直接展示。</summary>
    public static (bool Crashed, string? Reason) InspectLog(string logPath)
    {
        if (!File.Exists(logPath)) return (false, null);

        try
        {
            // 日志可能很大，只扫尾部 256KB —— 崩溃信息总在最后
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var take = (int)Math.Min(fs.Length, 256 * 1024);
            fs.Seek(-take, SeekOrigin.End);
            var buf = new byte[take];
            var read = fs.Read(buf, 0, take);
            var text = Encoding.UTF8.GetString(buf, 0, read);

            foreach (var line in text.Split('\n'))
            {
                foreach (var marker in CrashMarkers)
                {
                    if (line.Contains(marker, StringComparison.Ordinal))
                        return (true, line.Trim());
                }
            }
        }
        catch
        {
            // 读日志失败不影响判定
        }

        return (false, null);
    }
}