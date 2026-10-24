using System.Globalization;
using System.Text;

namespace PclLike.Core.Online;

/// <summary>
/// 大厅编号：把 `IP:端口` 编成一串能口头念、能手抄的短码。
///
/// **为什么需要它**：联机大厅的场景是"我把世界开在局域网上，你把编号念给我"。
/// 让人念 `192.168.1.7:25565` 要报 13 个数字加两个符号，抄错一位就白折腾；
/// 而 48 bit（IPv4 4 字节 + 端口 2 字节）用 base32 恰好 10 个字符装得下，
/// 加一位校验字符正好 11 个，念起来是三个音节组。
///
/// **为什么用 Crockford base32 而不是标准 base32**：Crockford 的字母表
/// （<see cref="Alphabet"/>）故意去掉了 I、L、O、U —— 手写时 1/I/L 和 0/O
/// 分不清，U 容易和 V 混。于是解码端可以大胆地把 O→0、I/L→1、U→V 自动纠正，
/// 用户抄错大小写或者把 l 当成 1 都还能解开。
///
/// **校验字符**：48 bit 的 6 个字节求和模 32。它便宜（不用查表不用多项式），
/// 能挡住绝大多数手抄错误 —— 但要诚实说明它的极限：当某个字节被改动的幅度
/// 恰好是 32 的整数倍、且没有波及其它字节时，和模 32 不变，会漏检。
/// 这种编号是给口头传达用的便利设施，不是完整性校验，真要防篡改得签名。
/// </summary>
public static class LobbyCode
{
    /// <summary>Crockford base32 字母表：没有 I / L / O / U（都是易混字符）。</summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>承载数据的字符数：48 bit / 5 bit = 9.6，向上取整 10（首字符只用了 3 bit）。</summary>
    private const int PayloadSymbols = 10;

    /// <summary>数据 + 校验，共 11 个字符。</summary>
    private const int TotalSymbols = 11;

    /// <summary>
    /// 把 IPv4 地址和端口编成 `XXXX-XXXX-XXX` 形式的编号。
    ///
    /// 地址必须是标准点分四段的 IPv4：**域名不行**（编号只有 48 bit，塞不下一串名字），
    /// 调用方想用域名得先自己解析。端口必须在 1..65535。
    /// 参数不合法直接抛 <see cref="ArgumentException"/> —— 编出一个解不开的编号，
    /// 比当场报错难查得多。
    /// </summary>
    public static string Encode(string ip, int port)
    {
        if (!TryParseIpv4(ip, out var bytes))
            throw new ArgumentException($"必须是点分四段的 IPv4 地址：{ip}", nameof(ip));

        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port, "端口必须在 1..65535");

        var value = Pack(bytes, port);

        var sb = new StringBuilder(TotalSymbols);

        // 从高位到低位每 5 bit 取一个字符。首字符只有 3 bit 有效（48 = 3 + 9×5），
        // 所以它永远不会是字母表里后 3/4 的字符 —— 这不是 bug，是位数决定的
        for (var shift = (PayloadSymbols - 1) * 5; shift >= 0; shift -= 5)
            sb.Append(Alphabet[(int)((value >> shift) & 31)]);

        sb.Append(Alphabet[CheckSymbol(value)]);

        var code = sb.ToString();
        return $"{code[..4]}-{code[4..8]}-{code[8..]}";
    }

    /// <summary>
    /// 解析编号。非法一律返回 null（不抛）—— 用户随时会念错，报错只该是界面上一行提示。
    ///
    /// 容忍：小写、多余/缺失的连字符、空格、以及 O/I/L/U 与 0/1/V 的混淆。
    /// 不容忍：长度不对（补零猜不了）、字母表外的字符（说明念错的不止一个字符）、
    /// 校验字符不符、端口为 0。
    /// </summary>
    public static (string Ip, int Port)? Decode(string code)
    {
        if (code is null) return null;

        var normalized = new StringBuilder(TotalSymbols);

        foreach (var raw in code)
        {
            // 分隔符和空白随便它怎么打。连字符是给人分段用的，不承载信息
            if (raw is '-' or '_' || char.IsWhiteSpace(raw)) continue;

            var c = char.ToUpperInvariant(raw);

            // Crockford 的标准容错：最容易抄错的四个字符直接映射回真身
            c = c switch
            {
                'O' => '0',
                'I' => '1',
                'L' => '1',
                'U' => 'V',
                _ => c,
            };

            var index = Alphabet.IndexOf(c);

            // 不在字母表里 = 念错了，而且没法猜它本来是什么
            if (index < 0) return null;

            normalized.Append(Alphabet[index]);
        }

        if (normalized.Length != TotalSymbols) return null;

        // 前 10 个字符是数据，最后一个是校验
        ulong value = 0;
        for (var i = 0; i < PayloadSymbols; i++)
            value = (value << 5) | (uint)Alphabet.IndexOf(normalized[i]);

        // 10 个字符能装 50 bit，但只有低 48 bit 是数据。高位非零说明这不是
        // 我们编出来的编号（比如用户把两段码接在了一起），直接判非法
        if ((value >> 48) != 0) return null;

        if (Alphabet.IndexOf(normalized[PayloadSymbols]) != CheckSymbol(value)) return null;

        var port = (int)(value & 0xFFFF);

        // 端口 0 不是可用端点，谁也不会连上它
        if (port == 0) return null;

        var ip = $"{(value >> 40) & 0xFF}.{(value >> 32) & 0xFF}.{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}";
        return (ip, port);
    }

    /// <summary>
    /// IPv4 4 字节 + 端口 2 字节（大端）= 48 bit。
    ///
    /// **为什么每个操作数都先落到 byte 再转 ulong**：`port` 是 int（有符号），
    /// 直接写 `(ulong)((port >> 8) & 0xFF)` 会让编译器看到"有符号数扩宽后参与按位或"，
    /// 报 CS0675 —— 而且这不是纯风格问题：只要有一步忘了 `&amp; 0xFF` 的截断，
    /// 负数端口就会带着高位的 1 整片糊进地址段，编出来的码看着正常但解不回原值。
    /// 先 `(byte)` 截断再转无符号，位数确定，谁读都不会误解。
    /// </summary>
    private static ulong Pack(byte[] ip, int port)
    {
        ulong value = (ulong)ip[0] << 40;
        value |= (ulong)ip[1] << 32;
        value |= (ulong)ip[2] << 24;
        value |= (ulong)ip[3] << 16;
        value |= (ulong)(byte)(port >> 8) << 8;
        value |= (byte)port;
        return value;
    }

    /// <summary>校验字符：48 bit 拆成 6 个字节求和，模 32 落在字母表上。</summary>
    private static int CheckSymbol(ulong value)
    {
        var sum = 0;
        for (var i = 0; i < 6; i++) sum += (int)((value >> (i * 8)) & 0xFF);
        return sum % 32;
    }

    /// <summary>
    /// 严格解析点分四段 IPv4。
    ///
    /// 不用 <c>IPAddress.TryParse</c>：它会接受 `1.2`、`0x7f.1`、`127.1` 这类简写，
    /// 用户十有八九是漏打了一段。虽然它们编出来的字节是一样的，但把"打错了"
    /// 当成正常输入，用户就会拿到一个自己以为写对了的地址，连不上也找不到原因。
    /// </summary>
    private static bool TryParseIpv4(string? text, out byte[] bytes)
    {
        bytes = [];

        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Trim().Split('.');
        if (parts.Length != 4) return false;

        var parsed = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            // NumberStyles.None：只收纯数字，带正负号/空格/千分位的一律不认
            if (!byte.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out parsed[i]))
                return false;
        }

        bytes = parsed;
        return true;
    }
}