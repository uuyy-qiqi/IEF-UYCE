using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PclLike.Core.Storage;

namespace PclLike.Core.Login;

/// <summary>
/// 账号管理器：持有当前账号，负责持久化。
///
/// ## 两种身份
///
/// - **在线（正版）**：有 Microsoft 档案，uuid 是真实 uuid，进服务器能被验证。
/// - **离线**：只有名字，uuid 由名字派生（OfflinePlayerUuid），只能玩单机
///   和不验证的服务器。
///
/// 启动器里这两者走同一条启动路径，区别只在传给游戏的
/// `--uuid / --accessToken / --userType` 三个参数。
///
/// ## 存储位置与安全
///
/// 存在游戏根目录的 `accounts.json`。里面有 Minecraft accessToken 和
/// Microsoft refreshToken——**这两个都是敏感凭证**。
///
/// 这里做的是**启动器级别的实用方案**：明文存在用户自己的目录里，
/// 权限跟随用户账户。真正的生产启动器会用 DPAPI（Windows 数据保护 API）
/// 加密，但那样换机器/换用户就解不开，对个人使用是负担。
///
/// 注意 `refreshToken` 是长期有效的，泄露等于账号被持续接管。
/// 所以文件里写了注释字段提醒用户别外传。
/// </summary>
public sealed class AccountManager
{
    /// <summary>游戏用的账号身份。</summary>
    public sealed record PlayerProfile(
        string Name,
        string Uuid,
        string AccessToken,
        bool IsOnline,
        string? RefreshToken)
    {
        /// <summary>
        /// 离线档。uuid 沿用 <see cref="ConfigStore.OfflineUuid"/> 的既有规则——
        /// 不要在这里另起一套，否则同一个名字在两处算出不同 uuid，
        /// 存档归属和服务器识别都会错乱。
        /// </summary>
        public static PlayerProfile Offline(string name) => new(
            name,
            ConfigStore.OfflineUuid(name),
            "0",              // 离线模式的 accessToken 惯例是 "0"
            false,
            null);
    }

    private readonly string _file;
    private readonly object _lock = new();

    /// <summary>
    /// 账号文件放在游戏根目录下，和 LauncherConfig 同级。
    ///
    /// gameRoot 必须显式给出：根目录的默认值规则属于宿主层
    /// （GUI 的 <c>App.GameRoot</c> 支持命令行/环境变量覆盖），
    /// Core 在这里自己猜一个 %APPDATA% 路径会导致
    /// "GUI 读 A 目录、账号写 B 目录"这类难查的不一致。
    /// </summary>
    public AccountManager(string gameRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        Directory.CreateDirectory(gameRoot);
        _file = Path.Combine(gameRoot, "accounts.json");
        Load();
    }

    public PlayerProfile? Profile { get; private set; }

    /// <summary>离线模式下上次用的名字，方便对话框预填。</summary>
    public string OfflineName { get; private set; } = "Player";

    // ---------------------------------------------------------------

    public void SetOffline(string name)
    {
        lock (_lock)
        {
            Profile = PlayerProfile.Offline(name);
            OfflineName = name;
            Save();
        }
    }

    public void SetOnlineProfile(string uuid, string name, string accessToken, string? refreshToken)
    {
        lock (_lock)
        {
            // 位置参数在这里是个陷阱：MicrosoftAuth.McProfile 的声明顺序是
            // (Uuid, Name, ...)，而 PlayerProfile 是 (Name, Uuid, ...)。照抄
            // 另一个类型的顺序写一次，就会把 uuid 当名字存进账号文件 —— 登录
            // 看着成功，实际是个非法 uuid，游戏起来就是离线。所以两个调用点
            // 一律用具名参数，让编译器替我们挡住顺序问题。
            Profile = new PlayerProfile(
                Name: name, Uuid: uuid, AccessToken: accessToken,
                IsOnline: true, RefreshToken: refreshToken);
            Save();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Profile = null;
            Save();
        }
    }

    /// <summary>
    /// 尝试用 refreshToken 续期。成功返回新的 accessToken。
    ///
    /// 用在启动时：如果上次是正版登录，先自动续期，用户无感。
    /// 续期失败（token 过期/被吊销）就退回离线，而不是报错挡住启动。
    /// </summary>
    public async Task<bool> TryRefreshAsync(string? clientId = null, CancellationToken ct = default)
    {
        PlayerProfile? p;
        lock (_lock) p = Profile;

        if (p is null || !p.IsOnline || string.IsNullOrWhiteSpace(p.RefreshToken))
            return false;

        try
        {
            var auth = new MicrosoftAuth(clientId);
            if (!auth.IsConfigured) return false;

            var ms = await auth.RefreshAsync(p.RefreshToken!, ct).ConfigureAwait(false);
            var profile = await auth.CompleteLoginAsync(ms, null, ct).ConfigureAwait(false);

            lock (_lock)
            {
                Profile = new PlayerProfile(
                    Name: profile.Name, Uuid: profile.Uuid, AccessToken: profile.AccessToken,
                    IsOnline: true, RefreshToken: ms.RefreshToken);
                Save();
            }
            return true;
        }
        catch
        {
            // 续期失败不抛，交给调用方决定退离线还是提示
            return false;
        }
    }

    // ---------------------------------------------------------------
    // 持久化
    // ---------------------------------------------------------------

    private sealed class Store
    {
        [JsonPropertyName("_comment")]
        public string Comment { get; set; } =
            "此文件包含登录凭证（accessToken / refreshToken），请勿分享给他人。";

        [JsonPropertyName("offlineName")] public string? OfflineName { get; set; }
        [JsonPropertyName("profile")] public PlayerProfile? Profile { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var store = JsonSerializer.Deserialize<Store>(File.ReadAllText(_file), Json);
            if (store is null) return;

            Profile = store.Profile;
            if (!string.IsNullOrWhiteSpace(store.OfflineName))
                OfflineName = store.OfflineName!;
        }
        catch
        {
            // 配置损坏就当没登录，不要因为读不了账号文件就起不来
        }
    }

    private void Save()
    {
        try
        {
            var store = new Store { OfflineName = OfflineName, Profile = Profile };
            File.WriteAllText(_file, JsonSerializer.Serialize(store, Json));
        }
        catch { }
    }
}