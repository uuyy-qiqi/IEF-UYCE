using System.Text.Json.Nodes;

// 查 Modrinth version 接口的 dependencies 字段和 game_versions 的真实内容
var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("PclLike/1.0 (Minecraft launcher)");

var json = JsonNode.Parse(await http.GetStringAsync(
    "https://api.modrinth.com/v2/project/sodium/version?loaders=%5B%22fabric%22%5D&game_versions=%5B%221.21.1%22%5D"));

var arr = json!.AsArray();
Console.WriteLine($"带过滤的 version 接口返回 {arr.Count} 个版本");

foreach (var v in arr.Take(2))
{
    Console.WriteLine($"\n版本: {v?["version_number"]}  id={v?["id"]}");
    Console.WriteLine($"  game_versions: {string.Join(",", (v?["game_versions"]?.AsArray() ?? []).Select(x => x?.GetValue<string>()))}");
    Console.WriteLine($"  loaders: {string.Join(",", (v?["loaders"]?.AsArray() ?? []).Select(x => x?.GetValue<string>()))}");
    Console.WriteLine($"  dependencies 原始 JSON: {v?["dependencies"]?.ToJsonString()}");
}

Console.WriteLine("\n══ 无过滤：最新 3 个版本的 game_versions");
var all = JsonNode.Parse(await http.GetStringAsync("https://api.modrinth.com/v2/project/sodium/version"))!.AsArray();
Console.WriteLine($"   共 {all.Count} 个版本");
foreach (var v in all.Take(3))
    Console.WriteLine($"   {v?["version_number"],-32} game_versions={string.Join(",", (v?["game_versions"]?.AsArray() ?? []).Take(4).Select(x => x?.GetValue<string>()))}");

Console.WriteLine("\n══ 确认：不带过滤参数会不会返回 26.3（复现 bug）");
var newest = all[0];
Console.WriteLine($"   最新版: {newest?["version_number"]}  {string.Join(",", (newest?["game_versions"]?.AsArray() ?? []).Select(x => x?.GetValue<string>()))}");

http.Dispose();