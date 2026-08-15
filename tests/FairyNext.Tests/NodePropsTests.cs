using FairyNext.Contracts;
using FairyNext.Core;

namespace FairyNext.Tests;

/// <summary>
/// M1-09 属性 setter codegen 的**运行期**用例（Program 的 partial 分片）。
/// 生成期执法（缺归属 = 编译错、生成物确定性）由独立门 tools/PropGen.Tests 守；本文件守三件事：
///  ① 归属表自洽——ABI PropId 单源与生成的 <see cref="NodeProps.All"/> 逐条对得上，
///     归属是**单个上行通道位**，下行伴随位只落在下行掩码内（架构不变量 1）；
///  ② 等值切断逐 PropId 成立——「写同值后 dirtyWord 不变」对表内**每一条**成立
///     （架构不变量 3 明写「对每个 PropId 自动生成该测试」，这里的循环就是那份自动化）；
///  ③ 写来源分流——src 一路传到失效理由聚合（Anim→Timeline / Binding→BindingRow / Layout→LayoutDerived）。
///
/// 替换等价性另有一份**免费**证据：M1-06 的 42 条 NodeTable 用例一条未改，替换后全绿。
/// </summary>
public static partial class Program
{
    private static void NodePropsSuite()
    {
        PropTableCoversAbi();
        PropTableChannelsWellFormed();
        PropTableMatchesHandWrittenSemantics();
        EveryPropIdCutsEqualWrites();
        EveryPropIdMarksItsOwnChannels();
        UnbackedPropIsLoudNotSilent();
        UnknownPropIdStillRejected();
        WriteSourceRoutesToReason();
        PairSettersUseGeneratedStores();
        GeneratedWritersDriveVisualBits();
    }

    // ── 归属表自洽 ──────────────────────────────────────────────────────────

    private static void PropTableCoversAbi()
    {
        NodePropInfo[] all = NodeProps.All;
        bool sameCount = all.Length == Abi.PropIds.Length;

        bool everyAbiIdOwned = true;
        foreach (AbiPropId p in Abi.PropIds)
        {
            if (!NodeProps.TryGet(p.Id, out NodePropInfo info) || info.Name != p.Name) everyAbiIdOwned = false;
        }

        // id 唯一 + 升序：生成器按 id 排序，重复 id 会让分派 switch 编译不过（这里守表本身）
        bool uniqueAscending = true;
        for (int i = 1; i < all.Length; i++)
        {
            if (all[i].Id <= all[i - 1].Id) uniqueAscending = false;
        }

        Check("归属表：Abi.PropIds 每一项都有通道归属，且无重复 id（不变量 1 通道归属封闭）",
            sameCount && everyAbiIdOwned && uniqueAscending && all.Length > 0);
    }

    private static void PropTableChannelsWellFormed()
    {
        bool ok = true;
        foreach (NodePropInfo p in NodeProps.All)
        {
            // 归属必须是**单个**上行通道位；Mark 位集 = 归属 ∪ 下行位，不许混进派生位（BoundsD 是拉取式）
            uint c = (uint)p.Channel;
            bool single = c != 0 && (c & (c - 1)) == 0;
            bool up = (p.Channel & ChMask.Up) == p.Channel;
            bool marksSuperset = (p.Marks & p.Channel) == p.Channel;
            bool extraIsDown = (p.Marks & ~(p.Channel | ChMask.Down)) == 0;
            if (!single || !up || !marksSuperset || !extraIsDown) ok = false;
        }
        Check("归属表：每项归属是单个上行通道位，附加位只能是下行位", ok);
    }

    private static void PropTableMatchesHandWrittenSemantics()
    {
        // M1-06 手写 switch 的通道语义逐条钉住：替换不是「重新决定」，是「同样的账换个记法」。
        bool ok =
            NodeProps.MarksOf(1) == Ch.Layout &&                              // Width
            NodeProps.MarksOf(2) == Ch.Layout &&                              // Height
            NodeProps.MarksOf(64) == (Ch.Color | Ch.DownColor) &&             // Alpha
            NodeProps.MarksOf(65) == (Ch.Visible | Ch.DownVisible) &&         // Visible
            NodeProps.MarksOf(66) == (Ch.Color | Ch.DownColor) &&             // Grayed
            NodeProps.MarksOf(128) == Ch.Transform &&                         // X
            NodeProps.MarksOf(132) == Ch.Transform;                           // Rotation
        Check("归属表：通道语义与 M1-06 手写 switch 逐条相同（替换等价性）", ok);
    }

    // ── 等值切断：逐 PropId ─────────────────────────────────────────────────

    /// <summary>按存储形态取一对「必然不同」的入参（位属性 0/1，α 两个不同 u8，float 两个不同位型）。</summary>
    private static void ProbeValues(NodePropStore store, out float a, out float b)
    {
        switch (store)
        {
            case NodePropStore.VisualBit: a = 0f; b = 1f; break;
            case NodePropStore.AlphaU8: a = 0.5f; b = 0.25f; break;
            default: a = 3.5f; b = 7.25f; break;
        }
    }

    private static void EveryPropIdCutsEqualWrites()
    {
        var t = SmallTree(out var n, out _, out _);
        var log = new MarkLog();
        t.InvalidationHook = log.Hook;

        string? bad = null;
        foreach (NodePropInfo p in NodeProps.All)
        {
            if (!p.Backed) continue;
            ProbeValues(p.Store, out float v, out _);

            t.WriteAuthored(n, p.Id, v, WriteSource.User);      // 先落到一个确定的值
            int before = log.Count;
            bool second = t.WriteAuthored(n, p.Id, v, WriteSource.User);   // 同值再写
            if (second || log.Count != before) bad ??= p.Name;
        }

        Check("等值切断（不变量 3）：表内每个 PropId 写同值都不写、不 Mark" + (bad == null ? "" : " —— 破例：" + bad),
            bad == null);
    }

    private static void EveryPropIdMarksItsOwnChannels()
    {
        var t = SmallTree(out var n, out _, out _);
        var log = new MarkLog();
        t.InvalidationHook = log.Hook;

        string? bad = null;
        foreach (NodePropInfo p in NodeProps.All)
        {
            if (!p.Backed) continue;
            ProbeValues(p.Store, out float v0, out float v1);

            t.WriteAuthored(n, p.Id, v0, WriteSource.User);
            int before = log.Count;
            bool changed = t.WriteAuthored(n, p.Id, v1, WriteSource.User);   // 换个值：必须脏
            if (!changed || log.Count != before + 1 || log.Last != p.Marks) bad ??= p.Name;
        }

        Check("通道路由：表内每个 PropId 值变即 Mark，且 Mark 的位集 == 表里的 Marks"
            + (bad == null ? "" : " —— 破例：" + bad), bad == null);
    }

    // ── 未接列 / 未归属 ─────────────────────────────────────────────────────

    private static void UnbackedPropIsLoudNotSilent()
    {
        var t = SmallTree(out var n, out _, out _);
        var log = new MarkLog();
        t.InvalidationHook = log.Hook;

        bool anyUnbacked = false, ok = true;
        foreach (NodePropInfo p in NodeProps.All)
        {
            if (p.Backed) continue;
            anyUnbacked = true;
            bool fired = AssertFires(() => t.WriteAuthored(n, p.Id, 1f, WriteSource.User));
            if (log.Count != 0 || (DebugGates && !fired)) ok = false;
        }

        Check("已声明归属但未接列的 PropId：断言响、不写不脏（不静默落 default）", anyUnbacked && ok);
    }

    private static void UnknownPropIdStillRejected()
    {
        var t = SmallTree(out var n, out _, out _);
        var log = new MarkLog();
        t.InvalidationHook = log.Hook;
        bool fired = AssertFires(() => t.WriteAuthored(n, 250, 1f, WriteSource.User));
        Check("未归属 PropId 仍落 default 断言且不写（分派封闭性未因 codegen 变松）",
            log.Count == 0 && (!DebugGates || fired));
    }

    // ── 写来源分流 ──────────────────────────────────────────────────────────

    private static void WriteSourceRoutesToReason()
    {
        var t = new NodeTable();
        var n = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, n);
        var inv = new Invalidation(t);

        t.WriteAuthored(n, 128, 5f, WriteSource.Anim);        // X ← 时间轴
        t.WriteAuthored(n, 1, 60f, WriteSource.Binding);      // Width ← 绑定行
        t.WriteAuthored(n, 64, 0.5f, WriteSource.User);       // Alpha ← 用户写

        InvalidationDiag d = inv.Diagnostics;
        Check("写来源分流：生成写口把 src 原样交给失效平面，理由聚合逐条对上",
            d.MarksOf(InvalidateReason.Timeline) == 1
            && d.MarksOf(InvalidateReason.BindingRow) == 1
            && d.MarksOf(InvalidateReason.UserWrite) == 1
            && d.MarksOf(Ch.Transform) == 1 && d.MarksOf(Ch.Layout) == 1
            && d.MarksOf(Ch.Color) == 1 && d.MarksOf(Ch.DownColor) == 1);
    }

    // ── 公共 setter 走同一套生成写口 ────────────────────────────────────────

    private static void PairSettersUseGeneratedStores()
    {
        var t = SmallTree(out var n, out _, out _);
        var log = new MarkLog();
        t.InvalidationHook = log.Hook;

        t.SetPosition(n, 3f, 4f);
        int afterMove = log.Count;
        Ch moveCh = log.Last;
        t.SetPosition(n, 3f, 4f);                 // 同值：两列都切断 ⇒ 不 Mark
        int afterSame = log.Count;
        t.SetPosition(n, 3f, 9f);                 // 只有 y 变：仍是一次 Mark
        t.SetSize(n, 20f, 30f);
        Ch sizeCh = log.Last;

        Check("成对写口：SetPosition/SetSize 复用生成的 Store*，双列一次 Mark、同值不脏",
            afterMove == 1 && moveCh == (NodeTable.MarksX | NodeTable.MarksY)
            && afterSame == 1 && log.Count == 3
            && sizeCh == (NodeTable.MarksWidth | NodeTable.MarksHeight)
            && Near(t.GetPosition(n).y, 9f) && Near(t.GetSize(n).x, 20f));
    }

    private static void GeneratedWritersDriveVisualBits()
    {
        var t = SmallTree(out var n, out _, out _);
        var log = new MarkLog();
        t.InvalidationHook = log.Hook;

        t.SetVisible(n, false);
        Ch visCh = log.Last;
        t.SetVisible(n, false);                   // 同值不脏
        int afterVisible = log.Count;
        t.SetTouchable(n, false);
        Ch touchCh = log.Last;
        t.SetPixelSnap(n, true);
        Ch snapCh = log.Last;

        Check("位属性公共 setter：走生成写口，位值未变不脏，通道取自归属表",
            afterVisible == 1 && visCh == NodeTable.MarksVisible
            && touchCh == NodeTable.MarksTouchable && snapCh == NodeTable.MarksPixelSnap
            && !t.IsVisible(n) && !t.IsTouchable(n)
            && (t.LocalVisual(n) & Visual.PixelSnap) != 0);
    }
}
