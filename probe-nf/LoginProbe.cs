using PclLike.Core.Login;
using PclLike.Core.Storage;

Console.OutputEncoding = System.Text.Encoding.UTF8;

const string GameRoot = @"C:\Users\yuwei\AppData\Roaming\.pcllike";

Console.WriteLine("══════ 正版登录：设备码流程实测 ══════");
Console.WriteLine();

// 从配置里读 client_id —— 和 GUI 走同一条路
var cfgPath = ConfigStore.PathFor(GameRoot);
var cfg = ConfigStore.Load(GameRoot);
var clientId = cfg.MicrosoftClientId;

Console.WriteLine($"配置文件: {cfgPath}");
Console.WriteLine($"client_id: {clientId ?? "(空)"}");
Console.WriteLine();

var auth = new MicrosoftAuth(clientId);
Console.WriteLine($"IsConfigured = {auth.IsConfigured}");
Console.WriteLine();

if (!auth.IsConfigured)
{
    Console.WriteLine("✗ client_id 没读到，后面的流程跑不了");
    return;
}

// 步骤 1：要设备码
Console.WriteLine("▸ 步骤 1/5  请求设备码");
try
{
    var code = await auth.StartDeviceCodeAsync();
    Console.WriteLine($"    ✓ 成功");
    Console.WriteLine($"      用户码   : {code.UserCode}");
    Console.WriteLine($"      验证地址 : {code.VerificationUri}");
    Console.WriteLine($"      直达链接 : {code.VerificationUriComplete}");
    Console.WriteLine($"      有效期   : {code.ExpiresInSeconds} 秒");
    Console.WriteLine($"      轮询间隔 : {code.IntervalSeconds} 秒");
    Console.WriteLine();
    Console.WriteLine($"    ┌─────────────────────────────────────────────┐");
    Console.WriteLine($"    │  请在浏览器打开下面的地址，输入代码：        │");
    Console.WriteLine($"    │                                             │");
    Console.WriteLine($"    │      {code.VerificationUri,-37}│");
    Console.WriteLine($"    │      代码： {code.UserCode,-30}│");
    Console.WriteLine($"    │                                             │");
    Console.WriteLine($"    │  或者直接点这个链接（代码已带上）：          │");
    Console.WriteLine($"    │      {code.VerificationUriComplete,-37}│");
    Console.WriteLine($"    └─────────────────────────────────────────────┘");
    Console.WriteLine();
    Console.WriteLine("    微软返回的原始提示：");
    Console.WriteLine($"      {code.Message}");
    Console.WriteLine();
    Console.WriteLine("  ── 到这里为止，client_id 已被微软接受 ──");
    Console.WriteLine("  下一步（轮询换 token）需要真的去浏览器输码，");
    Console.WriteLine("  探针不等你，所以到此为止。");
    Console.WriteLine();
    Console.WriteLine("  ✓ 步骤 1 通过：client_id 有效，设备码流程可用");
}
catch (Exception ex)
{
    Console.WriteLine($"    ✗ 失败：{ex.GetType().Name}: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("  可能原因：");
    Console.WriteLine("    - client_id 写错");
    Console.WriteLine("    - 应用类型不是「公共客户端」");
    Console.WriteLine("    - 租户配置不允许该账户类型");
}

Console.WriteLine();
Console.WriteLine("══════ 结束 ══════");