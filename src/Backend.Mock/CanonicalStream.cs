using System.Text;
using FairyNext.Core.Rendering;
using FairyNext.Numerics;

namespace FairyNext.Backend.Mock;

/// <summary>
/// 实例流的**规范化**字节形态与哈希（架构「平面六」核心数据结构 <c>Trace.streamHash</c>）。
///
/// 一句话：把一条流压成「同样的画面 ⇒ 同样的字节」的规范形，再对字节做 FNV-1a 64。
/// 两个消费者共用这一份实现：
///  - **M1-24 Trace**：逐帧一个 <c>streamHash</c>，两份 Trace 的 diff 直接报首分歧帧；
///  - **M1-14 增量正确性门**：增量消化 vs 全量重建，比 <see cref="Canonicalize"/> 的字节，
///    失败时 <see cref="FirstDifference"/> 给出首差异偏移。
///
/// ── 剔除清单（非语义位）────────────────────────────────────────────────────
/// 剔除的都是「换一次运行/换一条重建路径就会变，但画面一模一样」的位：
///  1. **池下标与句柄**：<c>route.slot</c> / <c>route.clipIndex</c> 的原始数值被换成
///     **首用序重编号**的规范 id，槽表/clip 表按同一编号重排——
///     谁分到 3 号槽是分配器的事，不是像素的事；
///  2. **未被引用的槽/clip 条目**：分配残留不进哈希（残留条目再多也不改一个像素）；
///  3. <c>SlotEntry.Owner</c>（NodeHandle 含代际位）与 <c>WriteFreq</c>（升格侦测计数）；
///  4. <c>ClipEntry._pad</c>（契约恒零）与 <c>route</c> 的保留位；
///  5. 后端原生句柄与 <c>DebugName</c>（后者只是给人看的）。
/// 保留的都是能改像素的：rect/uv/color/flags/aux/extra、槽矩阵与 axisAligned 位、
/// clip 的 rect/soft/radii 与它绑的槽、段键与区间、run 序、structEpoch、baseColor。
///
/// ── 为什么是「字节 + 哈希」而不是只有哈希 ──────────────────────────────────
/// 哈希只回答「一样吗」；门失败时要回答「哪儿不一样」。同一份规范化字节两用，
/// 保证「哈希说不等」与「字节 diff 指出的位置」永远是同一个事实。
///
/// float 一律按**原始位**写入（<c>SingleToInt32Bits</c>）：+0/-0 不同、NaN payload 不同即哈希不同。
/// 这与等值切断的 <see cref="BitEquals"/> 语义**故意不同**——切断是为了少置一次脏，
/// 规范化是为了对拍两条路径，后者宁可多报一次差异也不能把差异吃掉。
/// </summary>
public static class CanonicalStream
{
    /// <summary>规范形魔数（"FNXS"）。</summary>
    public const uint Magic = 0x53584E46;

    /// <summary>规范形版本。**改布局必 bump**：金样按版本解释字节。</summary>
    public const byte FormatVersion = 1;

    /// <summary>规范化字节（同画面同字节）。</summary>
    public static byte[] Canonicalize(StreamSnapshot snapshot)
    {
        if (snapshot == null) return Array.Empty<byte>();

        var w = new ByteWriter(64 + snapshot.QuadCount * 96);
        w.U32(Magic);
        w.U8(FormatVersion);

        ReadOnlySpan<QuadInstance> quads = snapshot.Quads;
        ReadOnlySpan<SlotEntry> slots = snapshot.Slots;
        ReadOnlySpan<ClipEntry> clips = snapshot.Clips;

        // 首用序重编号：槽 0（identity）与 clip 0（None 哨兵）恒占规范 id 0。
        var slotMap = new Renumber(slots.Length);
        var clipMap = new Renumber(clips.Length);
        slotMap.Claim(0);
        clipMap.Claim(0);

        w.I32(quads.Length);
        for (int i = 0; i < quads.Length; i++)
        {
            QuadInstance q = quads[i];
            w.V4(q.Rect);
            w.V4(q.UvA);
            w.V4(q.UvB);
            w.V4(q.Extra);
            w.U32(q.Color);
            w.U32(q.Flags);
            w.U32(q.Aux);
            w.U8((byte)q.TexSlot);

            int clipId = clipMap.Claim((int)q.ClipIndex);
            w.I32(slotMap.Claim((int)q.SlotIndex));
            w.I32(clipId);
        }

        // 被引用的 clip 所绑的槽也要进规范编号，而且必须在写槽表**之前**全部认领完——
        // 否则 clip 表里冒出一个新槽，槽表的条数就与已写下的 count 对不上。
        for (int id = 0; id < clipMap.Count; id++)
        {
            int src = clipMap.SourceOf(id);
            if ((uint)src < (uint)clips.Length) slotMap.Claim((int)clips[src].Slot);
        }

        w.I32(slotMap.Count);
        for (int id = 0; id < slotMap.Count; id++)
        {
            int src = slotMap.SourceOf(id);
            SlotEntry e = (uint)src < (uint)slots.Length ? slots[src] : SlotEntry.Identity;
            w.F32(e.M.m00); w.F32(e.M.m01); w.F32(e.M.m10);
            w.F32(e.M.m11); w.F32(e.M.tx); w.F32(e.M.ty);
            w.U16((ushort)e.Flags);
            // Owner（含代际的句柄）与 WriteFreq（诊断计数）在此**有意缺席**。
        }

        w.I32(clipMap.Count);
        for (int id = 0; id < clipMap.Count; id++)
        {
            int src = clipMap.SourceOf(id);
            ClipEntry e = (uint)src < (uint)clips.Length ? clips[src] : default;
            w.V4(e.Rect);
            w.F32(e.Soft.x); w.F32(e.Soft.y);
            w.V4(e.Radii);
            w.I32(slotMap.Claim((int)e.Slot));
            // _pad 有意缺席（契约恒零）。
        }

        ReadOnlySpan<SegmentDesc> segments = snapshot.Segments;
        w.I32(segments.Length);
        for (int i = 0; i < segments.Length; i++)
        {
            SegmentDesc s = segments[i];
            w.U32(s.Tex0.Value); w.U32(s.Tex1.Value); w.U32(s.Tex2.Value); w.U32(s.Tex3.Value);
            w.U8(s.TexCount);
            w.U8((byte)s.Blend);
            w.I32(s.Start);
            w.I32(s.Count);
            w.I32(s.RunIndex);
        }

        ReadOnlySpan<RunOrder> runs = snapshot.Runs;
        w.I32(runs.Length);
        for (int i = 0; i < runs.Length; i++)
        {
            w.I32(runs[i].RunIndex);
            w.I32(runs[i].SortingOrder);
        }

        w.U32(snapshot.StructEpoch);

        ReadOnlySpan<uint> baseColor = snapshot.BaseColor;
        w.I32(baseColor.Length);
        for (int i = 0; i < baseColor.Length; i++) w.U32(baseColor[i]);

        return w.ToArray();
    }

    /// <summary>规范化流哈希（FNV-1a 64；Trace 的逐帧哈希就是它）。</summary>
    public static ulong Hash(StreamSnapshot snapshot) => FnvHash.Hash64(Canonicalize(snapshot));

    /// <summary>两份快照的规范形是否逐字节相等（增量 vs 全量对拍的判据）。</summary>
    public static bool Equal(StreamSnapshot a, StreamSnapshot b) =>
        FirstDifference(Canonicalize(a), Canonicalize(b)) < 0;

    /// <summary>首差异字节偏移；相等返回 -1。长度不同时返回较短者的长度。</summary>
    public static int FirstDifference(byte[] a, byte[] b)
    {
        if (a == null || b == null) return a == b ? -1 : 0;
        int n = a.Length < b.Length ? a.Length : b.Length;
        for (int i = 0; i < n; i++) if (a[i] != b[i]) return i;
        return a.Length == b.Length ? -1 : n;
    }

    /// <summary>
    /// 人读的规范形 dump（门失败时贴进报告用）。**不是**哈希的输入——
    /// 哈希只吃 <see cref="Canonicalize"/> 的字节，两份文本/字节不构成两份事实源。
    /// </summary>
    public static string Dump(StreamSnapshot snapshot)
    {
        if (snapshot == null) return "(null)";
        var sb = new StringBuilder();
        sb.Append("stream epoch=").Append(snapshot.StructEpoch)
          .Append(" quads=").Append(snapshot.Quads.Length)
          .Append(" hash=0x").Append(Hash(snapshot).ToString("X16")).Append('\n');
        ReadOnlySpan<QuadInstance> quads = snapshot.Quads;
        for (int i = 0; i < quads.Length; i++)
        {
            QuadInstance q = quads[i];
            sb.Append("  q").Append(i).Append(' ')
              .Append("rect=(").Append(q.Rect.x).Append(',').Append(q.Rect.y).Append(',')
              .Append(q.Rect.z).Append(',').Append(q.Rect.w).Append(") ")
              .Append("color=0x").Append(q.Color.ToString("X8"))
              .Append(" slot=").Append(q.SlotIndex)
              .Append(" clip=").Append(q.ClipIndex)
              .Append(" tex=").Append(q.TexSlot)
              .Append(" flags=0x").Append(q.Flags.ToString("X"))
              .Append('\n');
        }
        ReadOnlySpan<SegmentDesc> segs = snapshot.Segments;
        for (int i = 0; i < segs.Length; i++) sb.Append("  ").Append(segs[i]).Append('\n');
        ReadOnlySpan<RunOrder> runs = snapshot.Runs;
        for (int i = 0; i < runs.Length; i++) sb.Append("  ").Append(runs[i]).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// 首用序重编号表：源下标 → 规范 id（0,1,2… 按第一次被引用的顺序）。
    /// 越界的源下标映射为 id 0（None/identity）——规范化是纯函数，不在这里报错；
    /// 越界引用本身由 mock 后端的健全性检查抓。
    /// </summary>
    private struct Renumber
    {
        private int[] _toId;      // 源下标 → 规范 id + 1（0 = 未认领）
        private int[] _toSource;  // 规范 id → 源下标
        private int _count;

        internal Renumber(int capacity)
        {
            int cap = capacity < 1 ? 1 : capacity;
            _toId = new int[cap];
            _toSource = new int[cap];
            _count = 0;
        }

        internal int Count => _count;

        internal int SourceOf(int id) => (uint)id < (uint)_count ? _toSource[id] : 0;

        /// <summary>认领一个源下标，返回其规范 id（幂等）。</summary>
        internal int Claim(int source)
        {
            if (source < 0) return 0;
            if (source >= _toId.Length)
            {
                int cap = _toId.Length;
                while (cap <= source) cap *= 2;
                Array.Resize(ref _toId, cap);
                Array.Resize(ref _toSource, cap);
            }
            int slot = _toId[source];
            if (slot != 0) return slot - 1;
            int id = _count++;
            _toId[source] = id + 1;
            _toSource[id] = source;
            return id;
        }
    }

    /// <summary>增长型字节写入器（小端固定；规范形的字节序不随机器变）。</summary>
    private struct ByteWriter
    {
        private byte[] _buf;
        private int _n;

        internal ByteWriter(int capacity)
        {
            _buf = new byte[capacity < 16 ? 16 : capacity];
            _n = 0;
        }

        internal void U8(byte v)
        {
            Ensure(1);
            _buf[_n++] = v;
        }

        internal void U16(ushort v)
        {
            Ensure(2);
            _buf[_n++] = (byte)v;
            _buf[_n++] = (byte)(v >> 8);
        }

        internal void U32(uint v)
        {
            Ensure(4);
            _buf[_n++] = (byte)v;
            _buf[_n++] = (byte)(v >> 8);
            _buf[_n++] = (byte)(v >> 16);
            _buf[_n++] = (byte)(v >> 24);
        }

        internal void I32(int v) => U32((uint)v);

        internal void F32(float v) => U32((uint)BitConverter.SingleToInt32Bits(v));

        internal void V4(in Vector4 v)
        {
            F32(v.x); F32(v.y); F32(v.z); F32(v.w);
        }

        internal byte[] ToArray()
        {
            var outBuf = new byte[_n];
            Array.Copy(_buf, outBuf, _n);
            return outBuf;
        }

        private void Ensure(int extra)
        {
            if (_n + extra <= _buf.Length) return;
            int cap = _buf.Length;
            while (cap < _n + extra) cap *= 2;
            Array.Resize(ref _buf, cap);
        }
    }
}
