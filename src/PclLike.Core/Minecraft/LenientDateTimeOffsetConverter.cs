using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PclLike.Core.Minecraft;

/// <summary>
/// 宽容的 DateTimeOffset 转换器。
///
/// ## 为什么要自己写
///
/// System.Text.Json 内置的 DateTimeOffset 转换器只认严格 ISO 8601——
/// 时区偏移**必须**带冒号（`+00:00`）。
///
/// 但实际数据来源五花八门：
///   - Mojang 官方清单：`2024-08-08T14:08:04+00:00`  ← 带冒号，能过
///   - Fabric meta API：`2026-10-22T19:06:37+0000`   ← 不带冒号，直接崩
///
/// 崩的后果很严重：这不是"少读一个字段"，是反序列化整个版本 JSON 抛异常，
/// 版本压根装不上。所以日期必须读得宽容——日期对我们只是显示用途，
/// 读不到也不该挡住启动。
///
/// 支持格式：
///   - 带冒号偏移：2024-08-08T14:08:04+00:00
///   - 不带冒号：  2026-10-22T19:06:37+0000
///   - 带 Z：      2024-08-08T14:08:04Z
///   - 无时区：    2024-08-08T14:08:04（当 UTC）
///   - 纯日期：    2024-08-08
/// </summary>
public sealed class LenientDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",   // 标准 ISO，带冒号偏移或 Z
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd",
    ];

    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        if (reader.TokenType != JsonTokenType.String)
        {
            // 有些版本 JSON 把时间写成数字（unix 秒），顺手认了
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var unix))
                return DateTimeOffset.FromUnixTimeSeconds(unix);

            reader.Skip();
            return null;
        }

        var raw = reader.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // 1. 先按标准解析（覆盖绝大多数情况，含 +00:00 与 Z）
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var direct))
            return direct;

        // 2. 把 +0000 / -0700 这种无冒号偏移补上冒号再试
        var fixedUp = AddColonToOffset(raw);
        if (DateTimeOffset.TryParse(fixedUp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var second))
            return second;

        // 3. 逐格式试
        foreach (var f in Formats)
        {
            if (DateTimeOffset.TryParseExact(raw, f, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var exact))
                return exact;
        }

        // 4. 实在认不出来就当没有——日期不重要，不该挡住整个版本加载
        return null;
    }

    /// <summary>把 `...+0000` 改写成 `...+00:00`，负偏移同理。</summary>
    private static string AddColonToOffset(string s)
    {
        // 从尾部往前找符号；偏移固定在末尾 5 位（+hhmm）
        if (s.Length < 5) return s;

        var tail = s[^5..];
        if ((tail[0] == '+' || tail[0] == '-') &&
            char.IsAsciiDigit(tail[1]) && char.IsAsciiDigit(tail[2]) &&
            char.IsAsciiDigit(tail[3]) && char.IsAsciiDigit(tail[4]))
        {
            return string.Concat(s.AsSpan(0, s.Length - 5), tail.AsSpan(0, 3), ":", tail.AsSpan(3));
        }

        return s;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        // 统一写成带冒号的 ISO 8601，保证自己写出去的能被自己读回来
        else writer.WriteStringValue(value.Value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
    }
}