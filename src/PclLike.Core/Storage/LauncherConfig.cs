using System.Text.Json;
using System.Text.Json.Serialization;

namespace PclLike.Core.Storage;

/// <summary>
/// 启动器设置：账号、内存、Java 路径等。
///
/// 存放在 &lt;root&gt;/launcher_config.json，和 Minecraft 本体文件放在一起，
/// 这样"换个 .minecraft 目录 = 换一套设置"，符合便携启动器的习惯。
///
/// 故意不做加密：离线模式下的 username 本来就只是本地标识，
/// 真正的 accessToken 现在没用上（离线启动），等接入正版登录再谈密钥保护。
/// </summary>
public sealed class LauncherConfig
{
    /// <summary>上次选择的版本 id。</summary>
    [JsonPropertyName("lastVersion")] public string? LastVersion { get; set; }

    [JsonPropertyName("username")] public string Username { get; set; } = "Player";

    /// <summary>离线模式的 UUID（对用户名做 md5 得到，Minecraft 认这个）。</summary>
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";

    [JsonPropertyName("maxMemoryMb")] public int MaxMemoryMb { get; set; } = 4096;

    [JsonPropertyName("minMemoryMb")] public int MinMemoryMb { get; set; } = 512;

    /// <summary>手动指定的 java.exe；空表示自动探测。</summary>
    [JsonPropertyName("javaPath")] public string? JavaPath { get; set; }

    /// <summary>额外的 JVM 参数，一行一个（界面上是文本框）。</summary>
    [JsonPropertyName("jvmArgs")] public string? JvmArgs { get; set; }

    /// <summary>启动后自动直连的服务器 host:port。</summary>
    [JsonPropertyName("server")] public string? Server { get; set; }

    /// <summary>是否在启动前自动补全缺失文件。</summary>
    [JsonPropertyName("autoInstall")] public bool AutoInstall { get; set; } = true;

    /// <summary>窗口关闭时是否同时结束游戏进程。</summary>
    [JsonPropertyName("killGameOnExit")] public bool KillGameOnExit { get; set; }

    /// <summary>
    /// 正版登录用的 Microsoft 应用（客户端）ID。
    ///
    /// 必须由用户自己注册 Azure 应用后填入——公共 client_id 不可用，
    /// 原因见 <see cref="Login.MicrosoftAuth"/> 的说明。
    /// 留空时离线模式照常工作，只有点"Microsoft 登录"才会提示去注册。
    /// </summary>
    [JsonPropertyName("microsoftClientId")] public string? MicrosoftClientId { get; set; }

    // ─────────────────────── 设置页里的各项 ───────────────────────

    /// <summary>启动后默认停在哪个大页签：launch / download / settings / tools。</summary>
    [JsonPropertyName("defaultTab")] public string DefaultTab { get; set; } = "launch";

    /// <summary>启动时自动展开底部的日志抽屉。</summary>
    [JsonPropertyName("autoExpandLog")] public bool AutoExpandLog { get; set; }

    /// <summary>游戏启动后把启动器窗口收起来（最小化）。</summary>
    [JsonPropertyName("hideOnLaunch")] public bool HideOnLaunch { get; set; }

    /// <summary>默认是否按实例隔离版本目录。</summary>
    [JsonPropertyName("isolateInstances")] public bool IsolateInstances { get; set; } = true;

    /// <summary>游戏进程优先级：high / normal / low。</summary>
    [JsonPropertyName("processPriority")] public string ProcessPriority { get; set; } = "high";

    /// <summary>窗口大小：default / maximized。</summary>
    [JsonPropertyName("windowSize")] public string WindowSize { get; set; } = "default";

    /// <summary>正版登录方式：devicecode（设备代码流）/ browser（浏览器跳转）。</summary>
    [JsonPropertyName("authMode")] public string AuthMode { get; set; } = "devicecode";

    /// <summary>
    /// 代理怎么找：auto（跟着环境变量/系统设置，系统没开就探本机常见端口）/
    /// manual（用 <see cref="ProxyUrl"/>）/ off（永远直连）。
    /// </summary>
    [JsonPropertyName("proxyMode")] public string ProxyMode { get; set; } = "auto";

    /// <summary>手动模式下的代理地址，例如 http://127.0.0.1:7897。</summary>
    [JsonPropertyName("proxyUrl")] public string? ProxyUrl { get; set; }

    [JsonIgnore]
    public IReadOnlyList<string> ExtraJvmArgs =>
        string.IsNullOrWhiteSpace(JvmArgs)
            ? []
            : JvmArgs.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>读写 <see cref="LauncherConfig"/>。</summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string PathFor(string gameRoot) => System.IO.Path.Combine(gameRoot, "launcher_config.json");

    /// <summary>读取配置；文件不存在或损坏时返回默认值，绝不抛。</summary>
    public static LauncherConfig Load(string gameRoot)
    {
        try
        {
            var file = PathFor(gameRoot);
            if (!File.Exists(file)) return new LauncherConfig();

            return JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(file), Opts)
                   ?? new LauncherConfig();
        }
        catch
        {
            // 配置坏了不该让启动器起不来，退回默认值
            return new LauncherConfig();
        }
    }

    /// <summary>写回配置。</summary>
    public static void Save(string gameRoot, LauncherConfig config)
    {
        Directory.CreateDirectory(gameRoot);
        var file = PathFor(gameRoot);

        // 先写临时文件再替换，避免写到一半断电留下半个 JSON
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, Opts));
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>
    /// 由用户名推导离线 UUID。
    ///
    /// Minecraft 离线模式的 UUID 固定是 version 3 (MD5) 的 name-based UUID：
    /// 把 "OfflinePlayer:" + 用户名 用 MD5 哈希，再按 UUID v3 的格式塞进去。
    /// 自己随便编一个 UUID 也能启动，但多人服务器会因为 UUID 不一致
    /// 把同一个人的存档/权限认成两个人。
    /// </summary>
    public static string OfflineUuid(string username)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + username));

        // 按 RFC 4122 设置 version(3) 与 variant 位
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }

    /// <summary>把任意输入规范成合法的玩家名（Minecraft 只接受 3~16 位 [A-Za-z0-9_]）。</summary>
    public static string SanitizeUsername(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Player";

        var cleaned = new string(raw.Trim().Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray());
        if (cleaned.Length < 3) cleaned = "Player";
        return cleaned.Length > 16 ? cleaned[..16] : cleaned;
    }
}