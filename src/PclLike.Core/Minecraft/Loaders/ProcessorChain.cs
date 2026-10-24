using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PclLike.Core.Minecraft.Loaders;

/// <summary>
/// Forge / NeoForge 安装器的 processor 链执行器。
///
/// ## 为什么必须有它
///
/// install_profile.json 里的 libraries 只是"能直接下载的 jar"，而 Forge 系的
/// 客户端 jar 有三件产物 **在 maven 上根本不存在**，必须由 installer 自带的
/// Java 工具现场生成：
///
///   client-1.20.1-….jar  ──jarsplitter──▶  -slim.jar + -extra.jar
///   -slim.jar + mappings ──ForgeAutoRenamingTool──▶  -srg.jar
///   -srg.jar + client.lzma ──binarypatcher──▶  forge-….jar（PATCHED）
///
/// 早期实现只下载 installer 里的 version.json / install_profile.json，
/// 合并 libraries 就收工，注释里写着"客户端只依赖 libraries 齐全"——
/// 这个判断是错的。缺了上述产物，启动时 FML 会在 MinecraftLocator.scanMods
/// 报 `Invalid paths argument, contained no existing paths`。
///
/// ## processor 的通用形态
///
///   每个 processor = { jar, classpath, args, outputs, sides }
///   - jar：要跑的 Java 程序的 maven 坐标
///   - args：命令行参数，里面的 {XXX} 占位符要按规则替换
///   - outputs：形如 "@{A}={sha1}; {B}={sha1}" 的校验串（{A} 是 data 表键名）
///   - sides：空 = 双端都跑；含 "client" 才在客户端跑
///
/// ## 占位符替换规则（两套，不能混）
///
///   1. data 表键 `{KEY}` → 该条目是 maven 坐标时替换为**下载后的本地路径**，
///      是字面量时（如 sha1）原样替换；特殊键 `{BINPATCH}` 指向 installer 内部条目。
///   2. 内置变量，如 {ROOT} {INSTALLER} {MINECRAFT_JAR} {SIDE} 等。
/// </summary>
public static partial class ProcessorChain
{
    /// <summary>一条 processor 的执行结果，用于日志与失败定位。</summary>
    public sealed record StepResult(int Index, string Jar, bool Skipped, string? SkipReason,
        int ExitCode, string CommandLine, IReadOnlyList<string> MissingOutputs);

    /// <summary>
    /// 执行整个 processor 链。
    /// </summary>
    /// <param name="installerBytes">installer jar 原始字节（要从中取 data/*.lzma 等条目）</param>
    /// <param name="installProfileJson">installer 里的 install_profile.json</param>
    /// <param name="gameRoot">.minecraft 等价的游戏根目录（{ROOT}）</param>
    /// <param name="side">"client" 或 "server"</param>
    /// <param name="minecraftJar">原版 client.jar 的本地路径（{MINECRAFT_JAR}）</param>
    /// <param name="minecraftVersion">MC 版本号，如 1.20.1</param>
    /// <param name="javaExe">Java 可执行文件路径</param>
    /// <param name="onLog">日志回调</param>
    public static async Task<IReadOnlyList<StepResult>> RunAsync(
        byte[] installerBytes,
        string installProfileJson,
        string gameRoot,
        string side,
        string minecraftJar,
        string minecraftVersion,
        string javaExe,
        Action<string>? onLog = null,
        CancellationToken ct = default)
    {
        var results = new List<StepResult>();

        var profile = JsonNode.Parse(installProfileJson)?.AsObject()
            ?? throw new InvalidOperationException("install_profile.json 不是合法 JSON");

        var librariesRoot = Path.Combine(gameRoot, "libraries");

        var processors = profile["processors"] as JsonArray ?? [];

        // ── 1. 先算出哪些 data 键是**生成物**（processor 的输出）──
        //
        // 这一步必须在建 data 表之前做，因为它决定了每个 Maven 形态的
        // data 条目到底是"要去 maven 下载的输入"还是"processor 现场生成的产物"。
        //
        // 血的教训：曾经无脑把 data 表里所有 [group:artifact:...] 都 Require 去下载，
        // 于是 MAPPINGS / MOJMAPS / MC_SLIM / MC_EXTRA / MC_SRG / PATCHED 这些
        // **maven 上根本不存在**的坐标全去请求，第一个 404 就炸在
        // `de.oceanlabs.mcp:mcp_config:1.20.1-…:mappings` 上。
        // 实际上那份 zip 只以 @zip 主包存在于 maven，:mappings@txt 是
        // installertools 的 MCP_DATA 任务从 zip 里**抽**出来的。
        var outputsByDataKey = CollectGeneratedKeys(processors, side);

        // ── 2. 建立 data 表：键 → 本地路径 或 字面量 ──
        var dataMap = BuildDataTable(profile, gameRoot, librariesRoot, installerBytes,
            side, outputsByDataKey, onLog);

        // ── 3. 收集 processor 依赖 jar 的下载清单 ──
        var downloader = new ProcessorDownloader(librariesRoot, onLog);

        // 产物路径的父目录先建好：installertools 不会自己 mkdir -p
        PrepareOutputDirs(processors, side, dataMap, gameRoot);

        // processor 自己的 jar 也要下载（它们不在 version.json 的 libraries 里）
        foreach (var p in processors)
        {
            if (p is not JsonObject po) continue;
            if (po["jar"]?.GetValue<string>() is { } jarName)
                downloader.Require(jarName);
            if (po["classpath"] is JsonArray cp)
                foreach (var c in cp)
                    if (c?.GetValue<string>() is { } cn) downloader.Require(cn);
        }

        // data 表里**非生成物**的 Maven 条目才是要下载的输入
        // （实践中只有 mcp_config:…@zip 这一条）
        foreach (var path in dataMap.Values.Where(v => v.IsMaven && !v.IsGenerated))
        {
            downloader.Require(path.MavenName!);
            onLog?.Invoke($"      ↓ 输入 {path.MavenName}");
        }

        // 生成物不下载，但必须把它们的路径也登记进 downloader，
        // 否则后面 PathOf() 拿不到路径（它只对 Require 过的名字有记录）。
        foreach (var (key, val) in dataMap.Where(kv => kv.Value.IsGenerated))
            downloader.RegisterLocalOnly(key, val.MavenName!);

        await downloader.DownloadAllAsync(ct).ConfigureAwait(false);

        // ── 3. 逐个执行 ──
        var installerPath = downloader.SaveInstallerCopy(installerBytes);

        for (var i = 0; i < processors.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (processors[i] is not JsonObject po)
                continue;

            var idx = i + 1;
            var jarSpec = po["jar"]?.GetValue<string>() ?? "";

            // sides 过滤：空数组 = 双端；否则必须含当前 side
            var sides = (po["sides"] as JsonArray)?
                .Select(s => s?.GetValue<string>() ?? "")
                .Where(s => s.Length > 0)
                .ToList() ?? [];

            if (sides.Count > 0 && !sides.Contains(side, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(new StepResult(idx, jarSpec, true,
                    $"sides={string.Join(",", sides)}，不含 {side}", 0, "", []));
                onLog?.Invoke($"  [{idx}/{processors.Count}] 跳过 {ShortJar(jarSpec)}（仅 {string.Join("/", sides)}）");
                continue;
            }

            // 解析 classpath：jar 本身 + classpath 数组
            var cpItems = new List<string>();
            if (downloader.PathOf(jarSpec) is { } self) cpItems.Add(self);
            if (po["classpath"] is JsonArray cpArr)
                foreach (var c in cpArr)
                    if (c?.GetValue<string>() is { } cn && downloader.PathOf(cn) is { } cpath)
                        cpItems.Add(cpath);

            // 解析 args：先做 data 表替换，再做内置变量替换
            var rawArgs = (po["args"] as JsonArray)?
                .Select(a => a?.GetValue<string>() ?? "")
                .ToList() ?? [];

            var args = rawArgs.Select(a => Substitute(a, dataMap, gameRoot, installerPath,
                minecraftJar, side, minecraftVersion)).ToList();

            // outputs 里的 sha1 校验值也要先记下来
            var outputs = po["outputs"] is { } outNode2
                ? ParseOutputs(outNode2, dataMap, gameRoot, installerPath,
                    minecraftJar, side, minecraftVersion)
                : [];

            var mainClass = await ResolveMainClassAsync(cpItems[0], ct).ConfigureAwait(false);

            var cmdline = new StringBuilder();
            cmdline.Append(Quote(javaExe));
            cmdline.Append(" -cp ").Append(Quote(string.Join(Path.PathSeparator, cpItems)));
            cmdline.Append(' ').Append(mainClass);
            foreach (var a in args) cmdline.Append(' ').Append(Quote(a));

            onLog?.Invoke($"  [{idx}/{processors.Count}] {ShortJar(jarSpec)} …");

            var (exit, stdout, stderr) = await ExecAsync(javaExe, cpItems, mainClass, args, gameRoot, ct)
                .ConfigureAwait(false);

            if (exit != 0)
            {
                onLog?.Invoke($"      ✗ 退出码 {exit}");
                if (!string.IsNullOrWhiteSpace(stderr))
                    onLog?.Invoke("      stderr: " + Trunc(stderr.Trim(), 1500));
                throw new InvalidOperationException(
                    $"processor {idx}（{jarSpec}）执行失败，退出码 {exit}。" + Environment.NewLine +
                    stderr.Trim());
            }

            // 校验 outputs 的 sha1
            var missing = new List<string>();
            foreach (var (outPath, expectSha) in outputs)
            {
                if (!File.Exists(outPath))
                {
                    missing.Add($"{outPath}（文件不存在）");
                    continue;
                }
                if (expectSha is null) continue;

                var actual = Sha1OfFile(outPath);
                if (!actual.Equals(expectSha, StringComparison.OrdinalIgnoreCase))
                    missing.Add($"{outPath}（sha1 期望 {expectSha}，实际 {actual}）");
            }

            if (missing.Count > 0)
            {
                foreach (var m in missing) onLog?.Invoke($"      ✗ 产物校验失败: {m}");
                throw new InvalidOperationException(
                    $"processor {idx}（{jarSpec}）产物校验失败：" + Environment.NewLine +
                    string.Join(Environment.NewLine, missing));
            }

            foreach (var (outPath, _) in outputs)
                onLog?.Invoke($"      ✓ {Path.GetFileName(outPath)}  {SizeText(outPath)}");

            results.Add(new StepResult(idx, jarSpec, false, null, exit, cmdline.ToString(), missing));
        }

        return results;
    }

    // ═══════════════════════════════════════════════════════════════
    // data 表
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 找出哪些 data 键是 processor 链**现场生成**的产物。
    ///
    /// 判定依据有两条，都要看：
    ///
    ///   1. processor 的 `outputs` 串，形如
    ///      `{"{MC_SLIM}":"{MC_SLIM_SHA}","{MC_EXTRA}":"{MC_EXTRA_SHA}"}`
    ///      —— 等号/冒号左边出现的 {KEY} 就是产物键。
    ///
    ///   2. args 里跟在 `--output` 后面的实参里的 {KEY}。
    ///      installertools 系的 processor（3/4/5）没有 outputs 串，
    ///      只靠 `--output {MAPPINGS}` 这种写法声明产物。
    ///
    /// 只处理**当前 side 会执行**的 processor：给 server 用的产物键
    /// （如 MC_UNPACKED）在 client 安装里不该被当成已生成。
    ///
    /// ⚠️ 这里**故意不过滤 side**：`MC_UNPACKED` 只由 server 侧 processor[2]
    /// 产出，若按 side 过滤，它在 client 安装里会没被标记成产物，
    /// 随后被当成"输入"去请求 `net.minecraft:client:…:unpacked` → 404（实测踩过）。
    /// 它既不是 maven 上的 artifact，client 安装也用不到 —— 标记为产物、
    /// 既不下载也不生成，才是正确语义。
    /// </summary>
    private static HashSet<string> CollectGeneratedKeys(JsonArray processors, string side)
    {
        _ = side;   // 保留参数以表明"这里曾有 side 过滤，是故意去掉的"
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var p in processors)
        {
            if (p is not JsonObject po) continue;

            // 来源 1：outputs —— 实际是 **JSON 对象** `{"{MC_SLIM}":"{MC_SLIM_SHA}"}`，
// 不是字符串。曾经按字符串 `"@{A}={sha}; {B}={sha}"` 处理，碰到真文件就抛
// `The node must be of type 'JsonValue'`。两种形态都兼容。
if (po["outputs"] is { } outNode)
    foreach (var spec in EnumerateOutputPairs(outNode))
        foreach (System.Text.RegularExpressions.Match m in KeyRefs().Matches(spec.Key))
            keys.Add(m.Groups[1].Value);

            // 来源 2：--output 后面的实参
            var args = (po["args"] as JsonArray)?
                .Select(a => a?.GetValue<string>() ?? "")
                .ToList() ?? [];

            for (var i = 0; i < args.Count - 1; i++)
            {
                if (!args[i].Equals("--output", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (System.Text.RegularExpressions.Match m in KeyRefs().Matches(args[i + 1]))
                    keys.Add(m.Groups[1].Value);
            }
        }

        return keys;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\{([A-Z_][A-Z0-9_]*)\}")]
    private static partial System.Text.RegularExpressions.Regex KeyRefs();

    /// <summary>
    /// 把内联的 `[group:artifact:version[:classifier][@ext]]` 还原成本地库路径。
    ///
    /// 方括号是 Forge install_profile 的约定：出现在 args 里的坐标要用方括号包起来，
    /// 表示"这是个 maven 坐标，请解析成你本地库仓库里的路径"。
    /// 一个参数里可能同时出现多个方括号坐标，也可能夹在普通文本之间，
    /// 所以用正则逐个替换而不是只判断整串是否被包住。
    /// </summary>
    private static string UnwrapInlineMaven(string s, string gameRoot)
    {
        if (!s.Contains('[')) return s;

        return InlineMavenRefs().Replace(s, m =>
            MavenLocalPath(gameRoot, m.Groups[1].Value));
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\[([A-Za-z0-9_.\-]+:[A-Za-z0-9_.\-]+:[A-Za-z0-9_.\-+]+[^\]]*)\]")]
    private static partial System.Text.RegularExpressions.Regex InlineMavenRefs();

    /// <summary>
    /// 把 processor 的 `outputs` 节点统一摊平成 `(产物, 期望sha1)` 序列。
    ///
    /// 真实 install_profile.json 里它是 **JSON 对象**：
    ///     "outputs": { "{MC_SLIM}": "{MC_SLIM_SHA}", "{MC_EXTRA}": "{MC_EXTRA_SHA}" }
    ///
    /// 但 Forge 老版本用的是字符串 `"@{A}={sha}; {B}={sha}"`，
    /// 所以两种都支持，调用方不必关心。
    /// </summary>
    private static IEnumerable<(string Key, string? ShaRef)> EnumerateOutputPairs(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (k, v) in obj)
                    yield return (k, v?.GetValue<string>());
                break;

            case JsonValue val when val.TryGetValue<string>(out var s):
                // 字符串形态：去掉外层 @ 与可能包裹的花括号，按 ; 拆
                var body = s.TrimStart('@').Trim().TrimStart('{').TrimEnd('}');
                foreach (var part in body.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = part.Split('=', 2);
                    yield return (kv[0].Trim(), kv.Length > 1 ? kv[1].Trim() : null);
                }
                break;
        }
    }

    /// <summary>data 表里一个条目的解析结果：要么是 maven 库，要么是字面量。</summary>
    /// <param name="IsMaven">是否形如 [group:artifact:version:classifier@ext]</param>
    /// <param name="MavenName">maven 坐标（IsMaven 为真时有效）</param>
    /// <param name="Literal">路径或字面量（IsMaven 为假时有效）</param>
    /// <param name="IsGenerated">
    /// 是否由 processor 链现场生成。生成物**不能**去 maven 下载，
    /// 但它的本地路径仍按 maven 布局算出来，好让后续 processor 找到前序产物。
    /// </param>
    private sealed record DataValue(bool IsMaven, string? MavenName, string Literal,
        bool IsGenerated = false);

    /// <summary>
    /// 把 install_profile 的 data 段解析成 `{KEY} → 值`。
    ///
    /// 条目的四种形态：
    ///   "[group:artifact:version:classifier@ext]"  → maven 坐标（方括号是 Forge 的标记）
    ///                                                —— 但若该键在 outputsByDataKey 里，
    ///                                                   它是产物，不下载
    ///   "'abc123…'"                                 → 引号包着的字面量（通常是 sha1）
    ///   "/data/client.lzma"                         → installer jar 内部条目，要解出来落盘
    ///   "{SIDE}"                                    → 保留原样，执行时再替换
    ///
    /// `{BINPATCH}` 是 installer 内部条目，不是 maven 库：要把
    /// `data/client.lzma`（客户端）或 `data/server.lzma`（服务端）解出来落盘。
    /// </summary>
    private static Dictionary<string, DataValue> BuildDataTable(
        JsonObject profile, string gameRoot, string librariesRoot,
        byte[] installerBytes, string side, HashSet<string> outputsByDataKey,
        Action<string>? onLog)
    {
        var map = new Dictionary<string, DataValue>(StringComparer.Ordinal);
        var data = profile["data"] as JsonObject;
        if (data is null) return map;

        // 先把 installer 当 zip 打开一次，用于提取内嵌条目
        ZipArchive? zip = null;
        try { zip = new ZipArchive(new MemoryStream(installerBytes), ZipArchiveMode.Read); }
        catch { /* 不是 zip 也无妨，只是提取不了内嵌文件 */ }

        foreach (var (key, node) in data)
        {
            if (node is not JsonObject o) continue;

            // 客户端优先取 client，没有则取通用值
            string? raw =
                o["client"]?.GetValue<string>() ??
                o["server"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(raw)) continue;

            // maven 坐标：形如 [group:artifact:version:classifier@ext]
            if (raw.StartsWith('[') && raw.EndsWith(']'))
            {
                var inner = raw[1..^1];
                var generated = outputsByDataKey.Contains(key);

                // 生成物的本地路径仍按 maven 布局算（这样前序 processor 写到这里，
                // 后续 processor 用同样的规则就能找到）；只是不参与下载。
                map[key] = new DataValue(true, inner, "", generated);

                if (generated)
                    onLog?.Invoke($"      ○ {key,-16} 由 processor 生成（不下载）");
                continue;
            }

            // installer 内部路径：形如 /data/client.lzma
            if (raw.StartsWith('/') && zip is not null)
            {
                var entryName = raw.TrimStart('/');
                var entry = zip.Entries.FirstOrDefault(e =>
                    e.FullName.Equals(entryName, StringComparison.OrdinalIgnoreCase));

                if (entry is not null)
                {
                    // 落到 libraries 下一个稳定位置，供 processor 当输入用
                    var outDir = Path.Combine(librariesRoot, "_processor_inputs");
                    Directory.CreateDirectory(outDir);
                    var outPath = Path.Combine(outDir, Path.GetFileName(entryName));

                    if (!File.Exists(outPath))
                    {
                        using var es = entry.Open();
                        using var fs = File.Create(outPath);
                        es.CopyTo(fs);
                    }

                    map[key] = new DataValue(false, null, outPath);
                    onLog?.Invoke($"      ⟳ 解出 {entryName} → {ShortPath(outPath)}  {SizeText(outPath)}");
                    continue;
                }
            }

            // 字面量（含 sha1 那种 'abc123…' 形式）——剥掉 Forge 的单引号标记
            map[key] = new DataValue(false, null, raw.Trim().Trim('\''));
        }

        zip?.Dispose();
        return map;
    }

    /// <summary>
    /// 把参数串里的占位符与内联 maven 坐标换成实际值。
    ///
    /// 三趟替换，顺序不能反：
    ///
    ///   1. 内置变量：{ROOT} {INSTALLER} {MINECRAFT_JAR} {MINECRAFT_VERSION} {SIDE}
    ///   2. data 表键：{KEY} → maven 条目的本地路径 / 字面量
    ///   3. **内联方括号坐标**：[group:artifact:version:classifier@ext] → 本地路径
    ///
    /// 第 3 趟是关键，且必须放在最后：第 2 趟的替换结果本身**不会**再带方括号，
    /// 而 args 里确实存在直接写坐标、不走 {KEY} 的情况，例如 processor[3] 的
    ///     --input [de.oceanlabs.mcp:mcp_config:1.20.1-20230612.114412@zip]
    /// 若不还原，installertools 会原样收到带方括号的字符串并报
    ///     RuntimeException: Input does not exist: [de.oceanlabs.mcp:...@zip]
    /// （实测就是这个报错）。
    /// </summary>
    private static string Substitute(string s, Dictionary<string, DataValue> dataMap,
        string gameRoot, string installerPath, string minecraftJar, string side, string mcVersion)
    {
        // 1. 内置变量（优先级高于 data 表，避免键名撞车）
        s = s.Replace("{ROOT}", gameRoot, StringComparison.Ordinal)
             .Replace("{INSTALLER}", installerPath, StringComparison.Ordinal)
             .Replace("{MINECRAFT_JAR}", minecraftJar, StringComparison.Ordinal)
             .Replace("{MINECRAFT_VERSION}", mcVersion, StringComparison.Ordinal)
             .Replace("{SIDE}", side, StringComparison.Ordinal);

        // 2. data 表键
        foreach (var (key, val) in dataMap)
        {
            var token = "{" + key + "}";
            if (!s.Contains(token, StringComparison.Ordinal)) continue;

            var replacement = val.IsMaven
                ? MavenLocalPath(gameRoot, val.MavenName!)
                : val.Literal;

            s = s.Replace(token, replacement, StringComparison.Ordinal);
        }

        // 3. 内联 maven 坐标：[group:artifact:...@ext] → 本地路径
        s = UnwrapInlineMaven(s, gameRoot);

        return s;
    }

    /// <summary>
    /// 把当前 side 会执行的那些 processor 的**产物路径**预先建好父目录。
    ///
    /// 有些 Java 工具（尤其 installertools 的 MCP_DATA / DOWNLOAD_MOJMAPS）
    /// 不会自己 mkdir -p，父目录不存在就直接抛
    /// `FileNotFoundException` / `NoSuchFileException`。
    /// </summary>
    private static void PrepareOutputDirs(
        JsonArray processors, string side, Dictionary<string, DataValue> dataMap, string gameRoot)
    {
        foreach (var p in processors)
        {
            if (p is not JsonObject po) continue;

            var sides = (po["sides"] as JsonArray)?
                .Select(s => s?.GetValue<string>() ?? "")
                .Where(s => s.Length > 0)
                .ToList() ?? [];

            if (sides.Count > 0 && !sides.Contains(side, StringComparer.OrdinalIgnoreCase))
                continue;

            var args = (po["args"] as JsonArray)?
                .Select(a => a?.GetValue<string>() ?? "")
                .ToList() ?? [];

            for (var i = 0; i < args.Count - 1; i++)
            {
                if (!args[i].Equals("--output", StringComparison.OrdinalIgnoreCase)) continue;

                // 只处理「整条就是一个 {KEY}」的情况——那才是产物路径。
                // 形如 {ROOT}/libraries/ 的复合值交给 Java 工具自己处理目录。
                var spec = args[i + 1].Trim();
                if (!spec.StartsWith('{') || !spec.EndsWith('}')) continue;

                var key = spec[1..^1];
                if (!dataMap.TryGetValue(key, out var dv)) continue;
                if (!dv.IsMaven) continue;

                var path = MavenLocalPath(gameRoot, dv.MavenName!);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            }
        }
    }

    /// <summary>
    /// 解析 outputs：返回 `本地路径 → 期望 sha1`。
    ///
    /// 形态由 <see cref="EnumerateOutputPairs"/> 统一摊平——对象或字符串都吃。
    /// 键与 sha1 都可能是 {KEY} 形式，要经 data 表还原成路径 / 字面量。
    /// </summary>
    private static List<(string Path, string? Sha1)> ParseOutputs(JsonNode outputsNode,
        Dictionary<string, DataValue> dataMap, string gameRoot, string installerPath,
        string minecraftJar, string side, string mcVersion)
    {
        var list = new List<(string, string?)>();

        foreach (var (keyRaw, shaRaw) in EnumerateOutputPairs(outputsNode))
        {
            if (string.IsNullOrWhiteSpace(keyRaw)) continue;

            var pathRaw = Substitute(keyRaw, dataMap, gameRoot, installerPath,
                minecraftJar, side, mcVersion);

            var sha = shaRaw is null
                ? null
                : Substitute(shaRaw, dataMap, gameRoot, installerPath,
                    minecraftJar, side, mcVersion);

            // data 表里的 sha1 写成 "'de86b035…'"——单引号是 Forge 用来区分
            // "这是字面量" 与 "这是 maven 坐标" 的标记，替换后必须剥掉。
            // 不剥的话校验会永远失败，而且报错信息里期望值与实际值看起来一模一样
            // （期望 'abc' vs 实际 abc），极难定位。实测踩过。
            sha = sha?.Trim().Trim('\'').Trim();

            list.Add((pathRaw, sha));
        }

        return list;
    }

    // ═══════════════════════════════════════════════════════════════
    // maven 坐标 → 本地路径
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// `group:artifact:version[:classifier][@ext]` → `libraries/group/path/artifact/version/artifact-version[-classifier].ext`
    ///
    /// 注意 group 里的点要变成斜杠，classifier 拼在 version 之后。
    /// </summary>
    public static string MavenLocalPath(string gameRoot, string mavenName)
        => Path.Combine(gameRoot, "libraries", MavenRelPath(mavenName))
                .Replace('/', Path.DirectorySeparatorChar);

    public static string MavenRelPath(string mavenName)
    {
        var (coords, ext) = SplitExt(mavenName);
        var parts = coords.Split(':');

        var group = parts[0];
        var artifact = parts.Length > 1 ? parts[1] : "";
        var version = parts.Length > 2 ? parts[2] : "";
        var classifier = parts.Length > 3 ? parts[3] : null;

        var file = classifier is null
            ? $"{artifact}-{version}.{ext}"
            : $"{artifact}-{version}-{classifier}.{ext}";

        return $"{group.Replace('.', '/')}/{artifact}/{version}/{file}";
    }

    /// <summary>拆出扩展名。默认 jar；`@txt` / `@zip` 这类显式后缀优先。</summary>
    private static (string Coords, string Ext) SplitExt(string mavenName)
    {
        var at = mavenName.LastIndexOf('@');
        if (at > 0) return (mavenName[..at], mavenName[(at + 1)..]);
        return (mavenName, "jar");
    }

    /// <summary>maven 坐标对应的下载 URL。</summary>
    public static string MavenUrl(string mavenName, string mavenBase)
        => $"{mavenBase.TrimEnd('/')}/{MavenRelPath(mavenName)}";

    // ═══════════════════════════════════════════════════════════════
    // processor 依赖下载
    // ═══════════════════════════════════════════════════════════════

    private sealed class ProcessorDownloader
    {
        private readonly string _librariesRoot;
        private readonly Action<string>? _log;
        private readonly Dictionary<string, string> _paths = new(StringComparer.Ordinal);
        private readonly List<string> _needed = [];

        public ProcessorDownloader(string librariesRoot, Action<string>? log)
        {
            _librariesRoot = librariesRoot;
            _log = log;
        }

        public void Require(string mavenName)
        {
            if (string.IsNullOrWhiteSpace(mavenName)) return;
            if (!_needed.Contains(mavenName, StringComparer.Ordinal))
                _needed.Add(mavenName);
        }

        /// <summary>
        /// 登记一个**不下载**的坐标，只为让 <see cref="PathOf"/> 能算出它的本地路径。
        ///
        /// processor 链里大量键（MAPPINGS / MC_SLIM / PATCHED …）是前序步骤的产物，
        /// maven 上没有对应文件。但它们仍以 maven 坐标形式出现在 data 表里，
        /// 后续 processor 要靠这个路径去读前序产物。
        /// </summary>
        public void RegisterLocalOnly(string label, string mavenName)
        {
            if (string.IsNullOrWhiteSpace(mavenName)) return;
            PathOf(mavenName);   // 只建立映射，不进 _needed
        }

        public string? PathOf(string mavenName)
        {
            if (string.IsNullOrWhiteSpace(mavenName)) return null;
            if (_paths.TryGetValue(mavenName, out var p)) return p;

            var p2 = Path.Combine(_librariesRoot, MavenRelPath(mavenName))
                .Replace('/', Path.DirectorySeparatorChar);
            _paths[mavenName] = p2;
            return p2;
        }

        public async Task DownloadAllAsync(CancellationToken ct)
        {
            var missing = new List<string>();

            foreach (var name in _needed)
            {
                var path = PathOf(name)!;
                if (File.Exists(path) && new FileInfo(path).Length > 0) continue;
                missing.Add(name);
            }

            if (missing.Count == 0) return;

            _log?.Invoke($"  ▸ 下载 {missing.Count} 个 processor 依赖…");

            foreach (var name in missing)
            {
                ct.ThrowIfCancellationRequested();

                var path = PathOf(name)!;
                var url = MavenUrl(name, StripLibrariesFromBase());

                try
                {
                    var bytes = await LoaderHttp.GetBytesAsync(url, ct).ConfigureAwait(false);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
                    _log?.Invoke($"      ✓ {MavenRelPath(name)}  {bytes.Length / 1024} KB");
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        LoaderHttp.Explain(ex, $"下载 processor 依赖 {name}") +
                        Environment.NewLine + $"  URL: {url}", ex);
                }
            }
        }

        /// <summary>processor 依赖都来自 Forge/NeoForge maven，从中反推 base。</summary>
        private static string StripLibrariesFromBase()
            => LoaderHttp.MavenBase;

        /// <summary>installer 字节落盘一次，供 {INSTALLER} 使用。</summary>
        public string SaveInstallerCopy(byte[] bytes)
        {
            var dir = Path.Combine(_librariesRoot, "_processor_inputs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "installer.jar");
            if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
                File.WriteAllBytes(path, bytes);
            return path;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Java 执行
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 从 jar 的 MANIFEST.MF 读 Main-Class。
    /// 不是所有 jar 都能读出来（有的靠 java -jar 的启停器），读不到就报错。
    /// </summary>
    private static async Task<string> ResolveMainClassAsync(string jarPath, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(jarPath);
        var mf = zip.GetEntry("META-INF/MANIFEST.MF");
        if (mf is not null)
        {
            await using var s = mf.Open();
            using var sr = new StreamReader(s);
            var text = await sr.ReadToEndAsync(ct).ConfigureAwait(false);

            // manifest 的续行规则：以单个空格开头的行是上一行的延续
            var unfolded = new StringBuilder();
            foreach (var line in text.Split('\n'))
            {
                var l = line.TrimEnd('\r');
                if (l.StartsWith(' ') && unfolded.Length > 0)
                    unfolded.Append(l[1..]);
                else
                    unfolded.Append('\n').Append(l);
            }

            foreach (var line in unfolded.ToString().Split('\n'))
            {
                if (!line.StartsWith("Main-Class:", StringComparison.OrdinalIgnoreCase)) continue;
                var v = line["Main-Class:".Length..].Trim();
                if (v.Length > 0) return v;
            }
        }

        throw new InvalidOperationException($"无法从 {Path.GetFileName(jarPath)} 的 MANIFEST 里读出 Main-Class");
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> ExecAsync(
        string javaExe, List<string> cpItems, string mainClass, List<string> args,
        string workDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = javaExe,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        psi.ArgumentList.Add("-cp");
        psi.ArgumentList.Add(string.Join(Path.PathSeparator, cpItems));
        psi.ArgumentList.Add(mainClass);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        if (!p.Start())
            throw new InvalidOperationException($"无法启动 Java 进程：{javaExe}");

        var so = p.StandardOutput.ReadToEndAsync(ct);
        var se = p.StandardError.ReadToEndAsync(ct);

        await p.WaitForExitAsync(ct).ConfigureAwait(false);

        return (p.ExitCode, await so.ConfigureAwait(false), await se.ConfigureAwait(false));
    }

    // ═══════════════════════════════════════════════════════════════
    // 小工具
    // ═══════════════════════════════════════════════════════════════

    private static string Sha1OfFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
    }

    private static string Quote(string s)
        => s.Contains(' ') && !s.StartsWith('"') ? $"\"{s}\"" : s;

    private static string ShortJar(string mavenName)
    {
        var parts = mavenName.Split(':');
        return parts.Length >= 2 ? parts[1] : mavenName;
    }

    private static string ShortPath(string p)
    {
        var idx = p.LastIndexOf("libraries", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? p[idx..] : p;
    }

    private static string SizeText(string path)
        => File.Exists(path) ? $"{new FileInfo(path).Length / 1024} KB" : "—";

    private static string Trunc(string s, int n)
        => s.Length <= n ? s : s[..n] + " …";
}