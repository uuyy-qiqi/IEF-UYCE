using PclLike.Core.Minecraft.Install;
using PclLike.Core.Minecraft.Launch;
using PclLike.Core.Storage;

namespace PclLike.Core.Minecraft;

/// <summary>
/// 启动一个版本：安装 → 选 Java → 拼参数 → 起进程。
/// 这是上层（CLI / UI）唯一需要打交道的入口。
/// </summary>
public sealed class LaunchService : IDisposable
{
    private readonly string _gameRoot;
    private readonly Downloader _downloader;
    private readonly InstanceInstaller _installer;

    public LaunchService(string gameRoot, Downloader? downloader = null)
    {
        _gameRoot = gameRoot;
        _downloader = downloader ?? new Downloader();
        _installer = new InstanceInstaller(gameRoot, _downloader);
    }

    public InstanceInstaller Installer => _installer;

    public sealed record LaunchOptions
    {
        public string VersionId { get; init; } = "1.20.1";
        public string Username { get; init; } = "Player";
        public string Uuid { get; init; } = "00000000000000000000000000000000";
        public string AccessToken { get; init; } = "0";
        public int MaxMemoryMb { get; init; } = 4096;
        public int MinMemoryMb { get; init; } = 512;
        public string? JavaPath { get; init; }
        public string? ServerAddress { get; init; }
        public int? ServerPort { get; init; }
        public IReadOnlyList<string> ExtraJvmArgs { get; init; } = [];
        public IReadOnlyList<string> ExtraGameArgs { get; init; } = [];
        public bool SkipInstall { get; init; }

        /// <summary>
        /// 找不到合适 Java 时，是否自动下载运行时。默认开。
        /// 关掉的话，没有任何匹配 Java 就会直接抛异常。
        /// </summary>
        public bool AutoDownloadJava { get; init; } = true;

        /// <summary>
        /// 要启动的实例。为 null 时退化成"根目录直接启动"（共享模式），
        /// 兼容 CLI 不指定实例的用法。
        /// </summary>
        public GameInstance? Instance { get; init; }
    }

    public sealed record LaunchOutcome(int ExitCode, bool Crashed, string? Reason, string LogPath);

    /// <summary>
    /// 游戏进程句柄。进程起来之后才有值，退出后置空。
    /// 上层（GUI）用它实现"结束游戏"。
    /// </summary>
    public GameLauncher.LaunchHandle? CurrentHandle { get; private set; }

    /// <summary>游戏是否正在运行。</summary>
    public bool IsRunning => CurrentHandle is { Process.HasExited: false };

    /// <summary>强制结束当前游戏进程（含子进程）。返回是否真的杀掉了东西。</summary>
    public bool KillGame()
    {
        var handle = CurrentHandle;
        if (handle is null) return false;

        try
        {
            if (handle.Process.HasExited) return false;

            // 游戏会派生子进程（尤其是带 native launcher 的情况），
            // 只杀父进程会留下孤儿，所以整棵树一起杀
            handle.Process.Kill(entireProcessTree: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>完整跑一遍：安装（可选）→ 定位 Java → 启动 → 等到退出。</summary>
    public async Task<LaunchOutcome> LaunchAsync(
        LaunchOptions options,
        IProgress<(string Stage, int Done, int Total)>? progress = null,
        Action<string>? onLog = null,
        CancellationToken ct = default)
    {
        var warnList = new List<string>();

        // 0. 先把"游戏目录"定下来。实例隔离的全部效果都取决于这一个值：
        //    --gameDir 指到哪儿，存档 / 设置 / Mod 就落在哪儿。
        var instance = options.Instance;
        var gameDir = instance?.GameDir ?? _gameRoot;

        if (instance is not null)
        {
            Directory.CreateDirectory(gameDir);
            onLog?.Invoke($"[实例] {instance.Name}（{instance.Id}，{instance.IsolationLabel}）");
            onLog?.Invoke($"[实例] 游戏目录：{gameDir}");

            // 实例的 JVM 参数 / 内存覆盖全局设置
            if (!string.IsNullOrWhiteSpace(instance.JvmArgs))
                warnList.Add($"实例自定义 JVM 参数：{instance.JvmArgs}");
        }

        // 1. 安装
        InstanceInstaller.InstallResult installed;
        if (options.SkipInstall)
        {
            installed = await VerifyExistingAsync(options.VersionId, ct).ConfigureAwait(false);
        }
        else
        {
            var ip = progress is null ? null : new Progress<InstanceInstaller.InstallProgress>(p =>
                progress.Report((p.Stage, p.Done, p.Total)));
            installed = await _installer.InstallAsync(options.VersionId, ip, ct).ConfigureAwait(false);
        }

        warnList.AddRange(installed.Warnings);

        // 2. 解析版本（拿 javaVersion 要求）
        var resolver = new VersionResolver(async (id, token) =>
        {
            var f = Path.Combine(_installer.VersionDir(id), $"{id}.json");
            return await File.ReadAllTextAsync(f, token).ConfigureAwait(false);
        });
        var version = await resolver.ResolveAsync(options.VersionId, ct).ConfigureAwait(false);

        // 3. 选 Java
        progress?.Report(("定位 Java", 0, 1));
        var required = version.JavaVersion?.MajorVersion ?? 8;
        var javas = JavaLocator.Discover(_gameRoot, options.JavaPath);

        var chosen = JavaLocator.SelectFor(javas, required, out var quality);

        // 机器上没找到合适的 Java（或者找到的全是跨度过大的降级），
        // 自己下一个 —— 这是 PCL2 的做法，比反复提示用户去手动装可靠得多。
        if (options.AutoDownloadJava && (chosen is null || quality == JavaLocator.MatchQuality.Fallback))
        {
            var why = chosen is null
                ? $"机器上没有找到 Java {required}"
                : $"只有 Java {chosen.MajorVersion}，与需要的 {required} 差距过大";

            progress?.Report(($"{why}，准备下载运行时", 0, 1));
            try
            {
                var mgr = new JavaRuntimeManager(_gameRoot);
                var downloaded = await mgr.EnsureAsync(
                    required,
                    new Progress<(string Stage, long Done, long Total)>(p =>
                        progress?.Report((p.Stage, (int)(p.Total > 0 ? p.Done * 100 / p.Total : 0), 100))),
                    ct).ConfigureAwait(false);

                var info = JavaLocator.Probe(downloaded);
                if (info is not null)
                {
                    chosen = info;
                    quality = JavaLocator.MatchQuality.Exact;
                    javas = [info];
                    warnList.Add($"已自动下载 Java {required}（{info.Vendor} {info.RawVersion}）");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 下载失败不该直接崩掉 —— 至少还有降级候选能用，把失败记进警告让用户看到
                warnList.Add($"自动下载 Java {required} 失败：{ex.Message}");
            }
        }

        if (chosen is null)
            throw new InvalidOperationException(
                $"没有找到可用的 Java。该版本需要 Java {required}。" +
                (options.AutoDownloadJava ? "自动下载也失败了，请检查网络后重试。" : "请安装后重试，或用 --java 指定路径。"));

        if (quality != JavaLocator.MatchQuality.Exact)
        {
            warnList.Add(quality == JavaLocator.MatchQuality.Fallback
                ? $"版本需要 Java {required}，实际使用 Java {chosen.MajorVersion}（{chosen.Path}）—— 跨度过大，启动失败的可能性很高。"
                : $"版本需要 Java {required}，使用更高的 Java {chosen.MajorVersion}（{chosen.Path}）。");
        }

        // 4. 拼参数
        progress?.Report(("构建启动参数", 0, 1));
        var assetsRoot = _installer.AssetsDir;

        // virtual 资源需要把 assetsDir 指到虚拟目录
        var indexFile = Path.Combine(assetsRoot, "indexes", $"{installed.AssetIndexId}.json");
        var virtualDir = Path.Combine(assetsRoot, "virtual", "legacy");
        if (File.Exists(indexFile))
        {
            var idx = await AssetInstaller.LoadIndexAsync(indexFile, ct).ConfigureAwait(false);
            if (idx.Virtual == true && Directory.Exists(virtualDir))
                assetsRoot = virtualDir;
        }

        // 实例覆盖全局设置：内存上限 + 额外 JVM 参数
        var maxMem = options.MaxMemoryMb;
        var extraJvm = options.ExtraJvmArgs.ToList();
        if (instance is not null)
        {
            if (instance.MaxMemoryMb is { } im and > 0) maxMem = im;
            extraJvm.AddRange(instance.ExtraJvmArgs);
        }

        var cfg = new LaunchConfig
        {
            VersionId = options.VersionId,
            GameDirectory = gameDir,
            AssetsRoot = assetsRoot,
            AssetIndexId = installed.AssetIndexId,
            NativesDirectory = installed.NativesDir,
            Classpath = installed.Classpath,
            LibrariesRoot = _installer.LibrariesDir,
            JavaExecutable = chosen.Path,
            Username = options.Username,
            Uuid = options.Uuid,
            AccessToken = options.AccessToken,
            MaxMemoryMb = maxMem,
            MinMemoryMb = options.MinMemoryMb,
            ServerAddress = options.ServerAddress,
            ServerPort = options.ServerPort,
            ExtraJvmArgs = options.ExtraJvmArgs,
            ExtraGameArgs = options.ExtraGameArgs,
            VersionType = version.Type ?? "release",
        };

        var jvmArgs = ArgumentBuilder.BuildJvmArgs(version, cfg);
        var gameArgs = ArgumentBuilder.BuildGameArgs(version, cfg);

        // 实例自定义 JVM 参数追加在参数生成之后，让它能覆盖前面的默认值
        if (extraJvm.Count > 0) jvmArgs = [.. jvmArgs, .. extraJvm];

        var request = new GameLauncher.LaunchRequest(
            JavaExecutable: chosen.Path,
            JvmArgs: jvmArgs,
            GameArgs: gameArgs,
            MainClass: version.MainClass ?? installed.MainClass,
            WorkingDirectory: gameDir);

        // 5. 启动
        progress?.Report(("启动游戏", 0, 1));
        var logDir = Path.Combine(gameDir, "logs");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, $"launch-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var handle = GameLauncher.Start(request, logPath, onLog, onLog);
        CurrentHandle = handle;

        foreach (var w in warnList) onLog?.Invoke($"[警告] {w}");

        try
        {
            var (code, crashed, reason) = await GameLauncher.WaitAsync(handle, ct).ConfigureAwait(false);
            return new LaunchOutcome(code, crashed, reason, logPath);
        }
        catch (OperationCanceledException)
        {
            // 取消 = 这次启动不要了，那就得真把进程收掉。
            //
            // 不能在 finally 里才清句柄、让调用方自己去杀：异常抛出去之后
            // CurrentHandle 已经被清空，调用方手里没有任何东西可杀，游戏会
            // 变成没人管的孤儿进程（关启动器"结束游戏"就是踩的这个坑）。
            try
            {
                if (!handle.Process.HasExited)
                {
                    handle.Process.Kill(entireProcessTree: true);
                    onLog?.Invoke("[启动器] 已结束游戏进程（取消启动）");
                }
            }
            catch
            {
                // 进程可能刚好自己退了，或者权限不够 —— 都不该盖掉真正的取消异常
            }

            throw;
        }
        finally
        {
            // 无论正常退出、崩溃还是被取消，都要把句柄清掉，
            // 否则 IsRunning 会一直报 true
            CurrentHandle = null;
        }
    }

    /// <summary>不安装，仅校验现有文件并组装（用于离线启动已装好的版本）。</summary>
    private async Task<InstanceInstaller.InstallResult> VerifyExistingAsync(string versionId, CancellationToken ct)
    {
        var warnings = new List<string>();
        var resolver = new VersionResolver(async (id, token) =>
        {
            var f = Path.Combine(_installer.VersionDir(id), $"{id}.json");
            return await File.ReadAllTextAsync(f, token).ConfigureAwait(false);
        });

        var version = await resolver.ResolveAsync(versionId, ct).ConfigureAwait(false);
        var vdir = _installer.VersionDir(versionId);

        var clientJar = Path.Combine(vdir, $"{versionId}.jar");
        if (!File.Exists(clientJar))
        {
            var parent = version.InheritsFrom;
            var alt = parent is null ? null : Path.Combine(_installer.VersionsDir, parent, $"{parent}.jar");
            if (alt is not null && File.Exists(alt)) clientJar = alt;
            else warnings.Add($"客户端 jar 不存在：{clientJar}");
        }

        var plans = ClasspathBuilder.PlanAll(version.Libraries ?? []);
        var cp = plans.Where(p => p.Jar is not null)
            .Select(p => Path.Combine(_installer.LibrariesDir,
                (p.Jar!.Path ?? p.Library.ToMavenPath()).Replace('/', Path.DirectorySeparatorChar)))
            .Where(File.Exists)
            .ToList();

        var missing = plans.Count(p => p.Jar is not null) - cp.Count;
        if (missing > 0) warnings.Add($"{missing} 个依赖库缺失，建议重新执行安装");

        cp.Add(clientJar);

        return new InstanceInstaller.InstallResult(
            versionId, clientJar, ClasspathBuilder.JoinClasspath(cp),
            Path.Combine(vdir, "natives"), version.AssetIndex?.Id ?? "legacy",
            version.MainClass ?? "net.minecraft.client.main.Main", warnings);
    }

    public void Dispose() => _downloader.Dispose();
}