using System.Text;
using FairyNext.Core;
using FairyNext.Core.Rendering;
using FairyNext.Numerics;

namespace FairyNext.Backend.Mock;

/// <summary>规范形的分区（<see cref="CanonicalStream.Locate"/> 的粗定位）。</summary>
public enum CanonicalSection : byte
{
    /// <summary>魔数 + 版本 + 实例条数。</summary>
    Header = 0,
    /// <summary>实例表。</summary>
    Quads = 1,
    /// <summary>被引用的 transform 槽表。</summary>
    Slots = 2,
    /// <summary>被引用的裁剪条目表。</summary>
    Clips = 3,
    /// <summary>段表。</summary>
    Segments = 4,
    /// <summary>run 派生序。</summary>
    Runs = 5,
    /// <summary>结构代。</summary>
    Epoch = 6,
    /// <summary>未乘 α 的基色表。</summary>
    BaseColor = 7,
    /// <summary>越过规范形末尾（= 两条流长度不同）。</summary>
    Beyond = 8,
    /// <summary>孤岛挂载表（M1-23；写在 run 序之后、structEpoch 之前）。</summary>
    Islands = 9,
}

/// <summary>
/// 规范形里的一个位置（增量正确性门失败时的定位结果）。
/// <see cref="Channel"/> 是「这个字段该由哪条通道负责」——门失败要直接指认**哪条通道漏标了**，
/// 而不是丢一个字节偏移让人自己去数。
/// </summary>
public readonly struct CanonicalSite
{
    /// <summary>分区。</summary>
    public readonly CanonicalSection Section;
    /// <summary>分区内的元素下标（Header/Epoch 恒 0）。</summary>
    public readonly int Index;
    /// <summary>字段名。</summary>
    public readonly string Field;
    /// <summary>负责该字段的通道（可多位：字段由多条通道共同决定时不假装只有一个嫌疑人）。</summary>
    public readonly Ch Channel;

    internal CanonicalSite(CanonicalSection section, int index, string field, Ch channel)
    {
        Section = section; Index = index; Field = field; Channel = channel;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        Section == CanonicalSection.Header || Section == CanonicalSection.Epoch || Section == CanonicalSection.Beyond
            ? $"{Section}.{Field} ⇐ {Channel}"
            : $"{Section}[{Index}].{Field} ⇐ {Channel}";
}

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
///  5. 后端原生句柄与 <c>DebugName</c>（后者只是给人看的）；
///  6. 孤岛的 <c>Node</c>（含代际的句柄）与 <c>StillAnimating</c>（内容对**本帧**的自报——
///     帧内量，不是流状态；进哈希会让「全量重建腿从没同步过孤岛」变成一次假红）。
/// 保留的都是能改像素的：rect/uv/color/flags/aux/extra、槽矩阵与 axisAligned 位、
/// clip 的 rect/soft/radii 与它绑的槽、段键与区间、run 序、**孤岛的类别/括号/序/裁剪方式/
/// 分频/槽/域/级联视觉**、structEpoch、baseColor。
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

    /// <summary>
    /// 规范形版本。**改布局必 bump**：金样按版本解释字节。
    /// v2（M1-23）：run 序之后插入**孤岛表**——孤岛挂载记录此前不在规范化范围内，
    /// 于是增量门在孤岛维度上是空的（改一个孤岛的 visual、槽、裁剪方式，两腿的字节都不动），
    /// 而槽/clip 的分配序漂移到了孤岛身上又会被误报成差异。两个方向的错都由这一段封住。
    /// </summary>
    public const byte FormatVersion = 2;

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
        ReadOnlySpan<IslandRecord> islands = snapshot.Islands;

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

        // 孤岛也引用槽与裁剪条目，且它的引用**必须在这里认领**（写槽表/clip 表之前）：
        // 一个只被孤岛引用的槽若拖到孤岛段才认领，槽表的条数就与已写下的 count 对不上。
        // 认领序 = quad 序 → 孤岛序，两腿同源（Extract 对两者的发射序一致），故规范 id 稳定。
        for (int i = 0; i < islands.Length; i++)
        {
            clipMap.Claim(islands[i].ClipEntry);
            slotMap.Claim(islands[i].Slot);
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

        // 孤岛表（M1-23）。**剔除三样非语义位**：Node（含代际的句柄，同 SlotEntry.Owner 的理由）、
        // Handle（后端原生句柄）、StillAnimating（内容对本帧的自报——它是帧内量不是流状态，
        // 进哈希会让「全量重建腿从没同步过孤岛」变成一次假红）。DebugName 同样不是像素。
        w.I32(islands.Length);
        for (int i = 0; i < islands.Length; i++)
        {
            IslandRecord e = islands[i];
            w.U8((byte)e.Kind);
            w.U8((byte)e.NativeKind);
            w.U8((byte)e.Bracket);
            w.U8((byte)e.ClipMode);
            w.I32(e.StencilDepth);
            w.I32(e.RunBefore);
            w.I32(e.PaintOrderIndex);
            w.I32(e.RenderEveryN);
            w.I32(slotMap.Claim(e.Slot));
            w.I32(clipMap.Claim(e.ClipEntry));
            w.F32(e.Visual.Alpha);
            w.U8(e.Visual.Visible ? (byte)1 : (byte)0);
            w.U8(e.Visual.Grayed ? (byte)1 : (byte)0);
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

    // ── 定位：字节偏移 → 分区/字段/通道 ────────────────────────────────────
    //
    // 布局常量在这里**重述一遍** Canonicalize 的写入序。两处不同步 = 定位说谎，
    // 所以下面每条都由一条单测按同一份快照正向验（写多少字节、落在哪个分区）。
    // 不把布局抽成表驱动是有意的：写入侧的可读性优先于两侧的形式统一，
    // 而定位侧的错误只会让报告指错地方，不会让门漏判——判等仍由字节比对本身给出。

    private const int HeaderBytes = 4 + 1 + 4;      // magic + version + quadCount
    private const int QuadBytes = 16 * 4 + 4 * 3 + 1 + 4 * 2;
    private const int SlotBytes = 4 * 6 + 2;
    private const int ClipBytes = 16 + 8 + 16 + 4;
    private const int SegmentBytes = 4 * 4 + 1 + 1 + 4 * 3;
    private const int RunBytes = 8;
    private const int IslandBytes = 4 + 4 * 6 + 4 + 2;

    /// <summary>
    /// 把 <see cref="FirstDifference"/> 给的字节偏移翻译成「哪个分区的哪个字段、该由哪条通道负责」。
    /// 增量正确性门的失败报告靠它从「第 137 字节不同」变成「quad[1].color ⇐ Color 通道」。
    /// </summary>
    public static CanonicalSite Locate(StreamSnapshot snapshot, int offset)
    {
        if (snapshot == null || offset < 0) return new CanonicalSite(CanonicalSection.Beyond, 0, "(none)", Ch.None);

        if (offset < HeaderBytes)
        {
            // 实例条数不同 = 叶的进出或预留区形状变了：那是结构的事。
            string field = offset < 5 ? "magic/version" : "quadCount";
            return new CanonicalSite(CanonicalSection.Header, 0, field, offset < 5 ? Ch.None : Ch.Structure);
        }
        int at = offset - HeaderBytes;

        int quadBytes = snapshot.Quads.Length * QuadBytes;
        if (at < quadBytes) return LocateQuad(at / QuadBytes, at % QuadBytes);
        at -= quadBytes;

        if (at < 4) return new CanonicalSite(CanonicalSection.Slots, 0, "count", Ch.Structure);
        at -= 4;
        int slotBytes = CountReferenced(snapshot, referencedSlots: true) * SlotBytes;
        if (at < slotBytes)
        {
            int i = at / SlotBytes, f = at % SlotBytes;
            return new CanonicalSite(CanonicalSection.Slots, i,
                f < 24 ? "matrix" : "flags", Ch.Transform);
        }
        at -= slotBytes;

        if (at < 4) return new CanonicalSite(CanonicalSection.Clips, 0, "count", Ch.Structure);
        at -= 4;
        int clipBytes = CountReferenced(snapshot, referencedSlots: false) * ClipBytes;
        if (at < clipBytes)
        {
            int i = at / ClipBytes, f = at % ClipBytes;
            string field = f < 16 ? "rect" : f < 24 ? "soft" : f < 40 ? "radii" : "slot";
            return new CanonicalSite(CanonicalSection.Clips, i, field,
                f < 16 || f >= 40 ? Ch.Structure | Ch.Transform : Ch.Structure);
        }
        at -= clipBytes;

        if (at < 4) return new CanonicalSite(CanonicalSection.Segments, 0, "count", Ch.Structure);
        at -= 4;
        int segBytes = snapshot.Segments.Length * SegmentBytes;
        if (at < segBytes)
        {
            int i = at / SegmentBytes, f = at % SegmentBytes;
            string field = f < 16 ? "textures" : f < 18 ? "key" : f < 22 ? "start" : f < 26 ? "count" : "run";
            return new CanonicalSite(CanonicalSection.Segments, i, field, Ch.Structure);
        }
        at -= segBytes;

        if (at < 4) return new CanonicalSite(CanonicalSection.Runs, 0, "count", Ch.Structure);
        at -= 4;
        int runBytes = snapshot.Runs.Length * RunBytes;
        if (at < runBytes)
        {
            int i = at / RunBytes, f = at % RunBytes;
            return new CanonicalSite(CanonicalSection.Runs, i, f < 4 ? "runIndex" : "sortingOrder", Ch.Structure);
        }
        at -= runBytes;

        if (at < 4) return new CanonicalSite(CanonicalSection.Islands, 0, "count", Ch.Structure);
        at -= 4;
        int islandBytes = snapshot.Islands.Length * IslandBytes;
        if (at < islandBytes)
        {
            int i = at / IslandBytes, f = at % IslandBytes;
            // 字段 → 通道：类别/括号/序/分频/裁剪域是整编的产物（结构）；槽是变换与结构共管；
            // visual 三元是 Color/Visible 两条通道的级联落点（「visual 并入下行」的落款）。
            string field =
                f < 4 ? "kind/native/bracket/clipMode"
                : f < 8 ? "stencilDepth"
                : f < 16 ? "order"
                : f < 20 ? "renderEveryN"
                : f < 24 ? "slot"
                : f < 28 ? "clip"
                : "visual";
            Ch channel = f < 20 ? Ch.Structure
                : f < 24 ? (Ch.Structure | Ch.Transform)
                : f < 28 ? Ch.Structure
                : (Ch.Color | Ch.Visible);
            return new CanonicalSite(CanonicalSection.Islands, i, field, channel);
        }
        at -= islandBytes;

        if (at < 4) return new CanonicalSite(CanonicalSection.Epoch, 0, "structEpoch", Ch.Structure);
        at -= 4;

        if (at < 4) return new CanonicalSite(CanonicalSection.BaseColor, 0, "count", Ch.Structure);
        at -= 4;
        if (at < snapshot.BaseColor.Length * 4)
            return new CanonicalSite(CanonicalSection.BaseColor, at / 4, "baseColor", Ch.Content);

        return new CanonicalSite(CanonicalSection.Beyond, 0, "(length)", Ch.Structure);
    }

    private static CanonicalSite LocateQuad(int index, int field)
    {
        // 字段 → 通道：rect 归变换（几何落位），uv/aux/extra 归内容（发射器的产物），
        // color 归颜色，flags 两者都沾（发射位 + grayed 位），route 三分量归结构（切段与落位的产物）。
        if (field < 16) return new CanonicalSite(CanonicalSection.Quads, index, "rect", Ch.Transform | Ch.Content);
        if (field < 32) return new CanonicalSite(CanonicalSection.Quads, index, "uvA", Ch.Content);
        if (field < 48) return new CanonicalSite(CanonicalSection.Quads, index, "uvB", Ch.Content);
        if (field < 64) return new CanonicalSite(CanonicalSection.Quads, index, "extra", Ch.Content);
        if (field < 68) return new CanonicalSite(CanonicalSection.Quads, index, "color", Ch.Color | Ch.Visible);
        if (field < 72) return new CanonicalSite(CanonicalSection.Quads, index, "flags", Ch.Color | Ch.Content);
        if (field < 76) return new CanonicalSite(CanonicalSection.Quads, index, "aux", Ch.Content);
        if (field < 77) return new CanonicalSite(CanonicalSection.Quads, index, "texSlot", Ch.Structure);
        if (field < 81) return new CanonicalSite(CanonicalSection.Quads, index, "slot", Ch.Transform | Ch.Structure);
        return new CanonicalSite(CanonicalSection.Quads, index, "clip", Ch.Structure);
    }

    /// <summary>规范形里真正写下的槽/裁剪条目数（未被引用的条目不进哈希，定位也要按同一口径数）。</summary>
    private static int CountReferenced(StreamSnapshot snapshot, bool referencedSlots)
    {
        ReadOnlySpan<QuadInstance> quads = snapshot.Quads;
        ReadOnlySpan<ClipEntry> clips = snapshot.Clips;
        ReadOnlySpan<IslandRecord> islands = snapshot.Islands;
        var slotMap = new Renumber(snapshot.Slots.Length);
        var clipMap = new Renumber(clips.Length);
        slotMap.Claim(0);
        clipMap.Claim(0);
        for (int i = 0; i < quads.Length; i++)
        {
            clipMap.Claim((int)quads[i].ClipIndex);
            slotMap.Claim((int)quads[i].SlotIndex);
        }
        for (int i = 0; i < islands.Length; i++)
        {
            clipMap.Claim(islands[i].ClipEntry);
            slotMap.Claim(islands[i].Slot);
        }
        for (int id = 0; id < clipMap.Count; id++)
        {
            int src = clipMap.SourceOf(id);
            if ((uint)src < (uint)clips.Length) slotMap.Claim((int)clips[src].Slot);
        }
        return referencedSlots ? slotMap.Count : clipMap.Count;
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
