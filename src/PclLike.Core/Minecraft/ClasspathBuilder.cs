using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace PclLike.Core.Minecraft;

/// <summary>
/// 从已解析（已合并 inheritsFrom）的版本 JSON 里算出实际需要落地的文件列表。
///
/// 关键点：
///   - 库要过 rules（当前平台不允许的直接跳过）
///   - 带 natives 的库：不把 jar 放进 classpath，而是下载后解压到 natives 目录
///     （解压时排除 extract.exclude 里的项，通常是 META-INF/）
///   - 1.19+ 的 natives 库已经把 classifier 直接写进 name 里（如 xxx:natives-windows），
///     同时 downloads.artifact 就是答案，不需要再查 classifiers
///   - 客户端 jar 从 downloads.client 拿
/// </summary>
public static class ClasspathBuilder
{
    public sealed record LibraryPlan(
        Library Library,
        Artifact? Jar,              // 需要进 classpath 的产物
        Artifact? NativeJar,        // 需要解压到 natives 的产物
        string ExtractExcludePrefix,
        bool NativeJarAlsoOnClasspath = false);

    /// <summary>
    /// 判断 1.19+ 那种 "group:artifact:version:natives-<os>[-<arch>]" 的 classifier
    /// 是否匹配当前架构。
    ///
    /// 版本 JSON 里这些条目的 rules 只写了 allow:windows，没有区分 arch——
    /// 三种架构（natives-windows / -arm64 / -x86）全都会通过规则。
    /// 所以必须在 classifier 名字上再筛一层，否则会多下两个没用的包，
    /// 更糟的是把错误的 .dll 解压进 natives 目录覆盖正确的那个，
    /// 表现为 UnsatisfiedLinkError 或进程直接崩。
    /// </summary>
    private static bool MatchesCurrentArch(string? name)
    {
        if (name is null) return true;

        var cls = name.Split(':').LastOrDefault();
        if (cls is null || !cls.StartsWith("natives-", StringComparison.Ordinal)) return true;

        var isArm64 = cls.EndsWith("-arm64", StringComparison.Ordinal);
        var isX86 = cls.EndsWith("-x86", StringComparison.Ordinal);

        return RuntimeInformation.OSArchitecture switch
        {
            // x64 用不带 arch 后缀的那个（就是 x86_64 版本）
            Architecture.X64 when RuntimeInformation.IsOSPlatform(OSPlatform.Windows) => !isArm64 && !isX86,
            Architecture.X64 when RuntimeInformation.IsOSPlatform(OSPlatform.OSX) => isArm64,   // macOS 的 x64 版在 natives-macos
            Architecture.Arm64 => isArm64 || (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && !isX86),
            Architecture.X86 => isX86,
            _ => !isArm64 && !isX86,
        };
    }

    /// <summary>把一个库条目解析成落地计划。返回 null 表示当前平台不需要这个库。</summary>
    public static LibraryPlan? Plan(Library lib, IReadOnlyDictionary<string, bool>? features = null)
    {
        if (!RuleEvaluator.IsAllowed(lib.Rules, features)) return null;
        if (!MatchesCurrentArch(lib.Name)) return null;

        var downloads = lib.Downloads;

        // 情形 A：有 natives 声明（1.18 及以前的写法）
        if (lib.Natives is { Count: > 0 })
        {
            var classifier = RuleEvaluator.SelectNativeClassifier(lib.Natives);
            if (classifier is null) return null;

            Artifact? native = null;
            if (downloads?.Classifiers is not null && downloads.Classifiers.TryGetValue(classifier, out var a))
                native = a;

            // 分类器条目经常只给 url/sha1/size，没有 path。这种时候绝不能退回
            // 「主 jar 的路径」当落点：那会把 natives 的内容写进 <artifact>-<version>.jar，
            // 直接把类库 jar 覆盖掉（sha1 又对不上，于是反复重下、越修越坏）。
            //
            // 实测（Cleanroom 0.7.12-alpha + LWJGL 3.4.3，2026-10-25）：
            // org.lwjgl 七个模块的类库 jar 全被各自的 natives-windows jar 覆盖，
            // 游戏里表现为 org.lwjgl.Version 与 org.lwjgl.sdl.SDL_LogOutputFunctionI
            // 这类「自家依赖 ClassNotFound」的怪错 —— 实际是类库 jar 里压根没有类。
            if (native is not null && string.IsNullOrEmpty(native.Path))
            {
                native = new Artifact
                {
                    Path = lib.ToMavenPath().Replace(".jar", $"-{classifier}.jar"),
                    Url = native.Url,
                    Sha1 = native.Sha1,
                    Size = native.Size,
                };
            }

            // 有些整合包只给 url 模板，这里退化成由 Maven 坐标推导
            if (native is null && !string.IsNullOrEmpty(lib.Url))
            {
                native = new Artifact
                {
                    Path = lib.ToMavenPath().Replace(".jar", $"-{classifier}.jar"),
                    Url = lib.Url,
                };
            }

            // 主 jar 也要进 classpath —— 这一条以前是漏的。
            //
            // 老写法里 natives 常常和主 jar 分开声明（1.12.2 的 LWJGL2 就是），
            // 但也有把两者写进同一条的（Cleanroom 0.7 的 LWJGL 3.4.3 全都是），
            // 那种情况下只解压 natives、主 jar 永远进不了 classpath，
            // 表现为 org.lwjgl.sdl.SDL_LogOutputFunctionI 这种「明明是自家依赖
            // 却 ClassNotFound」的怪错（实际是根本没上 -cp）。
            var main = downloads?.Artifact;
            if (main is null && !string.IsNullOrEmpty(lib.Url))
            {
                var mainPath = lib.ToMavenPath();
                main = new Artifact
                {
                    Path = mainPath,
                    Url = lib.Url.TrimEnd('/') + "/" + mainPath,
                };
            }

            // natives jar 也必须进 classpath —— 老写法（natives{} + downloads.classifiers）
            // 的 natives jar 以前只被解压，从不上 -cp，于是 LWJGL 3.x 的
            // org.lwjgl.system.Library.<clinit> 找不到自己的核心原生库：
            //   UnsatisfiedLinkError: Failed to locate library: lwjgl.dll
            //   at org.lwjgl.system.Library.loadSystem(Library.java:177)
            // 实测（Cleanroom 0.7.12-alpha / LWJGL 3.4.3，2026-10-25）：LWJGL 3.4 根本不看
            // java.library.path，只认「classpath 上的 natives jar」（它自己抽 dll 到临时目录）。
            // 而我们的扁平解压还有个副作用：LWJGL2 的 lwjgl.dll（306176 字节）和 LWJGL3 的
            // lwjgl.dll（531456 字节）同名，解压到 natives 目录里只会剩下其中一个。
            // 所以这条路必须走 classpath，不能靠解压。
            return new LibraryPlan(lib, Jar: main, NativeJar: native,
                ExtractExcludePrefix: "META-INF/", NativeJarAlsoOnClasspath: true);
        }

        // 情形 B：普通库，或者 1.19+ 把 natives 写成独立 name 的库
        var artifact = downloads?.Artifact;

        if (artifact is null && !string.IsNullOrEmpty(lib.Url))
        {
            // 整合包常见：只给 url 前缀，路径由坐标推
            var path = lib.ToMavenPath();
            artifact = new Artifact
            {
                Path = path,
                Url = lib.Url.TrimEnd('/') + "/" + path,
            };
        }

        if (artifact is null) return null;

        // 1.19+ 的 natives 库：名字里带 natives-xxx 的。
        //
        // 注意：这类 jar 必须【同时】进 classpath —— LWJGL 3.x 的 Library 类
        // 会自己从 classpath 上的 natives jar 里抽 .dll 到临时目录再 load。
        // 只把 dll 解压到 java.library.path 是不够的（1.19+ 走的不是这条路径），
        // 表现为 java.lang.UnsatisfiedLinkError: Failed to locate library: lwjgl.dll
        //
        // 同时我们仍然解压一份到 natives 目录：两条路都留着最稳，
        // 而且 lwjgl 的 extract 路径本身也依赖 nativies 目录存在。
        var isNativesArtifact = lib.Name is not null && lib.Name.Contains(":natives-", StringComparison.Ordinal);
        return isNativesArtifact
            ? new LibraryPlan(lib, Jar: artifact, NativeJar: artifact,
                ExtractExcludePrefix: "META-INF/", NativeJarAlsoOnClasspath: true)
            : new LibraryPlan(lib, Jar: artifact, NativeJar: null, ExtractExcludePrefix: "META-INF/");
    }

    /// <summary>把库列表解析成计划列表。</summary>
    public static List<LibraryPlan> PlanAll(IEnumerable<Library> libraries,
        IReadOnlyDictionary<string, bool>? features = null)
    {
        var result = new List<LibraryPlan>();

        // 1.12.2 + Cleanroom 会把 LWJGL 2.9.4 和 LWJGL 3.4.3 同时摆进 classpath，
        // 而两者都带 org.lwjgl.PointerBuffer / org.lwjgl.BufferUtils 这类同名类 ——
        // classpath 里谁在前谁赢，和 JSON 里怎么写的无关。
        //
        // 按 JSON 原顺序（LWJGL 2 在前）实测崩在 SDL 初始化：
        //   java.lang.VerifyError: Bad type on operand stack
        //   …'org/lwjgl/PointerBuffer' is not assignable to 'org/lwjgl/system/CustomBuffer'
        //   at org.lwjgl.sdl.SDL_LogOutputFunctionI.<clinit>
        // 原因是 LWJGL 3 内部的 LibFFI 拿到的是 LWJGL 2 的 PointerBuffer（不是 CustomBuffer）。
        // Cleanroom 的 lwjgly 本来就是“把 LWJGL2 调用改写成 LWJGL3”的垫片，所以 LWJGL 3 必须在前。
        // OrderBy 是稳定排序，组内仍保持 JSON 顺序。
        var ordered = libraries.OrderBy(l => LwjglRank(l.Name)).ToList();

        foreach (var lib in ordered)
        {
            var plan = Plan(lib, features);
            if (plan is not null) result.Add(plan);
        }
        return result;
    }

    /// <summary>
    /// classpath 排序等级：LWJGL 3（org.lwjgl:）→ 其它 → LWJGL 2（org.lwjgl.lwjgl:）。
    /// 两者有同名类时 LWJGL 3 必须赢，理由见 PlanAll 上的注释。
    /// </summary>
    private static int LwjglRank(string? name)
    {
        if (name is null) return 1;
        if (name.StartsWith("org.lwjgl.lwjgl:", StringComparison.OrdinalIgnoreCase)) return 2; // LWJGL 2
        if (name.StartsWith("org.lwjgl:", StringComparison.OrdinalIgnoreCase)) return 0;       // LWJGL 3
        return 1;
    }

    /// <summary>组装 classpath 字符串（不含客户端 jar，调用方自行追加）。</summary>
    public static string JoinClasspath(IEnumerable<string> jarPaths)
        => string.Join(Launch.ArgumentBuilder.ClasspathSeparator, jarPaths);
}