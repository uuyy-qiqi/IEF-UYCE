using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PclLike.Core.Skins;

/// <summary>
/// 皮肤来源。用来告诉界面这张头像"是哪来的"，好给出不同提示。
/// </summary>
public enum SkinSource
{
    /// <summary>没查到，用的是内置默认皮肤（Steve / Alex）。</summary>
    Default,

    /// <summary>正版登录拿到的自家皮肤。</summary>
    MojangProfile,

    /// <summary>按玩家名从 Mojang API 查到的皮肤（离线模式常用）。</summary>
    MojangByUsername,

    /// <summary>用户手动指定了本地皮肤文件。</summary>
    LocalFile,
}

/// <summary>
/// 一次皮肤解析的结果：贴图字节 + 来自哪里 + 解析出的玩家名。
///
/// 注意这里**只有字节**，不带任何渲染类型。切贴图、放大、3D 拼装都是
/// GUI 层的事（见 PclLike.Gui.Skins.SkinHead）—— Core 不依赖 WPF。
/// </summary>
public sealed record SkinResult(
    byte[] PngBytes,
    SkinSource Source,
    string? ResolvedName);

/// <summary>
/// 皮肤获取。
///
/// 两件事：
///   1. 按玩家名或 UUID 查 Mojang 的皮肤（离线账号也想要头像时就靠它）
///   2. 下载皮肤 PNG 并缓存到磁盘（同一个玩家不重复拉）
///
/// Mojang 的接口是 api.mojang.com / sessionserver.mojang.com，国内直连有时不稳，
/// 失败一律静默回退到默认皮肤，不让启动器崩。
/// </summary>
public sealed class SkinService
{
    private const string ProfileByUuidApi = "https://sessionserver.mojang.com/session/minecraft/profile/";
    private const string ProfileByNameApi = "https://api.mojang.com/users/profiles/minecraft/";

    private readonly HttpClient _http;

    /// <summary>内存里再缓存一层贴图字节，避免同一会话反复读盘。</summary>
    private readonly ConcurrentDictionary<string, byte[]> _memory = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>皮肤缓存目录。传 null 就用 %LocalAppData%\PclLike\skins。</summary>
    public string CacheDirectory { get; }

    /// <summary>用户手动指定的本地皮肤文件；设了它就不再联网查。</summary>
    public string? LocalSkinOverride { get; set; }

    /// <summary>内置默认皮肤的字节（Steve）。查不到时用它。</summary>
    public byte[] DefaultSkinBytes { get; set; } = Array.Empty<byte>();

    public SkinService(string? cacheDirectory = null)
    {
        CacheDirectory = cacheDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PclLike", "skins");

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PclLike/0.3 (Minecraft launcher)");
    }

    // ────────────────────────────────────────────────────────────
    // 查询
    // ────────────────────────────────────────────────────────────

    /// <summary>按玩家名查皮肤。查不到返回 null（调用方回退到默认皮肤）。</summary>
    public async Task<SkinResult?> ByUsernameAsync(string username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;

        // 离线玩家名可能不是正版账号，查不到很正常 —— 不抛异常
        var uuid = await ResolveUuidAsync(username.Trim(), ct).ConfigureAwait(false);
        if (uuid is null) return null;

        return await ByUuidAsync(uuid, ct).ConfigureAwait(false);
    }

    /// <summary>按 UUID 查皮肤（正版登录后可直接用这个）。</summary>
    public async Task<SkinResult?> ByUuidAsync(string uuid, CancellationToken ct = default)
    {
        var clean = uuid.Replace("-", "", StringComparison.Ordinal).Trim();
        if (clean.Length != 32) return null;

        // 缓存命中：直接读盘，不打网络
        var cached = Path.Combine(CacheDirectory, clean + ".png");
        if (File.Exists(cached))
        {
            var bytes = await File.ReadAllBytesAsync(cached, ct).ConfigureAwait(false);
            if (LooksLikeSkin(bytes))
                return new SkinResult(bytes, SkinSource.MojangByUsername, null);
        }

        try
        {
            using var resp = await _http.GetAsync(ProfileByUuidApi + clean, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var dto = await resp.Content
                .ReadFromJsonAsync<ProfileDto>(cancellationToken: ct).ConfigureAwait(false);

            var skinUrl = dto?.Properties
                ?.FirstOrDefault(p => p.Name == "textures")?.Value is { } b64
                ? ExtractSkinUrl(b64) : null;

            if (skinUrl is null) return null;

            var png = await _http.GetByteArrayAsync(skinUrl, ct).ConfigureAwait(false);
            if (!LooksLikeSkin(png)) return null;

            Directory.CreateDirectory(CacheDirectory);
            await File.WriteAllBytesAsync(cached, png, ct).ConfigureAwait(false);

            return new SkinResult(png, SkinSource.MojangProfile, dto?.Name);
        }
        catch
        {
            // 网络问题不该让启动器崩，静默回退到默认皮肤
            return null;
        }
    }

    /// <summary>玩家名 → UUID。用的是 Mojang 的公开映射接口。</summary>
    public async Task<string?> ResolveUuidAsync(string username, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http
                .GetAsync(ProfileByNameApi + Uri.EscapeDataString(username), ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode) return null;

            var dto = await resp.Content
                .ReadFromJsonAsync<NameToUuidDto>(cancellationToken: ct).ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(dto?.Id) ? null : dto!.Id;
        }
        catch { return null; }
    }

    /// <summary>
    /// 从 textures 属性里把皮肤地址挖出来。
    ///
    /// 这个属性的值是一段 base64 包着的 JSON：
    ///   {"textures":{"SKIN":{"url":"http://textures.minecraft.net/texture/..."}}}
    /// 格式变更过（早期是 http，现在是 https），两种都要吃下。
    /// </summary>
    private static string? ExtractSkinUrl(string base64Value)
    {
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64Value));
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("textures", out var textures)) return null;
            if (!textures.TryGetProperty("SKIN", out var skin)) return null;
            if (!skin.TryGetProperty("url", out var url)) return null;

            var s = url.GetString();
            if (string.IsNullOrWhiteSpace(s)) return null;

            // 老数据里可能是 http，Mojang 现在只认 https
            return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? "https://" + s["http://".Length..]
                : s;
        }
        catch { return null; }
    }

    /// <summary>
    /// 拿一份能直接显示的贴图：优先本地覆盖 → 缓存 → 网络查询 → 默认皮肤。
    /// 界面只调这个方法，不用自己判断回退顺序。
    /// </summary>
    public async Task<SkinResult> ResolveAsync(
        string? username, string? uuid, CancellationToken ct = default)
    {
        // 1) 用户手动指定的本地文件优先
        if (!string.IsNullOrWhiteSpace(LocalSkinOverride) && File.Exists(LocalSkinOverride))
        {
            var local = await File.ReadAllBytesAsync(LocalSkinOverride, ct).ConfigureAwait(false);
            if (LooksLikeSkin(local))
                return new SkinResult(local, SkinSource.LocalFile, username);
        }

        // 2) 内存缓存
        var key = uuid ?? username ?? "";
        if (_memory.TryGetValue(key, out var hit) && LooksLikeSkin(hit))
            return new SkinResult(hit, SkinSource.MojangProfile, username);

        // 3) 网络：有 uuid 用 uuid，否则按名字查
        var found = !string.IsNullOrWhiteSpace(uuid)
            ? await ByUuidAsync(uuid!, ct).ConfigureAwait(false)
            : null;

        if (found is null && !string.IsNullOrWhiteSpace(username))
            found = await ByUsernameAsync(username!, ct).ConfigureAwait(false);

        if (found is not null)
        {
            _memory[key] = found.PngBytes;
            return found with { ResolvedName = found.ResolvedName ?? username };
        }

        // 4) 默认皮肤
        return new SkinResult(DefaultSkinBytes, SkinSource.Default, username);
    }

    // ────────────────────────────────────────────────────────────
    // 字节级校验（不依赖 WPF，所以能待在 Core 里）
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// 这份字节看起来是不是一张可用的皮肤 PNG？
    ///
    /// 只做两件事：确认 PNG magic，并读出 IHDR 里的宽高，
    /// 要求是标准的 64×64 或旧版 64×32。
    /// </summary>
    public static bool LooksLikeSkin(byte[]? png)
    {
        if (png is null || png.Length < 33) return false;

        // PNG magic: 89 50 4E 47 0D 0A 1A 0A
        if (png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47)
            return false;

        // IHDR 数据从偏移 16 开始：宽 4 字节，高 4 字节，都是大端
        var w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        var h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];

        return (w == 64 && h == 64) || (w == 64 && h == 32);
    }

    // ---- JSON DTO ----

    private sealed class NameToUuidDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class ProfileDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("properties")] public List<PropDto>? Properties { get; set; }
    }

    private sealed class PropDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("value")] public string? Value { get; set; }
    }
}