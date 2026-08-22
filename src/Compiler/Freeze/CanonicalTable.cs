using System.Collections.Generic;
using System.Text;
using FairyNext.Numerics;

namespace FairyNext.Compiler.Freeze;

/// <summary>
/// 定长记录的 canonical 去重表（编译平面机制 10）：**字节等同的记录必得同一个 id**。
/// 去重不只省体积——「id 相等 ⇔ 内容相等」让运行时的内容比较降为整数比较，也让缓存键
/// 可以直接用 id；同时它是可断言的性质（不变量 8 的编译后置扫描比的就是这条）。
///
/// 只对**按 id 引用**的表用（STRT / CONT / TREF）。按**区间**引用的冻结流表
/// （QUAD/SEGS/LEAF/CLIP）不去重：那些表的身份是「组件的连续区间」，共享一条记录会打散
/// 区间，而区间连续正是实例化 memcpy 与 route 位宽的前提——两种身份互斥，各表选一种。
///
/// 确定性：id = 首次登记序。同一份输入两次编译登记序相同 ⇒ id 相同 ⇒ blob 逐字节相同
/// （编译产物 golden 的前置）。哈希只用来找候选，判等一律逐字节——哈希碰撞不会把两条
/// 不同记录合并成一个 id。
/// </summary>
internal sealed class CanonicalTable
{
    private readonly int _stride;
    private readonly List<byte[]> _records = new List<byte[]>();
    private readonly Dictionary<ulong, List<int>> _byHash = new Dictionary<ulong, List<int>>();

    /// <summary>建表（<paramref name="stride"/> = 记录字节宽）。</summary>
    public CanonicalTable(int stride)
    {
        if (stride <= 0) throw new ArgumentOutOfRangeException(nameof(stride));
        _stride = stride;
    }

    /// <summary>记录宽。</summary>
    public int Stride => _stride;

    /// <summary>去重后的记录数。</summary>
    public int Count => _records.Count;

    /// <summary>去重**前**的登记次数（内存计划的分母：Count/Inserts 就是去重率）。</summary>
    public int Inserts { get; private set; }

    /// <summary>登记一条记录，返回 canonical id。</summary>
    public int Add(ReadOnlySpan<byte> record)
    {
        if (record.Length != _stride)
            throw new ArgumentException("记录宽不符（" + record.Length + " != " + _stride + "）", nameof(record));
        Inserts++;
        ulong h = FnvHash.Hash64(record);
        if (_byHash.TryGetValue(h, out List<int>? bucket))
        {
            for (int i = 0; i < bucket.Count; i++)
                if (record.SequenceEqual(_records[bucket[i]])) return bucket[i];
        }
        else
        {
            bucket = new List<int>(1);
            _byHash.Add(h, bucket);
        }
        int id = _records.Count;
        _records.Add(record.ToArray());
        bucket.Add(id);
        return id;
    }

    /// <summary>读一条记录。</summary>
    public ReadOnlySpan<byte> At(int id) => _records[id];

    /// <summary>整段 payload（记录按 id 序拼接）。</summary>
    public byte[] ToPayload()
    {
        byte[] payload = new byte[_records.Count * _stride];
        for (int i = 0; i < _records.Count; i++)
            _records[i].CopyTo(payload, i * _stride);
        return payload;
    }

    /// <summary>
    /// 后置扫描（不变量 8）：表内不得有两条字节等同却 id 不同的记录。
    /// 按构造不可能发生——这道扫描存在是因为「不可能」是关于当前实现的断言，
    /// 而不变量是关于产物的断言，两者必须各有一次检查。
    /// </summary>
    public bool VerifyDistinct(out string error)
    {
        // 按字典序排下标再比相邻两条：O(n log n)，且**不复用** <see cref="Add"/> 的哈希桶——
        // 用被检查者自己的索引结构去检查它，等于让缺陷同时污染证据。
        // （逐对比较是 O(n²)：几十条时无所谓，一个真项目的包会把编译时间吃掉。）
        var order = new int[_records.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => Compare(_records[a], _records[b]));
        for (int k = 1; k < order.Length; k++)
        {
            if (Compare(_records[order[k - 1]], _records[order[k]]) != 0) continue;
            error = "canonical 表内记录 #" + order[k - 1] + " 与 #" + order[k] + " 字节等同却 id 不同";
            return false;
        }
        error = string.Empty;
        return true;
    }

    /// <summary>定长记录的字典序（宽相同，逐字节即可）。</summary>
    private static int Compare(byte[] a, byte[] b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i]) continue;
            return a[i] < b[i] ? -1 : 1;
        }
        return 0;
    }
}

/// <summary>
/// 不可变字符串表（STRT）的编译期建表面。下标 0 是**空串哨兵**：blob 里「没有字符串」与
/// 「空字符串」都取 0，读侧不必分辨 null（多语言 LANG 补丁段按下标叠加视图，同样落在 0 上无害）。
/// 去重按 UTF-8 字节，与 <see cref="CanonicalTable"/> 同一条纪律。
/// </summary>
internal sealed class StringPool
{
    private readonly List<byte[]> _bytes = new List<byte[]> { Array.Empty<byte>() };
    private readonly Dictionary<string, int> _index = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        { string.Empty, 0 },
    };

    /// <summary>条目数（含下标 0 的空串哨兵）。</summary>
    public int Count => _bytes.Count;

    /// <summary>去重前的登记次数。</summary>
    public int Inserts { get; private set; }

    /// <summary>池字节数。</summary>
    public int PoolBytes
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _bytes.Count; i++) n += _bytes[i].Length;
            return n;
        }
    }

    /// <summary>登记一个串，返回下标（null / 空串 = 0）。</summary>
    public uint Add(string? s)
    {
        Inserts++;
        if (string.IsNullOrEmpty(s)) return 0u;
        if (_index.TryGetValue(s!, out int id)) return (uint)id;
        id = _bytes.Count;
        _bytes.Add(Encoding.UTF8.GetBytes(s!));
        _index.Add(s!, id);
        return (uint)id;
    }

    /// <summary>读一条（下标 0 = 空串）。</summary>
    public string At(uint id) => id < (uint)_bytes.Count ? Encoding.UTF8.GetString(_bytes[(int)id]) : string.Empty;

    /// <summary>条目字节（读回对账用）。</summary>
    public ReadOnlySpan<byte> BytesAt(uint id) => _bytes[(int)id];
}
