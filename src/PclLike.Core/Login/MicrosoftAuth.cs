using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PclLike.Core.Login;

/// <summary>
/// Microsoft 账号登录（正版）。
///
/// ## 完整链路
///
/// 一次成功的正版登录要跨五个服务：
///
/// ```
/// 1. MS OAuth      devicecode → 用户去 microsoft.com/link 输码 → token
/// 2. Xbox Live     MS access_token → XBL token + userhash
/// 3. XSTS          XBL token → XSTS token（授权给 Minecraft 这个 relying party）
/// 4. MC 登录       XSTS token → Minecraft access_token
/// 5. MC 档案       access_token → uuid + 用户名 + 皮肤
/// ```
///
/// 任何一步都可能失败，且失败含义完全不同：
///   - 第 2 步报错 = 这个 MS 账号没注册 Xbox Live
///   - 第 3 步报 XErr 2148916233 = 没有 Xbox 账号
///   - 第 3 步报 XErr 2148916238 = 未成年账号，需要家长同意
///   - 第 4 步 401 = 这个账号没买 Minecraft
///
/// 所以每一步的错误都要翻译成人话，否则用户只看到 401 完全不知道怎么办。
///
/// ## 为什么用 device code 而不是嵌浏览器
///
/// device code 流程下用户在**系统浏览器**里输码登录，启动器全程不接触密码。
/// 嵌 WebView 要处理证书、CSP、代理，还得自己保管密码输入框——
/// 又麻烦又危险。device code 的代价只是用户要多输一个 8 位码。
/// </summary>
public sealed class MicrosoftAuth
{
    /// <summary>
    /// 客户端的 client_id。
    ///
    /// ## 为什么必须自己注册
    ///
    /// 这里不能硬编码任何"公共"client_id。查证过的事实：
    ///
    /// - 广为流传的 `00000000402b5328` 是 **MSA v1 时代**的 ID，
    ///   打 `login.microsoftonline.com/consumers` 会返回
    ///   `AADSTS700016: Application ... was not found in the directory`。
    ///   它属于旧的 live.com 体系，不是 AAD 应用，v2 端点不认。
    /// - 开源启动器（如 HMCL）的做法是 **client_id 由调用方传入**
    ///   （`options.callback.getClientId()`），代码里不硬编码。
    ///   因为硬编码别人的 ID 等于盗用对方的应用配额，
    ///   微软一旦因滥用封禁该应用，所有使用者一起挂。
    /// - 官方启动器的 client_id 未公开，也无法用于第三方。
    ///
    /// 所以正确做法是**每个用户注册自己的 Azure 应用**（免费，约 3 分钟）：
    ///
    /// 1. 打开 https://portal.azure.com → 「Microsoft Entra ID」
    /// 2. 左侧「应用注册」→「新注册」
    /// 3. 名称随意（如 `MyLauncher`）
    /// 4. **受支持的账户类型选「仅个人 Microsoft 账户」**
    ///    （这一步最关键：只有它才能登 Minecraft 的消费者账号）
    /// 5. 注册后复制「应用程序(客户端) ID」
    /// 6. 左侧「身份验证」→ 拉到最底「允许公共客户端流」→ **是** → 保存
    ///
    /// 然后把 ID 填进启动器设置即可。不需要 client secret
    /// （device code 属于公共客户端流，配 secret 反而不安全）。
    ///
    /// 相关常量见 <see cref="ConfigStore.MicrosoftClientId"/>。
    /// </summary>
    public const string DefaultClientId = "";

    private const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    private const string XblUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string McLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string McProfileUrl = "https://api.minecraftservices.com/minecraft/profile";
    private const string McEntitlementsUrl = "https://api.minecraftservices.com/entitlements/mcstore";

    /// <summary>请求的 scope。加上 XboxLive.signin 才能换 XBL token。</summary>
    private const string Scope = "XboxLive.signin offline_access";

    private readonly string _clientId;

    public MicrosoftAuth(string? clientId = null)
    {
        var id = string.IsNullOrWhiteSpace(clientId) ? DefaultClientId : clientId.Trim();
        _clientId = id;
    }

    /// <summary>
    /// client_id 是否已配置。GUI 在点"登录"前先查这个，
    /// 好给出注册指引而不是让用户看 AADSTS700016。
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_clientId);

    /// <summary>缺 client_id 时抛的异常，信息里直接带注册步骤。</summary>
    public static InvalidOperationException MissingClientIdError() => new(
        "还没有配置 Microsoft 应用的客户端 ID，无法登录正版。\n\n" +
        "这是因为正版登录需要你自己的 Azure 应用，公共 ID 用不了" +
        "（微软已停用旧的公共客户端 ID）。注册是免费的，约 3 分钟：\n\n" +
        "1. 打开 https://entra.microsoft.com 并登录\n" +
        "2. 左侧「应用注册」→「新注册」\n" +
        "3. 名称随便填（如 PclLike）\n" +
        "4. 帐户类型选「任何组织目录中的帐户和个人 Microsoft 帐户」\n" +
        "   —— 必须选这个，个人 @outlook.com 账号才登得进来；\n" +
        "      选「仅此组织目录」会报 AADSTS50020\n" +
        "5. 点注册，复制「应用程序(客户端) ID」\n" +
        "6. 左侧「身份验证」→ 最下方「允许公共客户端流」选「是」→ 保存\n" +
        "   —— 这步不能漏，否则设备码登录会报 AADSTS70002\n" +
        "7. 回到启动器设置，把客户端 ID 粘进去\n\n" +
        "不想注册也没关系：用离线模式一样能玩单机和大部分服务器。");

    /// <summary>
    /// 把微软返回的 OAuth 错误翻译成用户能照着做的提示。
    ///
    /// 微软的原始响应是一坨 JSON，直接把 error_description 怼给用户没用 ——
    /// 里面是 "AADSTS70002: The provided client is not supported for this feature."
    /// 这种话，用户既不知道 70002 是什么，也不知道去哪改。
    ///
    /// 所以按 error_codes 分派。没见过的码就原样返回，至少保留排查线索。
    /// </summary>
    private static string TranslateOAuthError(System.Net.HttpStatusCode status, string body)
    {
        string code = "";
        string desc = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error_description", out var d))
                desc = d.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("error_codes", out var codes) &&
                codes.ValueKind == JsonValueKind.Array && codes.GetArrayLength() > 0)
                code = codes[0].GetInt32().ToString();
        }
        catch { /* 不是 JSON，下面兜底 */ }

        var hint = code switch
        {
            // 应用没开「允许公共客户端流」—— 设备码属于公共客户端流
            "70002" =>
                "这是应用的配置问题，不是你的账号问题。\n" +
                "去 https://entra.microsoft.com → 应用注册 → 你的应用 → 身份验证\n" +
                "→ 最下方「高级设置」→「允许公共客户端流」改成「是」→ 保存。",

            // 账号类型与租户不匹配
            "50020" =>
                "这个 Microsoft 账号不在应用所属的组织目录里。\n" +
                "去应用注册的「身份验证」页，把「支持的帐户类型」改成\n" +
                "「任何组织目录中的帐户和个人 Microsoft 帐户」。",

            // 应用不存在 / client_id 写错
            "700016" =>
                "client_id 无效或应用已被删除。请核对启动器设置里粘贴的客户端 ID。",

            // 管理员未同意
            "65004" =>
                "应用需要的权限没有被授予。设备码流程不需要额外权限，\n" +
                "检查应用是否被限制在某些租户策略下。",

            _ => "",
        };

        var head = $"登录失败（HTTP {(int)status}）";
        if (!string.IsNullOrEmpty(code)) head += $"，错误码 AADSTS{code}";

        var msg = head + "\n";
        if (!string.IsNullOrEmpty(hint)) msg += "\n" + hint + "\n";
        if (!string.IsNullOrEmpty(desc)) msg += "\n微软的原始说明：\n" + desc;

        return msg;
    }

    // ---------------------------------------------------------------
    // 步骤 1：拿 device code，让用户去浏览器输
    // ---------------------------------------------------------------

    public sealed record DeviceCodeInfo(
        string DeviceCode,
        string UserCode,
        string VerificationUri,
        string VerificationUriComplete,
        int ExpiresInSeconds,
        int IntervalSeconds,
        string Message);

    public async Task<DeviceCodeInfo> StartDeviceCodeAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) throw MissingClientIdError();

        using var client = NewClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["scope"] = Scope,
        });

        using var resp = await client.PostAsync(DeviceCodeUrl, content, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(TranslateOAuthError(resp.StatusCode, body));

        var dto = JsonSerializer.Deserialize<DeviceCodeDto>(body, Json)
            ?? throw new InvalidOperationException("登录码响应无法解析");

        return new DeviceCodeInfo(
            dto.DeviceCode ?? "",
            dto.UserCode ?? "",
            dto.VerificationUri ?? "https://microsoft.com/link",
            dto.VerificationUriComplete ?? dto.VerificationUri ?? "https://microsoft.com/link",
            dto.ExpiresIn,
            Math.Max(1, dto.Interval),
            dto.Message ?? $"请在浏览器打开 {dto.VerificationUri}，输入代码 {dto.UserCode}");
    }

    // ---------------------------------------------------------------
    // 步骤 2：轮询等用户完成登录
    // ---------------------------------------------------------------

    public sealed record MsToken(string AccessToken, string RefreshToken, int ExpiresInSeconds);

    /// <summary>
    /// 轮询直到用户完成登录或超时。
    ///
    /// MS 规定：<paramref name="interval"/> 秒一次，太频繁会返回
    /// `slow_down`，此时要把间隔加长。这个规矩必须遵守，
    /// 否则会被临时封禁。
    /// </summary>
    public async Task<MsToken> WaitForTokenAsync(
        DeviceCodeInfo info,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var interval = info.IntervalSeconds;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(info.ExpiresInSeconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);

            using var client = NewClient();
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = _clientId,
                ["device_code"] = info.DeviceCode,
            });

            using var resp = await client.PostAsync(TokenUrl, content, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
            {
                var dto = JsonSerializer.Deserialize<TokenDto>(body, Json);
                if (!string.IsNullOrWhiteSpace(dto?.AccessToken))
                {
                    status?.Report("登录成功");
                    return new MsToken(dto.AccessToken!, dto.RefreshToken ?? "", dto.ExpiresIn);
                }
            }

            // 还没登录完的情况：authorization_pending / slow_down，继续等
            var err = TryReadError(body);
            switch (err)
            {
                case "authorization_pending":
                    status?.Report("等待你在浏览器中完成登录…");
                    continue;

                case "slow_down":
                    interval += 5;
                    status?.Report($"轮询太快，间隔调整为 {interval} 秒");
                    continue;

                case "authorization_declined":
                    throw new InvalidOperationException("你取消了登录授权");

                case "expired_token":
                    throw new InvalidOperationException("登录码已过期，请重新获取");

                case "bad_verification_code":
                    throw new InvalidOperationException("登录码无效，请重新获取");

                default:
                    // 其他错误（含空 body）当作继续等，但打印出来便于排查
                    if (!string.IsNullOrWhiteSpace(err))
                        status?.Report($"登录中（{err}）");
                    continue;
            }
        }

        throw new TimeoutException("登录超时。请重新获取登录码。");
    }

    /// <summary>用 refresh_token 换新的 access_token（不需要用户再操作）。</summary>
    public async Task<MsToken> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        using var client = NewClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = _clientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = Scope,
        });

        using var resp = await client.PostAsync(TokenUrl, content, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"刷新登录令牌失败（{resp.StatusCode}）：{TryReadError(body) ?? body}");

        var dto = JsonSerializer.Deserialize<TokenDto>(body, Json)
            ?? throw new InvalidOperationException("刷新响应无法解析");

        return new MsToken(dto.AccessToken ?? "", dto.RefreshToken ?? refreshToken, dto.ExpiresIn);
    }

    // ---------------------------------------------------------------
    // 步骤 3~5：MS token → Xbox → Minecraft 档案
    // ---------------------------------------------------------------

    /// <summary>完整的正版档案：uuid、用户名、access_token。</summary>
    public sealed record McProfile(string Uuid, string Name, string AccessToken, string RefreshToken, string? SkinUrl);

    /// <summary>
    /// 把 MS token 一路换成 Minecraft 档案。
    /// 每一步的失败都翻译成用户能看懂的原因。
    /// </summary>
    public async Task<McProfile> CompleteLoginAsync(MsToken ms, IProgress<string>? status = null, CancellationToken ct = default)
    {
        var client = NewClient();

        // ---- Xbox Live ----
        status?.Report("正在登录 Xbox Live…");
        var (xblToken, userHash) = await XboxAuthenticateAsync(client, ms.AccessToken, ct).ConfigureAwait(false);

        // ---- XSTS ----
        status?.Report("正在获取 XSTS 授权…");
        var xsts = await XstsAuthorizeAsync(client, xblToken, ct).ConfigureAwait(false);

        // ---- Minecraft ----
        status?.Report("正在登录 Minecraft…");
        var (mcToken, mcRefresh) = await MinecraftLoginAsync(client, userHash, xsts, ct).ConfigureAwait(false);

        // ---- 档案 ----
        status?.Report("正在读取游戏档案…");
        var profile = await GetProfileAsync(client, mcToken, ct).ConfigureAwait(false);

        if (profile is null)
            throw new InvalidOperationException(
                "这个账号没有 Minecraft: Java 版。请确认已购买游戏，或换一个账号。");

        return new McProfile(
            profile.Value.Uuid, profile.Value.Name, mcToken, mcRefresh,
            profile.Value.SkinUrl);
    }

    private static async Task<(string Token, string UserHash)> XboxAuthenticateAsync(
        HttpClient client, string msAccessToken, CancellationToken ct)
    {
        var payload = new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = $"d={msAccessToken}",
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
        };

        using var resp = await PostJsonAsync(client, XblUrl, payload, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Xbox Live 登录失败（{resp.StatusCode}）。" +
                "这个 Microsoft 账号可能还没有注册 Xbox 账号，请先在 xbox.com 注册。");

        var dto = JsonSerializer.Deserialize<XblDto>(body, Json)
            ?? throw new InvalidOperationException("Xbox Live 响应无法解析");

        var hash = dto.DisplayClaims?.Xui?.FirstOrDefault()?.Uhs;
        if (string.IsNullOrWhiteSpace(dto.Token) || string.IsNullOrWhiteSpace(hash))
            throw new InvalidOperationException("Xbox Live 没有返回有效令牌");

        return (dto.Token!, hash!);
    }

    private static async Task<string> XstsAuthorizeAsync(HttpClient client, string xblToken, CancellationToken ct)
    {
        var payload = new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xblToken } },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT",
        };

        using var resp = await PostJsonAsync(client, XstsUrl, payload, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            // XSTS 的失败原因藏在 XErr 里，翻译成人话
            var xerr = TryReadLong(body, "XErr");
            var msg = xerr switch
            {
                2148916233 => "这个账号没有 Xbox 账号。请先到 xbox.com 创建一个。",
                2148916235 => "Xbox Live 在你所在的国家/地区不可用。",
                2148916236 or 2148916237 => "这个账号需要完成 Xbox 的年龄验证。",
                2148916238 => "这是未成年账号，需要家长在 family.microsoft.com 上同意。",
                _ => $"XSTS 授权失败（{resp.StatusCode}）{(xerr is null ? "" : $"XErr={xerr}")}",
            };
            throw new InvalidOperationException(msg);
        }

        var dto = JsonSerializer.Deserialize<XblDto>(body, Json);
        if (string.IsNullOrWhiteSpace(dto?.Token))
            throw new InvalidOperationException("XSTS 没有返回有效令牌");

        return dto.Token!;
    }

    private static async Task<(string Token, string Refresh)> MinecraftLoginAsync(
        HttpClient client, string userHash, string xstsToken, CancellationToken ct)
    {
        var payload = new { identityToken = $"XBL3.0 x={userHash};{xstsToken}" };

        using var resp = await PostJsonAsync(client, McLoginUrl, payload, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Minecraft 登录失败（{resp.StatusCode}）。这个账号可能没有购买 Minecraft: Java 版。");

        var dto = JsonSerializer.Deserialize<McLoginDto>(body, Json);
        if (string.IsNullOrWhiteSpace(dto?.AccessToken))
            throw new InvalidOperationException("Minecraft 没有返回有效令牌");

        return (dto.AccessToken!, dto.RefreshToken ?? "");
    }

    private static async Task<(string Uuid, string Name, string? SkinUrl)?> GetProfileAsync(
        HttpClient client, string mcToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, McProfileUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcToken);

        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;   // 没买游戏

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"读取档案失败（{resp.StatusCode}）：{body}");

        var dto = JsonSerializer.Deserialize<McProfileDto>(body, Json);
        if (string.IsNullOrWhiteSpace(dto?.Id) || string.IsNullOrWhiteSpace(dto.Name))
            return null;

        // uuid 是 32 位无连字符的，转成标准格式
        var uuid = dto.Id!.Length == 32
            ? $"{dto.Id[..8]}-{dto.Id[8..12]}-{dto.Id[12..16]}-{dto.Id[16..20]}-{dto.Id[20..]}"
            : dto.Id;

        var skin = dto.Skins?.FirstOrDefault(s => s.State?.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) == true)?.Url;
        return (uuid, dto.Name!, skin);
    }

    /// <summary>检查该账号是否拥有游戏（用于登录前提示）。</summary>
    public async Task<bool> OwnsGameAsync(string mcToken, CancellationToken ct = default)
    {
        using var client = NewClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, McEntitlementsUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcToken);

        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        return resp.IsSuccessStatusCode;
    }

    // ---------------------------------------------------------------
    // 基础设施
    // ---------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static HttpClient NewClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };

        // 复用全局代理配置（NeoForge 那边也需要）
        if (!string.IsNullOrWhiteSpace(Minecraft.Loaders.LoaderHttp.ProxyUrl))
        {
            handler.Proxy = new System.Net.WebProxy(Minecraft.Loaders.LoaderHttp.ProxyUrl!);
            handler.UseProxy = true;
        }

        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PclLike/0.2");
        return c;
    }

    private static async Task<HttpResponseMessage> PostJsonAsync<T>(HttpClient client, string url, T payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.PostAsync(url, content, ct).ConfigureAwait(false);
    }

    private static string? TryReadError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch { return null; }
    }

    private static long? TryReadLong(string body, string prop)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty(prop, out var e)) return null;
            return e.ValueKind == JsonValueKind.Number ? e.GetInt64() : long.TryParse(e.GetString(), out var v) ? v : null;
        }
        catch { return null; }
    }

    // ---- 传输层 DTO（字段名跟着各家 API 走，不是我们的命名选择）----

    private sealed class DeviceCodeDto
    {
        [JsonPropertyName("device_code")] public string? DeviceCode { get; set; }
        [JsonPropertyName("user_code")] public string? UserCode { get; set; }
        [JsonPropertyName("verification_uri")] public string? VerificationUri { get; set; }
        [JsonPropertyName("verification_uri_complete")] public string? VerificationUriComplete { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("interval")] public int Interval { get; set; } = 5;
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class TokenDto
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private sealed class XblDto
    {
        [JsonPropertyName("Token")] public string? Token { get; set; }
        [JsonPropertyName("DisplayClaims")] public DisplayClaims? DisplayClaims { get; set; }
    }

    private sealed class DisplayClaims
    {
        [JsonPropertyName("xui")] public List<Xui>? Xui { get; set; }
    }

    private sealed class Xui
    {
        [JsonPropertyName("uhs")] public string? Uhs { get; set; }
    }

    private sealed class McLoginDto
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    }

    private sealed class McProfileDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("skins")] public List<SkinDto>? Skins { get; set; }
    }

    private sealed class SkinDto
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
    }
}