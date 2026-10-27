using System.Text;
using System.Text.Json.Nodes;

namespace PclLike.Core.Minecraft.Launch;

public sealed class LaunchConfig
{
    public required string VersionId { get; init; }
    public required string GameDirectory { get; init; }
    public required string AssetsRoot { get; init; }
    public required string AssetIndexId { get; init; }
    public required string NativesDirectory { get; init; }
    public required string Classpath { get; init; }
    public required string LibrariesRoot { get; init; }
    public required string JavaExecutable { get; init; }

    public string Username { get; init; } = "Player";
    public string Uuid { get; init; } = "00000000000000000000000000000000";
    public string AccessToken { get; init; } = "0";
    public string UserType { get; init; } = "legacy";

    /// <summary>
    /// 游戏参数里的 `--clientId`。官方版本 JSON 写作 `${clientid}`，
    /// 正版（MSA）必须是启动器自己的应用 ID。
    /// </summary>
    public string ClientId { get; init; } = PclLike.Core.Login.MicrosoftAuth.DefaultClientId;

    /// <summary>
    /// 账号的 XUID，官方版本 JSON 写作 `${auth_xuid}`。
    /// 拿不到真实的就填 `0` —— 不能填空串，见 TokenTable 里的说明。
    /// </summary>
    public string Xuid { get; init; } = "0";
    public string LauncherName { get; init; } = "PclLike";
    public string LauncherVersion { get; init; } = "0.1.0";
    public string VersionType { get; init; } = "release";

    public int MaxMemoryMb { get; init; } = 2048;
    public int MinMemoryMb { get; init; } = 512;

    public string? ServerAddress { get; init; }
    public int? ServerPort { get; init; }

    /// <summary>额外 JVM 参数，附加在标准参数之后。</summary>
    public IReadOnlyList<string> ExtraJvmArgs { get; init; } = [];

    /// <summary>额外游戏参数。</summary>
    public IReadOnlyList<string> ExtraGameArgs { get; init; } = [];
}

/// <summary>
/// 启动参数构建。
///
/// 两种格式：
///   1. 旧格式（1.12 及以前）：minecraftArguments 是一整个字符串，只需替换占位符
///   2. 新格式（1.13+）：arguments.game / arguments.jvm 是数组，元素可为
///      字符串或 {"rules":[...],"value":"..."|["..."]} 条件对象
///
/// 顺序：JVM 参数（含 -cp）→ mainClass → 游戏参数。
/// Windows 上 classpath 用 ; 分隔，其他平台用 :。
/// </summary>
public static class ArgumentBuilder
{
    public static List<string> BuildJvmArgs(VersionJson version, LaunchConfig cfg)
    {
        var result = new List<string>
        {
            $"-Xmx{cfg.MaxMemoryMb}M",
            $"-Xms{cfg.MinMemoryMb}M",
        };

        if (OperatingSystem.IsWindows())
        {
            // 官方 launcher 会加这几条，避免中文路径/编码问题
            result.Add("-Dfile.encoding=UTF-8");
            result.Add("-Dstdout.encoding=UTF-8");
            result.Add("-Dstderr.encoding=UTF-8");
        }

        result.Add($"-Djava.library.path={cfg.NativesDirectory}");
        result.Add($"-Dminecraft.launcher.brand={cfg.LauncherName}");
        result.Add($"-Dminecraft.launcher.version={cfg.LauncherVersion}");

        // 新格式的 jvm 参数（含 ${launcher_name} 等占位符与条件规则）
        if (version.Arguments?.Jvm is { Count: > 0 } jvm)
        {
            var tokens = TokenTable(cfg);
            var skipNext = false;
            foreach (var a in ExpandArgumentList(jvm, tokens, features: null))
            {
                // 版本 JSON 自己带 -cp / -classpath：跳过，classpath 由我们统一加。
                // 否则会出现两个 -cp，后面的（展开后可能为空或重复）会覆盖我们的，
                // 表现为 "找不到或无法加载主类"。
                if (skipNext) { skipNext = false; continue; }
                if (a is "-cp" or "-classpath") { skipNext = true; continue; }

                result.Add(a);
            }
        }

        result.AddRange(cfg.ExtraJvmArgs);

        // -cp 必须放在所有 -D 之后、mainClass 之前
        result.Add("-cp");
        result.Add(cfg.Classpath);

        return result;
    }

    public static List<string> BuildGameArgs(VersionJson version, LaunchConfig cfg)
    {
        var tokens = TokenTable(cfg);
        var features = new Dictionary<string, bool>
        {
            ["is_demo_user"] = false,
            ["has_custom_resolution"] = false,
            ["has_quick_plays_support"] = false,
            ["is_quick_play_singleplayer"] = false,
            ["is_quick_play_multiplayer"] = false,
            ["is_quick_play_realms"] = false,
        };

        var result = new List<string>();

        if (version.Arguments?.Game is { Count: > 0 } game)
        {
            result.AddRange(ExpandArgumentList(game, tokens, features));
        }
        else if (!string.IsNullOrEmpty(version.MinecraftArguments))
        {
            // 旧格式：整串按空格切分后替换占位符
            result.AddRange(SplitLegacyArguments(version.MinecraftArguments, tokens));
        }

        result.AddRange(cfg.ExtraGameArgs);

        // 直连服务器：官方参数里若没有 --server/--port 就补上
        if (!string.IsNullOrEmpty(cfg.ServerAddress) &&
            !result.Contains("--server"))
        {
            result.Add("--server");
            result.Add(cfg.ServerAddress);
            if (cfg.ServerPort is { } port)
            {
                result.Add("--port");
                result.Add(port.ToString());
            }
        }

        return result;
    }

    /// <summary>占位符 → 实际值。键含 ${}，替换时统一处理。</summary>
    private static Dictionary<string, string> TokenTable(LaunchConfig cfg) => new(StringComparer.Ordinal)
    {
        ["${auth_player_name}"] = cfg.Username,
        ["${auth_uuid}"] = cfg.Uuid,
        ["${auth_access_token}"] = cfg.AccessToken,
        ["${auth_session}"] = cfg.AccessToken,
        ["${user_type}"] = cfg.UserType,
        // 官方 1.20.1 的版本 JSON 里有 `--clientId ${clientid} --xuid ${auth_xuid}`。
        // 以前这两个不在表里，Substitute 对未知占位符是"保留原样"，
        // 于是字面量 `${clientid}` 直接进了命令行（启动日志里就能看到）。
        // 值也不能是空串：空串会被 ExpandArgumentList 丢掉（`if (v.Length > 0)`），
        // 结果 `--clientId` 后面紧跟着 `--xuid`，参数整体错位。
        ["${clientid}"] = cfg.ClientId,
        ["${auth_xuid}"] = cfg.Xuid,
        ["${version_name}"] = cfg.VersionId,
        ["${version_type}"] = cfg.VersionType,
        ["${game_directory}"] = cfg.GameDirectory,
        ["${assets_root}"] = cfg.AssetsRoot,
        ["${game_assets}"] = cfg.AssetsRoot,     // 旧版用这个名字
        ["${assets_index_name}"] = cfg.AssetIndexId,
        ["${natives_directory}"] = cfg.NativesDirectory,
        ["${launcher_name}"] = cfg.LauncherName,
        ["${launcher_version}"] = cfg.LauncherVersion,
        ["${classpath}"] = cfg.Classpath,
        // NeoForge / Forge 的 bootstrap 参数依赖这两个键：
        //   ${library_directory} 用于 -p 模块路径及各 -Dfml.* 属性，
        //   ${classpath_separator} 用于在 -p 里拼接多个 jar。
        // 漏掉它们会让 -p 变成字面量假路径，Java 判定模块不存在，
        // 于是同批 --add-opens/--add-exports 被静默忽略，
        // 最终在 UnionFileSystem.<clinit> 抛 InaccessibleObjectException。
        ["${library_directory}"] = cfg.LibrariesRoot,
        ["${classpath_separator}"] = OperatingSystem.IsWindows() ? ";" : ":",
        ["${resolution_width}"] = "854",
        ["${resolution_height}"] = "480",
        ["${user_properties}"] = "{}",
        ["${quickPlayPath}"] = "",
    };

    private static IEnumerable<string> ExpandArgumentList(
        List<object> list,
        IReadOnlyDictionary<string, string> tokens,
        IReadOnlyDictionary<string, bool>? features)
    {
        foreach (var item in list)
        {
            switch (item)
            {
                case string s:
                {
                    var v = Substitute(s, tokens);
                    if (v.Length > 0) yield return v;
                    break;
                }

                case JsonNode node when node is JsonObject obj:
                {
                    var v = ExpandObject(obj, tokens, features);
                    if (v.Length > 0) yield return v;
                    break;
                }

                case System.Text.Json.JsonElement el when el.ValueKind == System.Text.Json.JsonValueKind.String:
                {
                    var v = Substitute(el.GetString()!, tokens);
                    if (v.Length > 0) yield return v;
                    break;
                }

                case System.Text.Json.JsonElement el when el.ValueKind == System.Text.Json.JsonValueKind.Object:
                {
                    var v = ExpandJsonElement(el, tokens, features);
                    if (v.Length > 0) yield return v;
                    break;
                }
            }
        }
    }

    /// <summary>展开 {"rules":[...],"value":"x" 或 ["x","y"]}。注意 value 可能展开成空串，需要过滤。</summary>
    private static string ExpandObject(JsonObject obj, IReadOnlyDictionary<string, string> tokens,
        IReadOnlyDictionary<string, bool>? features)
    {
        var rules = ParseRules(obj["rules"]);
        if (!RuleEvaluator.IsAllowed(rules, features)) return "";

        var value = obj["value"];
        if (value is null) return "";

        if (value is JsonArray arr)
        {
            // 数组形式的 value：可能含条件元素，递归展开后拼接
            var parts = new List<string>();
            foreach (var v in arr)
            {
                if (v is JsonObject inner)
                {
                    var s = ExpandObject(inner, tokens, features);
                    if (!string.IsNullOrEmpty(s)) parts.Add(s);
                }
                else
                {
                    var s = Substitute(v?.GetValue<string>() ?? "", tokens);
                    if (!string.IsNullOrEmpty(s)) parts.Add(s);
                }
            }
            return string.Join(" ", parts);
        }

        return Substitute(value.GetValue<string>(), tokens);
    }

    private static string ExpandJsonElement(System.Text.Json.JsonElement el,
        IReadOnlyDictionary<string, string> tokens,
        IReadOnlyDictionary<string, bool>? features)
    {
        var node = JsonNode.Parse(el.GetRawText());
        return node is JsonObject o ? ExpandObject(o, tokens, features) : "";
    }

    private static List<Rule>? ParseRules(JsonNode? node)
    {
        if (node is not JsonArray arr) return null;
        var rules = new List<Rule>();
        foreach (var item in arr)
        {
            var r = System.Text.Json.JsonSerializer.Deserialize<Rule>(item!.ToJsonString(),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (r is not null) rules.Add(r);
        }
        return rules;
    }

    /// <summary>替换所有 ${xxx} 占位符。未知占位符保留原样便于发现问题。</summary>
    public static string Substitute(string input, IReadOnlyDictionary<string, string> tokens)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains("${", StringComparison.Ordinal))
            return input;

        var sb = new StringBuilder(input.Length + 32);
        var i = 0;
        while (i < input.Length)
        {
            if (input[i] == '$' && i + 1 < input.Length && input[i + 1] == '{')
            {
                var end = input.IndexOf('}', i + 2);
                if (end > 0)
                {
                    var key = input[i..(end + 1)];
                    if (tokens.TryGetValue(key, out var val))
                    {
                        sb.Append(val);
                        i = end + 1;
                        continue;
                    }
                }
            }
            sb.Append(input[i]);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>旧格式 minecraftArguments 按空格切分（支持引号包裹）。</summary>
    private static IEnumerable<string> SplitLegacyArguments(string raw, IReadOnlyDictionary<string, string> tokens)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in raw)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) parts.Add(current.ToString());

        foreach (var p in parts)
        {
            var s = Substitute(p, tokens);
            if (!string.IsNullOrEmpty(s)) yield return s;
        }
    }

    /// <summary>classpath 分隔符。</summary>
    public static char ClasspathSeparator => OperatingSystem.IsWindows() ? ';' : ':';
}