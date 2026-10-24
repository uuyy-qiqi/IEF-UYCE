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
    /// ## 这里踩过的坑，写下来免得再踩
    ///
    /// 一开始的结论是"必须每个用户自己注册 Azure 应用"，理由是：流传的
    /// `00000000402b5328` 打 `login.microsoftonline.com/consumers` 会返回
    /// `AADSTS700016: Application ... was not found in the directory`。
    ///
    /// **那个推理错在把"不属于 AAD"当成了"不能用"。** 实测下来：
    ///
    /// - `00000000402b5328` 在 `login.live.com` 上是**好的** —— 授权地址直接返回
    ///   微软的登录页（实测 30KB 正常页面），而不存在的 GUID 会返回 3.4KB 的错误页。
    /// - 自己注册的 Azure 应用（Entra + 设备码）能一路走到 XSTS，但最后
    ///   `login_with_xbox` 被 Minecraft 拒：`403 Invalid app registration`。
    ///   这不是账号没买游戏，是 Minecraft 不认这个应用。
    ///
    /// 也就是说：**能走完的是 Live Connect 那条老路**，本机装着的 PCL CE 也是走它
    /// （日志里是 `login.live.com/oauth20_token.srf`）。
    ///
    /// 所以默认用那个公开的桌面应用号。设置里那种"自己注册的 Azure 应用 GUID"
    /// 会被构造函数忽略掉（见 <see cref="CustomClientIdIgnored"/>）——
    /// 它必然失败，留着只会让用户反复试。
    ///
    /// UserKey 相关见 <see cref="ConfigStore.MicrosoftClientId"/>。
    /// </summary>
    public const string DefaultClientId = LiveClientId;

    /// <summary>
    /// 默认用的应用号：微软自己那个公开的桌面应用（Xbox 集成用的那个）。
    ///
    /// 这个值不是随手抄的。实测过一件事：**能走完的正版登录走的是
    /// `login.live.com`（老的 Live Connect），不是 `login.microsoftonline.com`。**
    ///
    /// - Entra（login.microsoftonline.com）+ 自己注册的 Azure 应用：设备码能拿到、
    ///   Xbox Live 能过、XSTS 也能过，但最后一步 `login_with_xbox` 会被 Minecraft
    ///   拒掉 —— `403 Invalid app registration`。同一套代码把 client_id 换成不存在的
    ///   GUID，会在更早一步就 401；说明 403 那一层是 Minecraft 在查"这个应用我认不认"，
    ///   而它只认注册过的应用 —— 自己注册的 Azure 应用不在里面。
    /// - Live Connect（login.live.com）用的是微软自己的公开应用号，Minecraft 认它。
    ///   本机的 PCL CE 日志里就是 `login.live.com/oauth20_token.srf`，走通了。
    ///
    /// 所以默认走 Live Connect。设备码 + Entra 那套代码留着（能跑到 XSTS，
    /// 排查时有用），但从产品配置里已经走不到了 —— 构造函数会把 GUID 换回来。
    /// </summary>
    private const string LiveClientId = "00000000402b5328";

    private const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    private const string XblUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string McLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string McProfileUrl = "https://api.minecraftservices.com/minecraft/profile";
    private const string McEntitlementsUrl = "https://api.minecraftservices.com/entitlements/mcstore";

    /// <summary>Live Connect 的三个端点（登录页、换令牌、回跳地址）。</summary>
    private const string LiveAuthorizeUrl = "https://login.live.com/oauth20_authorize.srf";
    private const string LiveTokenUrl = "https://login.live.com/oauth20_token.srf";

    /// <summary>
    /// 回跳地址。这是那个公开应用号预先注册好的桌面回跳页 —— 登录完微软会跳到它，
    /// 并把 code 放在 URL 上。我们不去"打开"这个页面，只是盯着地址里有没有 code。
    /// </summary>
    private const string LiveRedirectUrl = "https://login.live.com/oauth20_desktop.srf";

    /// <summary>Live Connect 的 scope 写法（老服务，拿不到 XBL token 就得重来）。</summary>
    private const string LiveScope = "XboxLive.signin XboxLive.offline_access";

    /// <summary>请求的 scope。加上 XboxLive.signin 才能换 XBL token。</summary>
    private const string Scope = "XboxLive.signin offline_access";

    private readonly string _clientId;

    public MicrosoftAuth(string? clientId = null)
    {
        var id = string.IsNullOrWhiteSpace(clientId) ? DefaultClientId : clientId.Trim();

        // 自己注册的 Entra 应用号（GUID 长相）用来登录是死路：实测能一路走到 XSTS，
        // 但最后 `login_with_xbox` 会被 Minecraft 拒 —— `403 Invalid app registration`。
        // 它不是账号问题，是 Minecraft 不认这个应用。
        //
        // 所以见到 GUID 就退回内置应用号，并把这件事记下来告诉界面。
        // 不能把一个必然失败的设置静静地留在用户配置里让他反复试。
        if (!id.StartsWith("00000000", StringComparison.Ordinal))
        {
            CustomClientIdIgnored = true;
            id = DefaultClientId;
        }

        _clientId = id;
    }

    /// <summary>
    /// 配置里那个自定义应用号被忽略了（它是 Entra GUID，登不进游戏）。
    /// 界面据此给用户一句说明，别让他以为自己填的东西生效了。
    /// </summary>
    public bool CustomClientIdIgnored { get; }

    /// <summary>
    /// client_id 是否已配置。GUI 在点"登录"前先查这个，
    /// 好给出注册指引而不是让用户看 AADSTS700016。
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_clientId);

    /// <summary>
    /// client_id 是空的时候抛的异常。
    /// 
    /// 正常情况下走不到这里（构造函数总会退回内置应用号），留着是为了万一
    /// <see cref="DefaultClientId"/> 被清掉时有个能看懂的报错。
    /// </summary>
    public static InvalidOperationException MissingClientIdError() => new(
        "登录用的应用号是空的，无法登录正版。\n\n" +
        "启动器内置了一个可用的公开应用号，settings 里把它清空就会变成这样。\n" +
        "把设置里「Microsoft 应用号」那一栏留空即可（留空就是用内置的）。\n\n" +
        "另：自己注册的 Azure 应用号**不能**用来登录正版 —— 实测能走到最后一步，\n" +
        "但会被 Minecraft 拒（403 Invalid app registration）。所以不用去注册。\n\n" +
        "不想登录也没关系：离线模式一样能玩单机和大部分服务器。");

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

    /// <summary>
    /// 用 refresh_token 换新的 access_token（不需要用户再操作）。
    ///
    /// 走哪个端点取决于应用号属于哪家：Live 应用只能去 login.live.com，Azure 应用
    /// 只能去 Entra。用错了会被回 invalid_client，所以这里必须分派。
    /// </summary>
    public Task<MsToken> RefreshAsync(string refreshToken, CancellationToken ct = default)
        => UsesLiveConnect ? RefreshLiveAsync(refreshToken, ct) : RefreshEntraAsync(refreshToken, ct);

    private async Task<MsToken> RefreshEntraAsync(string refreshToken, CancellationToken ct)
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
    // 步骤 1~2：Live Connect —— 真正能走完的那条登录路
    // ---------------------------------------------------------------

    /// <summary>
    /// 当前用的是不是 Live Connect（看应用号长什么样就够判断）。
    /// 界面靠它决定走哪条登录路：Live 走内嵌浏览器，Entra 走设备码。
    /// </summary>
    public bool UsesLiveConnect => _clientId.StartsWith("00000000", StringComparison.Ordinal);

    /// <summary>
    /// 拼出登录窗口要打开的授权地址。用户在窗口里登录完，微软会跳到
    /// <see cref="LiveRedirectUrl"/>，那串地址里带着 code。
    /// </summary>
    public string BuildLiveAuthorizeUrl()
    {
        return LiveAuthorizeUrl +
               $"?client_id={Uri.EscapeDataString(_clientId)}" +
               "&response_type=code" +
               $"&redirect_uri={Uri.EscapeDataString(LiveRedirectUrl)}" +
               $"&scope={Uri.EscapeDataString(LiveScope)}" +
               "&display=touch";
    }

    /// <summary>
    /// 从登录窗口最终的地址里把 code 抠出来。还没登录完就返回 null；
    /// 微软明确报了错就把错误原样抛出来 —— 这一层不该替用户猜。
    /// </summary>
    public static string? ReadCodeFromRedirect(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var code = QueryValue(url, "code");
        if (!string.IsNullOrWhiteSpace(code)) return code;

        var err = QueryValue(url, "error");
        if (!string.IsNullOrWhiteSpace(err))
        {
            var desc = QueryValue(url, "error_description");
            throw new InvalidOperationException(
                $"登录被拒绝：{err}{(string.IsNullOrWhiteSpace(desc) ? "" : $"（{desc}）")}");
        }

        return null;
    }

    /// <summary>自己解析查询参数，不依赖 Uri 对畸形地址的容忍度。</summary>
    private static string? QueryValue(string url, string key)
    {
        var q = url.IndexOf('?');
        if (q < 0) return null;

        foreach (var pair in url[(q + 1)..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (!pair[..eq].Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }
        return null;
    }

    /// <summary>用授权码换 MS 令牌。</summary>
    public async Task<MsToken> ExchangeLiveCodeAsync(string code, CancellationToken ct = default)
    {
        using var client = NewClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = _clientId,
            ["code"] = code,
            ["redirect_uri"] = LiveRedirectUrl,
            ["scope"] = LiveScope,
        });

        using var resp = await client.PostAsync(LiveTokenUrl, content, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"授权码换取令牌失败（{resp.StatusCode}）：{TryReadError(body) ?? body.Trim()}");

        var dto = JsonSerializer.Deserialize<TokenDto>(body, Json)
            ?? throw new InvalidOperationException("换令牌响应无法解析");

        if (string.IsNullOrWhiteSpace(dto.AccessToken))
            throw new InvalidOperationException("换令牌响应里没有 access_token");

        return new MsToken(dto.AccessToken!, dto.RefreshToken ?? "", dto.ExpiresIn);
    }

    /// <summary>
    /// Live Connect 的刷新。refresh_token 是长期有效的 —— 所以这一个 code
    /// 只在第一次登录时用得上，之后启动器都靠它自动续。
    /// </summary>
    public async Task<MsToken> RefreshLiveAsync(string refreshToken, CancellationToken ct = default)
    {
        using var client = NewClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = _clientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = LiveScope,
        });

        using var resp = await client.PostAsync(LiveTokenUrl, content, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"刷新登录令牌失败（{resp.StatusCode}）：{TryReadError(body) ?? body.Trim()}");

        var dto = JsonSerializer.Deserialize<TokenDto>(body, Json)
            ?? throw new InvalidOperationException("刷新响应无法解析");

        return new MsToken(dto.AccessToken ?? "", dto.RefreshToken ?? refreshToken, dto.ExpiresIn);
    }

    /// <summary>
    /// 正版登录的完整入口（Live Connect 版）。
    ///
    /// <paramref name="requestCode"/> 由界面提供：把登录窗口打开、等用户登完，
    /// 把地址里的 code 交回来。Core 不认识窗口，所以这里只管要一个 code。
    /// </summary>
    public async Task<McProfile> LoginWithLiveAsync(
        Func<CancellationToken, Task<string?>> requestCode,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        status?.Report("正在打开微软登录窗口…");
        var code = await requestCode(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("登录窗口被关掉了，没有拿到授权码。");

        status?.Report("正在换取登录令牌…");
        var ms = await ExchangeLiveCodeAsync(code, ct).ConfigureAwait(false);

        return await CompleteLoginAsync(ms, status, ct).ConfigureAwait(false);
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
        {
            // 服务端在 body 里写明了原因，直接用原文。
            //
            // 这里以前自己猜了一句"这个账号可能没有购买 Minecraft: Java 版"，
            // 实测那句话是错的 —— 403 的原文是 `Invalid app registration`，
            // 说的是登录用的应用号不被 Minecraft 认可，跟账号买没买游戏无关。
            // 猜错的提示会把用户带去查订单，白折腾。
            var reason = TryReadText(body, "errorMessage") ?? body.Trim();
            var extra = reason.Contains("Invalid app registration", StringComparison.OrdinalIgnoreCase)
                ? "\n\n（这是说登录用的应用号不被 Minecraft 认可，不是账号问题。）"
                : "";
            throw new InvalidOperationException(
                $"Minecraft 登录失败（{(int)resp.StatusCode}）：{reason}{extra}");
        }

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

    /// <summary>取一个字符串字段；不是字符串或取不到都算 null（不抛）。</summary>
    private static string? TryReadText(string body, string prop)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty(prop, out var e)) return null;
            return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
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