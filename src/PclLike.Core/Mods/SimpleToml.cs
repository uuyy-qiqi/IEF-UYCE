namespace PclLike.Core.Mods;

/// <summary>
/// 极简 TOML 解析器，只覆盖 Forge/NeoForge 的 `mods.toml` 用到的语法。
///
/// 为什么不引 Tomlyn 之类的完整实现：
///   1. `mods.toml` 只用到 TOML 的一小块子集 —— 表、数组表、字符串、布尔。
///      为了读几个字段引一个几万行的库不划算。
///   2. 完整实现会严格报错。但现实中的 mods.toml 有不少手写不规范的地方
///      （多加逗号、引号不配对），严格解析会直接失败，用户看到的就是"Mod 读不出来"。
///      宽容解析更符合这里的实际需求。
///
/// 覆盖的语法：
///   key = "value"
///   key = true / false
///   [table]                  普通表
///   [[array]]                数组表（每次出现追加一个元素）
///   [table.sub]              嵌套表
///   [[a.b]]                  嵌套数组表
///   """多行字符串"""          三引号
///
/// 不覆盖：日期时间、十六进制、内联表 {a=1}、数组值 [1,2]、多行数组。
/// 遇到不认识的行直接跳过 —— 宁可少读几个字段，也不能因为解析失败让整个 Mod 消失。
/// </summary>
public static class SimpleToml
{
    /// <summary>
    /// 解析结果是一棵嵌套的字典树。
    ///
    /// 值的形态：
    ///   string                 普通字符串
    ///   bool                   布尔
    ///   Dictionary&lt;string, object&gt;   表 `[a]`
    ///   List&lt;object&gt;                 数组表 `[[a]]`，元素是 Dictionary
    /// </summary>
    public static Dictionary<string, object> Parse(string text)
    {
        var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var current = root;

        var lines = text.Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = StripComment(lines[i]).Trim();
            if (line.Length == 0) continue;

            // ── 数组表：[[a.b]] ──
            if (line.StartsWith("[[", StringComparison.Ordinal) && line.EndsWith("]]", StringComparison.Ordinal))
            {
                var path = SplitPath(line[2..^2].Trim());
                current = AppendArrayTable(root, path);
                continue;
            }

            // ── 普通表：[a.b] ──
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var path = SplitPath(line[1..^1].Trim());
                current = GetOrCreateTable(root, path);
                continue;
            }

            // ── 键值对 ──
            var eq = FindEquals(line);
            if (eq <= 0) continue;      // 不认识的语法，跳过

            var key = line[..eq].Trim().Trim('"');
            var raw = line[(eq + 1)..].Trim();
            if (key.Length == 0 || raw.Length == 0) continue;

            // 三引号多行字符串：把后续行接到一起
            if (raw.StartsWith("\"\"\"", StringComparison.Ordinal))
            {
                var sb = new System.Text.StringBuilder(raw[3..]);
                var closed = raw.Length > 3 && raw.EndsWith("\"\"\"", StringComparison.Ordinal)
                             && raw.Length >= 6;
                if (closed) sb.Length = raw.Length - 6;

                while (!closed && i + 1 < lines.Length)
                {
                    i++;
                    var next = lines[i];
                    if (next.Contains("\"\"\"", StringComparison.Ordinal))
                    {
                        var idx = next.IndexOf("\"\"\"", StringComparison.Ordinal);
                        sb.Append('\n').Append(next[..idx]);
                        closed = true;
                    }
                    else sb.Append('\n').Append(next);
                }

                current[key] = sb.ToString().Trim();
                continue;
            }

            current[key] = ParseValue(raw);
        }

        return root;
    }

    /// <summary>解析单个值：字符串 / 布尔 / 数字 / 裸词。</summary>
    private static object ParseValue(string raw)
    {
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            return Unescape(raw[1..^1]);

        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
            return raw[1..^1];      // 单引号是字面量，不转义

        if (raw.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (raw.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;

        // 数字（含小数、负号）
        if (double.TryParse(raw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
            return d;

        // 数组值一般出现在不关心的字段里，原样保留字符串即可
        return raw;
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        return s.Replace("\\n", "\n")
                .Replace("\\t", "\t")
                .Replace("\\r", "\r")
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\");
    }

    /// <summary>去掉行尾注释，但不动字符串里的 #。</summary>
    private static string StripComment(string line)
    {
        var inSingle = false;
        var inDouble = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && inDouble) { i++; continue; }   // 跳过转义字符
            if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '#' && !inSingle && !inDouble) return line[..i];
        }
        return line;
    }

    /// <summary>
    /// 找等号。要跳过引号内的等号（`description = "a = b"`）。
    /// </summary>
    private static int FindEquals(string line)
    {
        var inSingle = false;
        var inDouble = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && inDouble) { i++; continue; }
            if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '=' && !inSingle && !inDouble) return i;
        }
        return -1;
    }

    private static string[] SplitPath(string s)
        => s.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Trim('"').Trim('\''))
            .ToArray();

    /// <summary>沿路径找/建普通表。中间遇到数组表就取它的最后一个元素。</summary>
    private static Dictionary<string, object> GetOrCreateTable(Dictionary<string, object> root, string[] path)
    {
        var cur = root;
        foreach (var seg in path)
        {
            if (!cur.TryGetValue(seg, out var v) || v is not Dictionary<string, object> next)
            {
                // 已存在但是数组表 —— 往最后一个元素里钻
                if (v is List<object> arr && arr.Count > 0 && arr[^1] is Dictionary<string, object> last)
                {
                    cur = last;
                    continue;
                }

                next = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                cur[seg] = next;
            }
            cur = next;
        }
        return cur;
    }

    /// <summary>沿路径找/建数组表，并追加一个新元素，返回它。</summary>
    private static Dictionary<string, object> AppendArrayTable(Dictionary<string, object> root, string[] path)
    {
        var cur = root;

        for (var i = 0; i < path.Length - 1; i++)
        {
            var seg = path[i];
            if (!cur.TryGetValue(seg, out var v))
            {
                var t = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                cur[seg] = t;
                cur = t;
            }
            else if (v is Dictionary<string, object> d) cur = d;
            else if (v is List<object> arr && arr.Count > 0 && arr[^1] is Dictionary<string, object> last) cur = last;
            else
            {
                var t = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                cur[seg] = t;
                cur = t;
            }
        }

        var leaf = path[^1];
        if (!cur.TryGetValue(leaf, out var existing) || existing is not List<object> list)
        {
            list = [];
            cur[leaf] = list;
        }

        var item = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        list.Add(item);
        return item;
    }
}