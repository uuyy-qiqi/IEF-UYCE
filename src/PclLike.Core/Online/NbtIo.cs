using System.Buffers.Binary;
using System.Text;

namespace PclLike.Core.Online;

// ══════════════════════════════════════════════════════════════════════════
//  标签模型
// ══════════════════════════════════════════════════════════════════════════

/// <summary>
/// NBT 标签基类。
///
/// **为什么是 record 而不是 class**：NBT 标签是"值"，不是"实体"。同一份
/// servers.dat 读两遍得到的两棵树必须能直接用 `==` 比出相等，否则调用方
/// （以及自测）只能逐字段手写比较，以后每加一个字段就漏一次。record 自带
/// 值相等，只有"成员本身是引用类型"的那几种（三个数组、List、Compound）
/// 需要按内容再比一遍，见各自的 Equals。
/// </summary>
public abstract record NbtTag
{
    /// <summary>
    /// 这个节点在磁盘上的 type id（官方规范 1=Byte … 12=LongArray）。
    /// 构造 <see cref="NbtList"/> 时要告诉它"列表里装的是哪种"，所以放在基类上，
    /// 由每个子类型给出常量，避免调用方到处写魔数。
    /// </summary>
    public abstract byte TagTypeId { get; }
}

/// <summary>TAG_Byte：8 位**有符号**整数。Minecraft 拿它当布尔用（0/1）。</summary>
public sealed record NbtByte(sbyte Value) : NbtTag
{
    public override byte TagTypeId => NbtIo.TagByte;
}

/// <summary>TAG_Short：16 位大端有符号整数。</summary>
public sealed record NbtShort(short Value) : NbtTag
{
    public override byte TagTypeId => NbtIo.TagShort;
}

/// <summary>TAG_Int：32 位大端有符号整数。</summary>
public sealed record NbtInt(int Value) : NbtTag
{
    public override byte TagTypeId => NbtIo.TagInt;
}

/// <summary>TAG_Long：64 位大端有符号整数。</summary>
public sealed record NbtLong(long Value) : NbtTag
{
    public override byte TagTypeId => NbtIo.TagLong;
}

/// <summary>TAG_Float：32 位 IEEE-754 大端。</summary>
public sealed record NbtFloat(float Value) : NbtTag
{
    public override byte TagTypeId => NbtIo.TagFloat;
}

/// <summary>TAG_Double：64 位 IEEE-754 大端。</summary>
public sealed record NbtDouble(double Value) : NbtTag
{
    public override byte TagTypeId => NbtIo.TagDouble;
}

/// <summary>
/// TAG_ByteArray。里面存的是原始字节（存档里的方块数据、图标压缩块），
/// 所以用 byte[] 而不是 string。
/// </summary>
public sealed record NbtByteArray(byte[] Value) : NbtTag
{
    public override byte TagTypeId => NbtIo.TagByteArray;

    /// <summary>
    /// 按内容比，不按引用比。
    ///
    /// 编译器为 record 生成的 Equals 用的是 EqualityComparer&lt;byte[]&gt;.Default，
    /// 也就是**引用相等** —— 那样 `Read(Write(x)) == x` 永远是 false，
    /// 值语义就名存实亡了。这里必须自己写一遍。
    /// </summary>
    public bool Equals(NbtByteArray? other)
        => other is not null && Value.AsSpan().SequenceEqual(other.Value);

    /// <summary>哈希也必须跟着按内容算，否则"相等的两个对象哈希不同"会毒害 HashSet/Dictionary。</summary>
    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var b in Value) h.Add(b);
        return h.ToHashCode();
    }
}

/// <summary>
/// TAG_String：长度前缀（无符号 16 位）+ UTF-8 字节。
///
/// **为什么普通 UTF-8 就够**：官方规范写的是 Java 的 "modified UTF-8"，
/// 它只在两处与标准 UTF-8 不同：U+0000 编成 C0 80、以及 U+FFFF 以上的字符
/// 拆成两个 3 字节的代理对。servers.dat 里的字段（name / ip）全是 ASCII 或
/// 普通中文，既不会有 NUL 也不会有增补平面字符，两种编码逐字节相同。
/// 为一个用不到的分支引入自定义编码器不划算，但这条前提必须写在这里，
/// 免得以后有人拿它去解析真正含增补字符的 NBT（存档里的告示牌就有可能）。
/// </summary>
public sealed record NbtString(string Value) : NbtTag
{
    public override byte TagTypeId => NbtIo.TagString;
}

/// <summary>
/// TAG_List：元素类型统一（<paramref name="TagType"/>）+ 32 位长度 + 元素负载。
///
/// TagType 需要显式给出（空列表也写得出）：规范里它是文件的一部分，
/// 而且 MC 的解析器会拿它去决定怎么读元素。元素负载里**不带**类型字节和名字，
/// 所以一整串不一致的类型会让整份文件变成垃圾 —— 写入时会校验。
/// </summary>
public sealed record NbtList(byte TagType, IReadOnlyList<NbtTag> Items) : NbtTag
{
    public override byte TagTypeId => NbtIo.TagList;

    /// <summary>从元素推断 TagType 建一个列表。空列表按规范写 TagType=0（"无类型"）。</summary>
    public static NbtList Of(params NbtTag[] items)
        => new(items.Length == 0 ? NbtIo.TagEnd : items[0].TagTypeId, items);

    public bool Equals(NbtList? other)
    {
        if (other is null) return false;
        if (Items.Count != other.Items.Count) return false;

        // 逐元素比。元素自己也是 record，值语义已经写好了，这里直接 == 即可
        for (var i = 0; i < Items.Count; i++)
            if (Items[i] != other.Items[i]) return false;

        return true;
    }

    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var item in Items) h.Add(item);
        return h.ToHashCode();
    }
}

/// <summary>
/// TAG_Compound：有序的"名字 → 标签"映射。
///
/// 用 <see cref="IReadOnlyDictionary{TKey,TValue}"/> 而不是 Dictionary，
/// 是为了不让调用方在写入过程中偷偷改掉内容（写盘和内存不一致最难查）。
/// 新增键走 <see cref="With"/>，返回新对象。
/// </summary>
public sealed record NbtCompound(IReadOnlyDictionary<string, NbtTag> Value) : NbtTag
{
    public override byte TagTypeId => NbtIo.TagCompound;

    /// <summary>取一个子标签，没有就返回 null（不抛）—— 损坏的 servers.dat 少字段是常态。</summary>
    public NbtTag? this[string name] => Value.TryGetValue(name, out var tag) ? tag : null;

    /// <summary>取字符串字段；类型不对或不存在都返回 null，省得调用方层层强转。</summary>
    public string? GetString(string name) => this[name] is NbtString s ? s.Value : null;

    /// <summary>取整数字段；不存在/类型不符返回 null（用 -1 之类的哨兵值会掩盖错误）。</summary>
    public int? GetInt(string name) => this[name] is NbtInt i ? i.Value : null;

    /// <summary>
    /// 复制一份并覆盖若干键，**保留**其它键。
    ///
    /// 这是给 servers.dat 这类"别人也往里写东西"的文件用的：只改自己认识的字段，
    /// 不认识的（服务器图标 icon、以后 MC 新增的开关）原样留着。整份重建的写法
    /// 会把用户的图标悄悄删掉，事后完全没法追。
    /// </summary>
    public NbtCompound With(params (string Name, NbtTag Tag)[] entries)
    {
        var copy = new Dictionary<string, NbtTag>(Value, StringComparer.Ordinal);
        foreach (var (name, tag) in entries) copy[name] = tag;
        return new NbtCompound(copy);
    }

    /// <summary>按名字建一个复合标签，可读性比手写 Dictionary 好。</summary>
    public static NbtCompound Of(params (string Name, NbtTag Tag)[] entries)
    {
        var map = new Dictionary<string, NbtTag>(entries.Length, StringComparer.Ordinal);
        foreach (var (name, tag) in entries) map[name] = tag;
        return new NbtCompound(map);
    }

    public bool Equals(NbtCompound? other)
    {
        if (other is null) return false;
        if (Value.Count != other.Value.Count) return false;

        foreach (var (key, tag) in Value)
        {
            // 键名是 Ordinal 比的：NBT 的名字是字节串，不区分大小写是错的
            if (!other.Value.TryGetValue(key, out var theirs)) return false;
            if (tag != theirs) return false;
        }

        return true;
    }

    /// <summary>哈希与字典的遍历顺序无关（按键名排序后再算），否则"相等但哈希不同"。</summary>
    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var key in Value.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            h.Add(key);
            h.Add(Value[key]);
        }

        return h.ToHashCode();
    }
}

/// <summary>TAG_IntArray：长度前缀 + 若干 32 位大端整数。</summary>
public sealed record NbtIntArray(int[] Value) : NbtTag
{
    public override byte TagTypeId => NbtIo.TagIntArray;

    public bool Equals(NbtIntArray? other)
        => other is not null && Value.AsSpan().SequenceEqual(other.Value);

    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var v in Value) h.Add(v);
        return h.ToHashCode();
    }
}

/// <summary>TAG_LongArray：长度前缀 + 若干 64 位大端整数。</summary>
public sealed record NbtLongArray(long[] Value) : NbtTag
{
    public override byte TagTypeId => NbtIo.TagLongArray;

    public bool Equals(NbtLongArray? other)
        => other is not null && Value.AsSpan().SequenceEqual(other.Value);

    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var v in Value) h.Add(v);
        return h.ToHashCode();
    }
}

// ══════════════════════════════════════════════════════════════════════════
//  读写
// ══════════════════════════════════════════════════════════════════════════

/// <summary>
/// 最小 NBT 读写器。
///
/// **范围**：只做 Minecraft 实际会用到的"未压缩 NBT"（gzip 之外的裸格式），
/// 也就是 `servers.dat` 那一种。不带 zlib/gzip 包装 —— 那是调用方的事，
/// 混进来只会让"文件损坏"和"不是压缩的"两种失败无法区分。
///
/// **为什么手写而不上库**：整个启动器只用得到这一个格式的一小块，BCL 里没有
/// NBT，引一个第三方包为几十行代码不值得（而且项目约定不加 NuGet 依赖）。
///
/// **字节序**：NBT 全是大端（网络序）。用 <see cref="BinaryPrimitives"/>
/// 显式写大端，不用 BitConverter —— 后者跟着机器走，在小端以外的机器上会静默出错。
///
/// **健壮性**：所有长度字段都先跟"剩余字节数"对账再分配内存。损坏的 servers.dat
/// 到处都是（半截下载、磁盘坏道），一个声称自己有 20 亿条元素的列表不能让我们
/// 直接 new 一个 20 亿长的 List 把启动器拖死。
/// </summary>
public static class NbtIo
{
    // 官方规范的类型 id。写成常量，让构造 NbtList / 判断类型时不用记数字
    public const byte TagEnd = 0;
    public const byte TagByte = 1;
    public const byte TagShort = 2;
    public const byte TagInt = 3;
    public const byte TagLong = 4;
    public const byte TagFloat = 5;
    public const byte TagDouble = 6;
    public const byte TagByteArray = 7;
    public const byte TagString = 8;
    public const byte TagList = 9;
    public const byte TagCompound = 10;
    public const byte TagIntArray = 11;
    public const byte TagLongArray = 12;

    /// <summary>
    /// 嵌套深度上限。
    ///
    /// 解析是递归的，而 NBT 允许复合里套复合。一个恶意/损坏的文件可以构造出
    /// 上百万层嵌套，直接把线程栈打爆（StackOverflow 在 .NET 里不可捕获，
    /// 整个进程立刻死）。512 层远超任何真实存档，撞上就一定不是正常数据。
    /// </summary>
    private const int MaxDepth = 512;

    /// <summary>
    /// 解析未压缩 NBT，返回根 TAG_Compound（名字→标签字典）。
    ///
    /// 数据不完整、根不是 Compound、类型 id 不认识都会抛 <see cref="InvalidDataException"/>；
    /// 需要"坏了就当空的"语义请看 <see cref="ServerListFile.Read"/>，它负责兜住异常。
    /// </summary>
    public static NbtCompound Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var reader = new Reader(data);

        var rootType = reader.ReadByte();
        if (rootType != TagCompound)
            throw new InvalidDataException($"未压缩 NBT 的根必须是 TAG_Compound(10)，实际是 {rootType}");

        // 根节点的名字：Minecraft 写的是空串，读出来没有任何用处（文件里也只有一份根），
        // 所以读掉就丢 —— 这和"根 Compound 就是字典本身"的模型是一致的
        _ = reader.ReadString();

        if (reader.ReadPayload(TagCompound, 0) is not NbtCompound root)
            throw new InvalidDataException("根节点的负载不是 TAG_Compound");

        return root;
    }

    /// <summary>序列化成未压缩 NBT（根写 TAG_Compound + 空名字，与游戏一致）。</summary>
    public static byte[] Write(NbtCompound root)
    {
        ArgumentNullException.ThrowIfNull(root);

        using var ms = new MemoryStream();
        ms.WriteByte(TagCompound);
        WriteStringRaw(ms, "");
        WritePayload(ms, root);
        return ms.ToArray();
    }

    // ─────────────────────────── 写 ───────────────────────────

    private static void WritePayload(Stream s, NbtTag tag)
    {
        switch (tag)
        {
            case NbtByte t:
                s.WriteByte(unchecked((byte)t.Value));
                break;

            case NbtShort t:
                WriteI16(s, t.Value);
                break;

            case NbtInt t:
                WriteI32(s, t.Value);
                break;

            case NbtLong t:
                WriteI64(s, t.Value);
                break;

            case NbtFloat t:
                WriteF32(s, t.Value);
                break;

            case NbtDouble t:
                WriteF64(s, t.Value);
                break;

            case NbtByteArray t:
                WriteI32(s, t.Value.Length);
                s.Write(t.Value);
                break;

            case NbtString t:
                WriteStringRaw(s, t.Value);
                break;

            case NbtList t:
                WriteList(s, t);
                break;

            case NbtCompound t:
                WriteCompound(s, t);
                break;

            case NbtIntArray t:
                WriteI32(s, t.Value.Length);
                foreach (var v in t.Value) WriteI32(s, v);
                break;

            case NbtLongArray t:
                WriteI32(s, t.Value.Length);
                foreach (var v in t.Value) WriteI64(s, v);
                break;

            default:
                throw new ArgumentException($"写不出来的 NBT 标签类型：{tag.GetType().Name}", nameof(tag));
        }
    }

    private static void WriteCompound(Stream s, NbtCompound compound)
    {
        foreach (var (name, tag) in compound.Value)
        {
            s.WriteByte(tag.TagTypeId);
            WriteStringRaw(s, name);
            WritePayload(s, tag);
        }

        // TAG_End 是复合的结束标记。少了它，游戏会把后面的字节当新字段读，
        // 整个文件直接报废 —— 所以这个 0 不能省
        s.WriteByte(TagEnd);
    }

    private static void WriteList(Stream s, NbtList list)
    {
        s.WriteByte(list.TagType);
        WriteI32(s, list.Items.Count);

        foreach (var item in list.Items)
        {
            // 列表元素负载里不带类型字节，读的时候完全依赖头的 TagType。
            // 类型对不上就写出一份游戏读不了的文件，宁可在写之前就炸掉
            if (item.TagTypeId != list.TagType)
                throw new ArgumentException(
                    $"列表声明元素类型是 {list.TagType}，却塞进了 {item.TagTypeId}（{item.GetType().Name}）", nameof(list));

            WritePayload(s, item);
        }
    }

    /// <summary>
    /// 写字符串：无符号 16 位长度 + UTF-8 字节（modified UTF-8 的近似，理由见 <see cref="NbtString"/>）。
    /// </summary>
    private static void WriteStringRaw(Stream s, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? "");

        // 长度字段只有 2 字节，超长字符串没有合法表示。截断会写出游戏读不了的文件，
        // 所以这里必须抛 —— 而且真撞上说明调用方把整块数据当字符串塞了进来
        if (bytes.Length > ushort.MaxValue)
            throw new ArgumentException($"NBT 字符串最长 {ushort.MaxValue} 字节，实际 {bytes.Length} 字节");

        WriteU16(s, (ushort)bytes.Length);
        s.Write(bytes);
    }

    private static void WriteU16(Stream s, ushort v)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, v);
        s.Write(buf);
    }

    private static void WriteI16(Stream s, short v)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(buf, v);
        s.Write(buf);
    }

    private static void WriteI32(Stream s, int v)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, v);
        s.Write(buf);
    }

    private static void WriteI64(Stream s, long v)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buf, v);
        s.Write(buf);
    }

    private static void WriteF32(Stream s, float v)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteSingleBigEndian(buf, v);
        s.Write(buf);
    }

    private static void WriteF64(Stream s, double v)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(buf, v);
        s.Write(buf);
    }

    // ─────────────────────────── 读 ───────────────────────────

    /// <summary>
    /// 顺序读字节的小游标。
    ///
    /// 用 class 而不是 ref struct：读是递归的（复合套列表套复合），
    /// ref struct 不能当普通引用在递归调用间传，位置状态没法共享。
    /// </summary>
    private sealed class Reader
    {
        private readonly byte[] _data;
        private int _pos;

        public Reader(byte[] data) => _data = data;

        private int Remaining => _data.Length - _pos;

        /// <summary>确认后面还有 n 个字节，否则说明文件被截断。</summary>
        private void Need(int n)
        {
            if (n < 0 || Remaining < n)
                throw new InvalidDataException($"NBT 数据不完整：还需要 {n} 字节，只剩 {Remaining} 字节");
        }

        public byte ReadByte()
        {
            Need(1);
            return _data[_pos++];
        }

        private short ReadI16()
        {
            Need(2);
            var v = BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(_pos));
            _pos += 2;
            return v;
        }

        private int ReadI32()
        {
            Need(4);
            var v = BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(_pos));
            _pos += 4;
            return v;
        }

        public string ReadString()
        {
            var len = ReadU16();
            Need(len);
            var s = Encoding.UTF8.GetString(_data, _pos, len);
            _pos += len;
            return s;
        }

        private ushort ReadU16()
        {
            Need(2);
            var v = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(_pos));
            _pos += 2;
            return v;
        }

        /// <summary>
        /// 按类型 id 读一段负载（不含类型字节，也不含名字）。
        /// <paramref name="depth"/> 是当前嵌套层数，只用来挡住栈溢出。
        /// </summary>
        public NbtTag ReadPayload(byte type, int depth)
        {
            if (depth > MaxDepth)
                throw new InvalidDataException($"NBT 嵌套超过 {MaxDepth} 层，判定为损坏数据");

            switch (type)
            {
                case TagByte:
                    return new NbtByte(unchecked((sbyte)ReadByte()));

                case TagShort:
                    return new NbtShort(ReadI16());

                case TagInt:
                    return new NbtInt(ReadI32());

                case TagLong:
                    Need(8);
                    var l = BinaryPrimitives.ReadInt64BigEndian(_data.AsSpan(_pos));
                    _pos += 8;
                    return new NbtLong(l);

                case TagFloat:
                    Need(4);
                    var f = BinaryPrimitives.ReadSingleBigEndian(_data.AsSpan(_pos));
                    _pos += 4;
                    return new NbtFloat(f);

                case TagDouble:
                    Need(8);
                    var d = BinaryPrimitives.ReadDoubleBigEndian(_data.AsSpan(_pos));
                    _pos += 8;
                    return new NbtDouble(d);

                case TagByteArray:
                    // 每个元素至少 1 字节，用剩余字节数当上界，避免畸形长度把我们撑爆
                    var byteLen = GuardedCount(1);
                    Need(byteLen);
                    var bytes = new byte[byteLen];
                    Array.Copy(_data, _pos, bytes, 0, byteLen);
                    _pos += byteLen;
                    return new NbtByteArray(bytes);

                case TagString:
                    return new NbtString(ReadString());

                case TagList:
                    return ReadList(depth);

                case TagCompound:
                    return ReadCompound(depth);

                case TagIntArray:
                    var intLen = GuardedCount(4);
                    var ints = new int[intLen];
                    for (var i = 0; i < intLen; i++) ints[i] = ReadI32();
                    return new NbtIntArray(ints);

                case TagLongArray:
                    var longLen = GuardedCount(8);
                    var longs = new long[longLen];
                    for (var i = 0; i < longLen; i++)
                    {
                        Need(8);
                        longs[i] = BinaryPrimitives.ReadInt64BigEndian(_data.AsSpan(_pos));
                        _pos += 8;
                    }

                    return new NbtLongArray(longs);

                default:
                    throw new InvalidDataException($"不认识的 NBT 类型 id：{type}");
            }
        }

        /// <summary>
        /// 读长度前缀并校验它是不是"物理上装得下"。
        ///
        /// 这一步是纯粹的自保：长度字段是 int，畸形数据可以声称 21 亿个元素。
        /// 因为每个元素至少占 <paramref name="minBytesPerItem"/> 字节，
        /// 长度乘以最小尺寸超过剩余字节数就一定不是合法文件。校验之后再分配，
        /// 内存开销就被真实文件大小兜住了。
        /// </summary>
        private int GuardedCount(int minBytesPerItem)
        {
            var count = ReadI32();

            if (count < 0)
                throw new InvalidDataException($"NBT 长度为负：{count}");

            if (count > Remaining / minBytesPerItem)
                throw new InvalidDataException(
                    $"NBT 声明有 {count} 个元素，但只剩 {Remaining} 字节（每个至少 {minBytesPerItem} 字节）");

            return count;
        }

        private NbtList ReadList(int depth)
        {
            var elementType = ReadByte();
            var count = GuardedCount(1);

            // 空列表：元素类型在规范里没有意义（真实文件里写 0），长度 0 就是空，
            // 不去校验 elementType —— 有些实现写的就是 0，为此判整份文件损坏太严
            if (count == 0) return new NbtList(TagEnd, []);

            // 类型 id 提前校验：不然每个元素都要试一次 switch，错了才发现
            if (elementType is TagEnd or > TagLongArray)
                throw new InvalidDataException($"NBT 列表的元素类型 id 非法：{elementType}");

            var items = new List<NbtTag>(count);
            for (var i = 0; i < count; i++) items.Add(ReadPayload(elementType, depth + 1));

            return new NbtList(elementType, items);
        }

        private NbtCompound ReadCompound(int depth)
        {
            var items = new Dictionary<string, NbtTag>(StringComparer.Ordinal);

            while (true)
            {
                var type = ReadByte();

                // TAG_End 收尾
                if (type == TagEnd) break;

                if (type > TagLongArray)
                    throw new InvalidDataException($"复合标签里出现非法类型 id：{type}");

                var name = ReadString();

                // 重名：后写的覆盖先写的，和 Minecraft 自己的读法一致（它也是往 map 里塞）
                items[name] = ReadPayload(type, depth + 1);
            }

            return new NbtCompound(items);
        }
    }
}