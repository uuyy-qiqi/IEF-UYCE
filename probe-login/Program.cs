// 正版登录链路诊断探针。
//
// 目的只有一个：把每一步的**原始响应**打出来。启动器里那句
// 「这个账号可能没有购买 Minecraft: Java 版」是我自己猜的 —— 403 可能来自
// 五六种完全不同的原因，猜没有意义，看服务端到底说了什么才有意义。
//
// 用法：probe-login [client_id]
//   不给 client_id 就从 launcher_config.json 里读。

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PclLike.Core.Storage;

Console.OutputEncoding = Encoding.UTF8;

var gameRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".pcllike");
// 第一个不以 -- 开头的参数当 client_id；--new 是强制重新走设备码的标志。
var clientId = args.FirstOrDefault(a => !a.StartsWith("--")) ?? ConfigStore.Load(gameRoot).MicrosoftClientId;

if (string.IsNullOrWhiteSpace(clientId))
{
    Console.WriteLine("没有 client_id。用法：probe-login <client_id>");
    return 1;
}

Console.WriteLine("════════ 正版登录链路诊断 ════════");
Console.WriteLine($"client_id : {clientId}");
Console.WriteLine($"游戏根目录 : {gameRoot}");
Console.WriteLine();

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("PclLike-Diagnostic/1.0");

var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

// ── 1. 取 MS 令牌 ──────────────────────────────────────────────
//
// 优先用上次存下来的 refresh_token。设备码要人手动授权，每试一次都让用户
// 点一遍不现实 —— 而这个链路要试很多次。
var tokenPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".cache", "probe-login-token.json");
tokenPath = Path.GetFullPath(tokenPath);

string? msToken = null;

if (!args.Contains("--new") && File.Exists(tokenPath))
{
    Console.WriteLine("[1] 用上次存下的 refresh_token 换新令牌（不想用就加 --new）");

    try
    {
        var saved = JsonDocument.Parse(File.ReadAllText(tokenPath)).RootElement;
        var refresh = saved.GetProperty("refresh_token").GetString() ?? "";
        var savedClient = saved.TryGetProperty("client_id", out var sc) ? sc.GetString() : null;

        if (savedClient == clientId && refresh.Length > 0)
        {
            var rb = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["scope"] = "XboxLive.signin offline_access",
            });

            using var rr = await http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", rb);
            var rt2 = await rr.Content.ReadAsStringAsync();
            Console.WriteLine($"    HTTP {(int)rr.StatusCode}");

            if (rr.IsSuccessStatusCode)
            {
                using var rd = JsonDocument.Parse(rt2);
                msToken = rd.RootElement.GetProperty("access_token").GetString();
                Console.WriteLine("    换到了访问令牌。");
            }
            else
            {
                Console.WriteLine($"    换不到，退回设备码流程。原始响应：{rt2}");
            }
        }
        else
        {
            Console.WriteLine("    存的那份是别的 client_id，忽略。");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"    读 token 文件失败（{ex.Message}），退回设备码流程。");
    }
}

// 没换到就自己走一遍设备码流程。
if (msToken is null)
{

Console.WriteLine("[1] 申请设备码");

var dcBody = new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["client_id"] = clientId,
    ["scope"] = "XboxLive.signin offline_access",
});
using var dcResp = await http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode", dcBody);
var dcText = await dcResp.Content.ReadAsStringAsync();
Console.WriteLine($"    HTTP {(int)dcResp.StatusCode}");

if (!dcResp.IsSuccessStatusCode)
{
    Console.WriteLine("    原始响应：");
    Console.WriteLine(dcText);
    return 1;
}

using var dc = JsonDocument.Parse(dcText);
var deviceCode = dc.RootElement.GetProperty("device_code").GetString()!;
var userCode = dc.RootElement.GetProperty("user_code").GetString()!;
var verifyUri = dc.RootElement.GetProperty("verification_uri").GetString()!;
var interval = dc.RootElement.TryGetProperty("interval", out var iv) ? iv.GetInt32() : 5;

Console.WriteLine();
Console.WriteLine($"    ┌──────────────────────────────────────────┐");
Console.WriteLine($"    │   验证码： {userCode,-30}│");
Console.WriteLine($"    └──────────────────────────────────────────┘");
Console.WriteLine($"    地址：{verifyUri}");
Console.WriteLine();
Console.WriteLine("    请用**拥有 Minecraft: Java 版的那个账号**完成授权。");
Console.WriteLine("    （浏览器里若已登录别的账号，先退出或换无痕窗口。）");
Console.WriteLine();

try { Process.Start(new ProcessStartInfo(verifyUri) { UseShellExecute = true }); } catch { }

// ── 2. 轮询拿 MS token ─────────────────────────────────────────
Console.WriteLine("[2] 等待授权完成…");

var deadline = DateTime.UtcNow.AddMinutes(10);

while (DateTime.UtcNow < deadline)
{
    await Task.Delay(TimeSpan.FromSeconds(interval));

    var tokBody = new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["client_id"] = clientId,
        ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
        ["device_code"] = deviceCode,
    });

    using var tokResp = await http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", tokBody);
    var tokText = await tokResp.Content.ReadAsStringAsync();

    if (tokResp.IsSuccessStatusCode)
    {
        using var td = JsonDocument.Parse(tokText);
        msToken = td.RootElement.GetProperty("access_token").GetString()!;
        var refresh = td.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        Console.WriteLine($"    HTTP 200，拿到令牌（refresh_token {(string.IsNullOrEmpty(refresh) ? "没有" : "有")}）");

        // 存下来，下次就不用再让用户点设备码了。
        if (!string.IsNullOrEmpty(refresh))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(tokenPath)!);
                File.WriteAllText(tokenPath,
                    JsonSerializer.Serialize(new { client_id = clientId, refresh_token = refresh }));
                Console.WriteLine($"    已存到 {tokenPath}");
            }
            catch (Exception ex) { Console.WriteLine($"    存 token 失败：{ex.Message}"); }
        }

        break;
    }

    try
    {
        using var ed = JsonDocument.Parse(tokText);
        var err = ed.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "?";
        if (err is "authorization_pending" or "slow_down") continue;
        Console.WriteLine($"    轮询出错：{err} —— {tokText}");
        return 1;
    }
    catch { Console.WriteLine($"    轮询响应无法解析：{tokText}"); }
}

if (msToken is null) { Console.WriteLine("    超时，没等到授权。"); return 1; }

} // 设备码流程结束

if (msToken is null) { Console.WriteLine("    没有 MS 令牌，走不下去。"); return 1; }

// ── 3. Xbox Live ───────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("[3] Xbox Live 用户认证");

var xblPayload = JsonSerializer.Serialize(new
{
    Properties = new
    {
        AuthMethod = "RPS",
        SiteName = "user.auth.xboxlive.com",
        RpsTicket = $"d={msToken}",
    },
    RelyingParty = "http://auth.xboxlive.com",
    TokenType = "JWT",
});

using var xblResp = await http.PostAsync("https://user.auth.xboxlive.com/user/authenticate",
    new StringContent(xblPayload, Encoding.UTF8, "application/json"));
var xblText = await xblResp.Content.ReadAsStringAsync();
Console.WriteLine($"    HTTP {(int)xblResp.StatusCode}");
if (!xblResp.IsSuccessStatusCode) { Console.WriteLine($"    原始响应：\n{xblText}"); return 1; }

string xblToken, uhs;
using (var xd = JsonDocument.Parse(xblText))
{
    xblToken = xd.RootElement.GetProperty("Token").GetString()!;
    uhs = xd.RootElement.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()!;
}
Console.WriteLine($"    uhs = {uhs}");

// ── 4. XSTS ────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("[4] XSTS 授权（RelyingParty = rp://api.minecraftservices.com/）");

var xstsPayload = JsonSerializer.Serialize(new
{
    Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xblToken } },
    RelyingParty = "rp://api.minecraftservices.com/",
    TokenType = "JWT",
});

using var xstsResp = await http.PostAsync("https://xsts.auth.xboxlive.com/xsts/authorize",
    new StringContent(xstsPayload, Encoding.UTF8, "application/json"));
var xstsText = await xstsResp.Content.ReadAsStringAsync();
Console.WriteLine($"    HTTP {(int)xstsResp.StatusCode}");

if (!xstsResp.IsSuccessStatusCode)
{
    Console.WriteLine($"    原始响应：\n{xstsText}");
    using var xerr = JsonDocument.Parse(xstsText);
    if (xerr.RootElement.TryGetProperty("XErr", out var xe))
        Console.WriteLine($"    XErr = {xe.GetInt64()}");
    return 1;
}

string xstsToken, xid = "?";
using (var sd = JsonDocument.Parse(xstsText))
{
    xstsToken = sd.RootElement.GetProperty("Token").GetString()!;
    var xui = sd.RootElement.GetProperty("DisplayClaims").GetProperty("xui")[0];
    if (xui.TryGetProperty("xid", out var x)) xid = x.GetString() ?? "?";
    if (xui.TryGetProperty("uhs", out var u2)) uhs = u2.GetString() ?? uhs;
}
Console.WriteLine($"    xid（Xbox 账号 id） = {xid}");
Console.WriteLine($"    uhs = {uhs}");

// XSTS 令牌本身是一个 JWT。Minecraft 服务端就是读它里面的 claim 做判断的，
// 所以把它解出来看 —— "Invalid app registration" 到底对应哪个 claim 少了值，
// 答案在这里而不在我的猜测里。
Console.WriteLine();
Console.WriteLine("    ── XSTS 令牌自己的 claim（Minecraft 读的就是这个）──");

var segs = xstsToken.Split('.');
Console.WriteLine($"      令牌长度 {xstsToken.Length}，段数 {segs.Length}，开头：{xstsToken.Substring(0, Math.Min(50, xstsToken.Length))}");

if (segs.Length >= 2)
{
    try
    {
        var b64 = segs[1].Replace('-', '+').Replace('_', '/');
        var rem = b64.Length % 4;
        if (rem == 1) b64 = b64[..^1];            // 非法长度，去掉最后一个字符再补
        else if (rem > 1) b64 = b64.PadRight(b64.Length + (4 - rem), '=');

        var bytes = Convert.FromBase64String(b64);
        var payloadJson = Encoding.UTF8.GetString(bytes);

        Console.WriteLine($"      解出 {bytes.Length} 字节，开头：{payloadJson.Substring(0, Math.Min(40, payloadJson.Length))}");

        using var pd = JsonDocument.Parse(payloadJson);
        foreach (var prop in pd.RootElement.EnumerateObject())
        {
            var v = prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? prop.Value.GetRawText()
                : prop.Value.ToString();

            // iss/aud/rp 是"谁申请的令牌"，appid 是申请它的应用，xid/uhs 是账号。
            var mark = prop.Name is "aud" or "rp" or "appid" or "iss" or "xid" or "uhs" or "agg" ? " ★" : "";
            Console.WriteLine($"      {prop.Name} = {v}{mark}");
        }
    }
    catch (Exception ex) { Console.WriteLine($"      解不开：{ex.Message}"); }
}
else
{
    Console.WriteLine("      令牌不是 JWT 形状。");
}

// ── 5. 这个 Xbox 账号叫什么 ────────────────────────────────────
Console.WriteLine();
Console.WriteLine("[5] 查这个 Xbox 账号的玩家代号（确认到底登的是哪个账号）");

using (var gtReq = new HttpRequestMessage(HttpMethod.Get,
           "https://profile.xboxlive.com/users/me/profile/settings?settings=Gamertag"))
{
    gtReq.Headers.Authorization = new AuthenticationHeaderValue("XBL3.0", $"x={uhs};{xstsToken}");
    gtReq.Headers.Add("x-xbl-contract-version", "2");

    using var gtResp = await http.SendAsync(gtReq);
    var gtText = await gtResp.Content.ReadAsStringAsync();
    Console.WriteLine($"    HTTP {(int)gtResp.StatusCode}");
    Console.WriteLine($"    原始响应：{gtText}");
}

// ── 6. login_with_xbox（403 就出在这一步） ─────────────────────
Console.WriteLine();
Console.WriteLine("[6] 用 XSTS 换 Minecraft 令牌 ── ★ 403 就是这一步");

var mcPayload = JsonSerializer.Serialize(new { identityToken = $"XBL3.0 x={uhs};{xstsToken}" });
using var mcResp = await http.PostAsync("https://api.minecraftservices.com/authentication/login_with_xbox",
    new StringContent(mcPayload, Encoding.UTF8, "application/json"));
var mcText = await mcResp.Content.ReadAsStringAsync();
Console.WriteLine($"    HTTP {(int)mcResp.StatusCode}");
Console.WriteLine($"    原始响应：\n{mcText}");

if (!mcResp.IsSuccessStatusCode) return 1;

string mcToken;
using (var md = JsonDocument.Parse(mcText))
    mcToken = md.RootElement.GetProperty("access_token").GetString()!;

// ── 7. 档案 ────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("[7] 读游戏档案 /minecraft/profile");

using (var prReq = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile"))
{
    prReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcToken);
    using var prResp = await http.SendAsync(prReq);
    var prText = await prResp.Content.ReadAsStringAsync();
    Console.WriteLine($"    HTTP {(int)prResp.StatusCode}");
    Console.WriteLine($"    原始响应：\n{prText}");
}

// ── 8. 拥有的东西 ──────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("[8] 拥有的权益 /entitlements/mcstore");

using (var enReq = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/entitlements/mcstore"))
{
    enReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcToken);
    using var enResp = await http.SendAsync(enReq);
    var enText = await enResp.Content.ReadAsStringAsync();
    Console.WriteLine($"    HTTP {(int)enResp.StatusCode}");
    Console.WriteLine($"    原始响应：\n{enText}");
}

Console.WriteLine();
Console.WriteLine("════════ 诊断结束 ════════");
return 0;