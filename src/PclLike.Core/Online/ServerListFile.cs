namespace PclLike.Core.Online;

/// <summary>
/// 服务器列表里的一条。
///
/// <paramref name="Ip"/> 原样保留用户在游戏里填的字符串：它可以是
/// `host`（端口默认 25565），也可以是 `host:port`，还可以是纯 IPv4 或域名。
/// **不做归一化** —— 把 `host` 补成 `host:25565` 或者反过来把端口剥掉，
/// 都会让用户明明没改过的东西看起来变了，而且 DNS/SRV 记录那套逻辑在游戏侧，
/// 启动器猜错了只会更糟。所以"同一条"的判定就是这一串文本比出来相等。
///
/// <paramref name="AcceptTextures"/> 对应游戏界面上的"允许服务器使用资源包"。
/// </summary>
public sealed record ServerEntry(string Name, string Ip, bool AcceptTextures = false);

/// <summary>
/// `<gameDir>/servers.dat` 的读写。
///
/// **为什么直接改 NBT 而不是"读成条目 → 整份重写"**：servers.dat 里每条服务器
/// 除了 name/ip/acceptTextures，还可能有游戏写入的**服务器图标**（icon，一段
/// base64）以及以后版本新增的字段。如果我们只认出三个字段再把列表重新拼一遍，
/// 用户自己设的图标会在第一次"添加服务器"之后无声消失，而且没有任何迹象。
/// 所以这里的做法是：把原始 NBT 读出来，只在需要的那几个键上做增删改，
/// 其余原样带回去写。
///
/// **不抛异常**：这个文件由游戏和我们共同维护，随时可能被游戏重写、被别的启动器
/// 改坏、或者只写了一半。列表读不出来最多是"界面空了"，让异常冒到 GUI 上崩掉
/// 是不划算的 —— 所以读一律返回"能读出来的部分"，写失败返回"没改成功"。
/// </summary>
public static class ServerListFile
{
    private const string ServersKey = "servers";
    private const string NameKey = "name";
    private const string IpKey = "ip";
    private const string AcceptTexturesKey = "acceptTextures";

    /// <summary>
    /// 读 &lt;gameDir&gt;/servers.dat。
    ///
    /// 文件不存在、不是合法 NBT、某个条目字段缺失或类型不对，都会跳过而不是抛。
    /// 缺 ip 的条目直接丢掉：那种条目在界面上点一下也连不上，列出来只会让人困惑。
    /// </summary>
    public static List<ServerEntry> Read(string path)
    {
        var root = LoadRoot(path);
        var raw = ServersOf(root);

        var result = new List<ServerEntry>(raw.Count);
        foreach (var entry in raw)
        {
            var ip = entry.GetString(IpKey)?.Trim();

            // 没 ip 或类型不是字符串 —— 点了也连不上，跳过
            if (string.IsNullOrEmpty(ip)) continue;

            // acceptTextures 是 TAG_Byte。缺字段时默认 false。
            // 注意不能写成 `(x as NbtByte)?.Value != 0`：可空比较里 null != 0 是 true，
            // 那样缺字段会被当成"已勾选"，正好反了
            var accept = entry[AcceptTexturesKey] is NbtByte b && b.Value != 0;

            result.Add(new ServerEntry(entry.GetString(NameKey) ?? "", ip, accept));
        }

        return result;
    }

    /// <summary>
    /// 添加或更新一条，写回 servers.dat，返回**最终（磁盘上）的条目数**。
    ///
    /// 同一个 ip（忽略大小写，别的字段不管）视为同一条，整条替换 ——
    /// 名字和"允许资源包"开关一起更新，因为调用方拿来的是用户在对话框里
    /// 确认过的完整条目，只改名字会让勾选框停留在旧状态。
    ///
    /// 写盘失败时返回**读得到的原有条数**（磁盘内容没变），不会假装写成功。
    /// </summary>
    public static int AddOrUpdate(string path, ServerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var ip = (entry.Ip ?? "").Trim();

        // 空 ip 写进去就是一条永远连不上的死条目，不如不写
        if (ip.Length == 0) return Read(path).Count;

        var root = LoadRoot(path);
        var raw = ServersOf(root);

        var name = entry.Name ?? "";
        var index = IndexOfIp(raw, ip);

        if (index >= 0)
        {
            // 更新：在原有条目上覆盖三个键，图标等其它字段留在原地
            raw[index] = raw[index].With(
                (NameKey, new NbtString(name)),
                (IpKey, new NbtString(ip)),
                (AcceptTexturesKey, new NbtByte(entry.AcceptTextures ? (sbyte)1 : (sbyte)0)));
        }
        else
        {
            raw.Add(NbtCompound.Of(
                (NameKey, new NbtString(name)),
                (IpKey, new NbtString(ip)),
                (AcceptTexturesKey, new NbtByte(entry.AcceptTextures ? (sbyte)1 : (sbyte)0))));
        }

        // 先算出目标内容，再落地。写失败就把"磁盘上真实条数"报回去
        var saved = SaveRoot(path, WithServers(root, raw));
        return saved ? raw.Count : Read(path).Count;
    }

    /// <summary>
    /// 删掉 ip 相同的一条，返回是否**真的删掉了**。
    ///
    /// ip 不存在返回 false；写盘失败也返回 false —— 磁盘上还在，报 true 会让界面
    /// 把条目划掉，下次启动又冒出来。
    /// </summary>
    public static bool Remove(string path, string ip)
    {
        var target = (ip ?? "").Trim();
        if (target.Length == 0) return false;

        var root = LoadRoot(path);
        var raw = ServersOf(root);

        var index = IndexOfIp(raw, target);
        if (index < 0) return false;

        raw.RemoveAt(index);
        return SaveRoot(path, WithServers(root, raw));
    }

    // ─────────────────────────── 内部 ───────────────────────────

    /// <summary>
    /// ip 比较用忽略大小写：域名不区分大小写（`MC.Example.com` 和 `mc.example.com`
    /// 是同一台机器），用户手输时大小写很随意。中括号（IPv6 字面量）也在比之前去掉，
    /// 免得 `[::1]` 和 `::1` 被当成两条。
    /// </summary>
    private static int IndexOfIp(List<NbtCompound> raw, string ip)
    {
        for (var i = 0; i < raw.Count; i++)
        {
            var existing = raw[i].GetString(IpKey)?.Trim();
            if (existing is null) continue;
            if (string.Equals(existing, ip, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }

    /// <summary>
    /// 读出根 Compound。文件不存在或坏掉都返回一个空 Compound，
    /// 让调用方能拿它当"干净起点"继续写（用户第一次加服务器时文件本来就不存在）。
    /// </summary>
    private static NbtCompound LoadRoot(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return EmptyRoot();

            return NbtIo.Read(File.ReadAllBytes(path));
        }
        catch
        {
            // 损坏、被占用、权限不足 —— 都当成"还没有列表"。真正的读写错误会在
            // 写回时以返回值的形式暴露，不会在这里静默吞掉用户的操作结果
            return EmptyRoot();
        }
    }

    private static NbtCompound EmptyRoot()
        => new(new Dictionary<string, NbtTag>(StringComparer.Ordinal));

    /// <summary>取根里的 servers 列表（元素类型是 Compound）。</summary>
    private static List<NbtCompound> ServersOf(NbtCompound root)
    {
        var result = new List<NbtCompound>();
        if (root[ServersKey] is not NbtList list) return result;

        foreach (var item in list.Items)
        {
            // 不是 Compound 的元素跳过：一个坏元素不该让整份列表作废
            if (item is NbtCompound compound) result.Add(compound);
        }

        return result;
    }

    /// <summary>
    /// 把新列表装回根，**其它键不动**（保留游戏可能写进去的东西，理由见类注释）。
    /// 写回去时元素类型固定 10=Compound，与游戏一致。
    /// </summary>
    private static NbtCompound WithServers(NbtCompound root, List<NbtCompound> servers)
    {
        var items = new List<NbtTag>(servers.Count);
        foreach (var server in servers) items.Add(server);

        return root.With((ServersKey, new NbtList(NbtIo.TagCompound, items)));
    }

    /// <summary>
    /// 原子落盘：先写 .tmp 再改名。
    ///
    /// 游戏可能正开着、随时会把 servers.dat 重写一遍；直接原地截断再写，
    /// 万一中途失败就留下半份文件，用户整个服务器列表没了。改名是原子的，
    /// 最坏情况也只是这次改动没生效。
    /// </summary>
    private static bool SaveRoot(string path, NbtCompound root)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            // servers.dat 就躺在 gameDir 里，正常情况下它已经存在；
            // 手动指定了别的路径时顺手建出父目录，省得调用方自己拼
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, NbtIo.Write(root));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch
        {
            // 改名失败的话临时文件别留在 gameDir 里（游戏会把它当成垃圾）
            try { File.Delete(path + ".tmp"); } catch { /* 尽力而为 */ }
            return false;
        }
    }
}