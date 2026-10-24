using System.Runtime.InteropServices;

namespace PclLike.Core.Minecraft;

/// <summary>
/// 规则（rules）求值。决定一个库或一条参数是否适用于当前平台。
///
/// 语义（与原版 launcher 一致）：
///   - 没有 rules → 默认允许
///   - 有 rules → 初始不允许，按顺序求值，最后一条匹配的规则决定结果
///   - os.name: "windows" / "osx" / "linux"
///   - os.arch: "x86" / "x86_64" / "arm64"
///   - features: 由启动器提供的特性开关（如 is_demo_user、has_custom_resolution）
/// </summary>
public static class RuleEvaluator
{
    public static string CurrentOsName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "linux";

    public static string CurrentOsArch => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X86 => "x86",
        Architecture.X64 => "x86_64",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        _ => "unknown",
    };

    /// <summary>features 默认全 false；调用方可传入 is_demo_user 等。</summary>
    public static bool IsAllowed(List<Rule>? rules, IReadOnlyDictionary<string, bool>? features = null)
    {
        if (rules is null || rules.Count == 0) return true;

        var allowed = false;
        foreach (var rule in rules)
        {
            if (!Matches(rule, features)) continue;
            allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }
        return allowed;
    }

    private static bool Matches(Rule rule, IReadOnlyDictionary<string, bool>? features)
    {
        if (rule.Os is not null)
        {
            if (rule.Os.Name is not null &&
                !string.Equals(rule.Os.Name, CurrentOsName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (rule.Os.Arch is not null &&
                !string.Equals(rule.Os.Arch, CurrentOsArch, StringComparison.OrdinalIgnoreCase))
                return false;

            // os.version 是正则，实际很少用，这里做保守处理
            if (rule.Os.Version is not null)
            {
                if (OperatingSystem.IsWindows() && !Environment.OSVersion.Version.ToString().Contains(rule.Os.Version))
                    return false;
            }
        }

        if (rule.Features is not null)
        {
            foreach (var (key, expected) in rule.Features)
            {
                var actual = features?.TryGetValue(key, out var v) == true && v;
                if (actual != expected) return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 从 natives 字典里选出当前平台对应的 classifier 名。
    /// 例如 {"windows":"natives-windows","osx":"natives-macos","linux":"natives-linux"}。
    /// 注意 1.19+ 的 natives-macos 有 -arm64 变体。
    /// </summary>
    public static string? SelectNativeClassifier(Dictionary<string, string> natives)
    {
        if (!natives.TryGetValue(CurrentOsName, out var raw)) return null;

        // 处理 ${arch} 占位符：32 位系统要把 arm64 换成 32
        var archToken = RuntimeInformation.OSArchitecture == Architecture.X64 ? "64" : "32";
        return raw.Replace("${arch}", archToken);
    }
}