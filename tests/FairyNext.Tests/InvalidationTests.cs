using System.Reflection;
using FairyNext.Core;

namespace FairyNext.Tests;

/// <summary>
/// M1-07 失效协议用例（Program 的 partial 分片）。
/// 逐条对齐 docs/architecture.md「平面二 · 时间宪法」不变量清单：
/// 2 Mark 幂等 O(1) / 5 全局失效只在 P2 / 9 P7-P8 禁改（降级不丢）/ 12 失效可解释性（reason 聚合）/
/// 13 下行戳完备（只下钻本帧戳过的子树、隐藏免下钻、显时补戳）/ 15 reason 必填，
/// 外加方向语义三分（上行队列 / 下行子树戳 / BoundsD 遇脏即停 query-pull）与 M1-06 等值切断的联合行为。
/// </summary>
public static partial class Program
{
    // ── 工具 ────────────────────────────────────────────────────────────────

    /// <summary>记录型排水器（本包不实现任何真排水器，测试用它验协议）。</summary>
    private sealed class DrainRecorder : IChannelDrain
    {
        public DrainRecorder(Ch consumes) { Consumes = consumes; }

        public Ch Consumes { get; }
        public readonly List<NodeHandle> Seen = new List<NodeHandle>();
        public readonly List<Ch> Channels = new List<Ch>();
        public readonly List<ulong> Frames = new List<ulong>();
        public readonly List<FramePhase> Phases = new List<FramePhase>();
        public int Calls;

        /// <summary>在排水回调内执行（测「排水期间再 Mark 落下一批」）。</summary>
        public Action? Reentrant;

        /// <summary>M1-08 起签名带 <c>ref FrameContext</c>（相位机落地后补上的接缝）。</summary>
        public void Drain(ref FrameContext ctx, Ch channel, ReadOnlySpan<NodeHandle> queue)
        {
            Calls++;
            Channels.Add(channel);
            Frames.Add(ctx.FrameId);
            Phases.Add(ctx.Phase);
            for (int i = 0; i < queue.Length; i++) Seen.Add(queue[i]);
            Reentrant?.Invoke();
        }
    }

    /// <summary>root → n[0] → n[1] → … 的一条链（失效沿父链上溯的测试地形）。</summary>
    private static NodeTable ChainTree(int depth, out NodeHandle[] nodes)
    {
        var t = new NodeTable();
        nodes = new NodeHandle[depth];
        NodeHandle parent = t.Root;
        for (int i = 0; i < depth; i++)
        {
            nodes[i] = t.CreateNode(NodeType.Component);
            t.AddChild(parent, nodes[i]);
            parent = nodes[i];
        }
        return t;
    }

    /// <summary>root → (A → a0,a1 ; B → b0,b1) 的两支树（下行路由/隔离的测试地形）。</summary>
    private static NodeTable ForkTree(out NodeHandle a, out NodeHandle[] ac, out NodeHandle b, out NodeHandle[] bc)
    {
        var t = new NodeTable();
        a = t.CreateNode(NodeType.Component);
        b = t.CreateNode(NodeType.Component);
        t.AddChild(t.Root, a);
        t.AddChild(t.Root, b);
        ac = new[] { t.CreateNode(NodeType.Image), t.CreateNode(NodeType.Image) };
        bc = new[] { t.CreateNode(NodeType.Image), t.CreateNode(NodeType.Image) };
        t.AddChild(a, ac[0]); t.AddChild(a, ac[1]);
        t.AddChild(b, bc[0]); t.AddChild(b, bc[1]);
        return t;
    }

    private static int TotalQueued(Invalidation inv)
    {
        int n = 0;
        for (int slot = 0; slot < ChMask.UpCount; slot++) n += inv.QueueLength(ChMask.UpChannelAt(slot));
        return n;
    }

    private static void InvalidationSuite()
    {
        MarkIsIdempotent();
        MarkReasonIsMandatory();
        SevenUpChannelsDoNotCrossTalk();
        DrainIsFifoAndClearsBitsAndQueue();
        DrainDropsStaleHandles();
        MarkDuringDrainLandsInNextBatch();
        DrainWithoutDrainerKeepsQueueAndBits();
        QueueGrowsBeyondInitialCapacity();
        RegisterIsUniquePerChannel();
        DrainChannelsBatch();

        DownStampCoversAncestorChain();
        DownDrainRoutesOnlyStampedBranch();
        DownDrainSkipsHiddenSubtree();
        HiddenSubtreeRedrilledOnShow();
        DownChannelsNeverEnterQueues();
        MarkDownDuringDrainLandsInNextGeneration();
        ReattachedStampedSubtreeIsRedrilled();
        DetachedWritesSurviveReattachAcrossGenerations();
        EpochWrapClearsStaleStamps();

        BoundsStopsAtDirtyAncestor();
        BoundsConsumeClearsSubtree();

        GlobalInvalidateOnlyAppliesAtP2();
        GlobalSourcesAreIndependent();
        GlobalFanOutMarksWithGlobalReason();

        DiagnosticsAggregateByChannelAndReason();
        DiagnosticsLatchAtFrameEnd();

        EqualityCutoffJoinsMark();
        AlphaMarksUpAndDownInOneCall();
        MarkInRenderDrainAssertsButKeepsWrite();
        MarkSiteRingRecordsCallSite();
        CascadingPropsAllCarryDownChannel();
        TouchableCascadesToSubtree();
    }

    // ── 级联下行门：参与 Visual.Cascade 的局部属性必须带下行通道 ──────────────
    //
    // 缺一个下行位的表现不是崩溃而是**静默错判**：父置 touchable=false，子树 worldVisual
    // 不重算 → 子节点自认可点 → 命中测试命中本该不可点的节点。这与旧 fork 靠
    // `_NotifyDescendantStreams` 补丁救场的第五通道病同构，故按属性逐条钉死，
    // 而不是只测当前这一个漏网属性。

    private static void CascadingPropsAllCarryDownChannel()
    {
        // Visual.Cascade 逐位读的四个分量：α（积）、visible（AND）、touchable（AND）、grayed（OR）。
        // 凡被父值影响的分量，其 setter 必须给下行通道——否则子树 worldVisual 永远收不到重算。
        string[] cascading = { "Alpha", "Visible", "Touchable", "Grayed" };
        var missing = new List<string>();
        foreach (string name in cascading)
        {
            NodePropInfo p = Array.Find(NodeProps.All, x => x.Name == name);
            if (p.Name == null || (p.Marks & ChMask.Down) == Ch.None) missing.Add(name);
        }
        Check($"级联下行门：参与 Visual.Cascade 的属性全部带下行通道（缺：{(missing.Count == 0 ? "无" : string.Join(",", missing))}）",
            missing.Count == 0);
    }

    private static void TouchableCascadesToSubtree()
    {
        var t = ForkTree(out var a, out var ac, out _, out _);
        var inv = new Invalidation(t);
        t.Phase = FramePhase.P6_Settle;
        t.ApplyStructure();                         // 序先定形，否则派生沿空 paintOrder 静默算 0 个节点
        t.DrainDerivedFull();                       // 收敛初值：整树 touchable = true
        bool before = (t.WorldVisual(ac[0]) & Visual.Touchable) != 0;

        t.Phase = FramePhase.P3_Commands;
        inv.BeginFrame(2);
        t.SetTouchable(a, false);                   // 父置不可点

        t.Phase = FramePhase.P6_Settle;
        var reached = new List<uint>();
        inv.DrainDown((idx, bits) => reached.Add(idx));
        t.ApplyStructure();
        t.DrainDerivedFull();
        bool after = (t.WorldVisual(ac[0]) & Visual.Touchable) != 0;

        Check("级联下行门：父置 touchable=false 时子树被下钻并重算——子节点不再自认可点",
            before && !after && reached.Contains(a.Index) && reached.Contains(ac[0].Index));
    }

    // ── 不变量 2：Mark 幂等 O(1) ────────────────────────────────────────────

    private static void MarkIsIdempotent()
    {
        var t = new NodeTable();
        var n = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, n);
        var inv = new Invalidation(t);

        inv.Mark(n, Ch.Transform, InvalidateReason.UserWrite);
        inv.Mark(n, Ch.Transform, InvalidateReason.Timeline);
        inv.Mark(n, Ch.Transform, InvalidateReason.TweenDirect);

        Check("不变量 2：同帧对同 (节点,通道) 连 Mark 三次，队列长度仍为 1（脏位置位即在队）",
            inv.QueueLength(Ch.Transform) == 1
            && inv.Diagnostics.Marks == 3 && inv.Diagnostics.Enqueued == 1 && inv.Diagnostics.Deduped == 2);
    }

    private static void MarkReasonIsMandatory()
    {
        // 编译期断言（写下来就红，故只能以注释形态存在）：
        //     inv.Mark(n, Ch.Transform);                  // ← 不存在这个重载，编译错误
        //     inv.Mark(n, Ch.Transform, reason: null);    // ← InvalidateReason 是值类型，编译错误
        // 机器可验的那一半：Mark/MarkDown 的每个重载都必须带一个**非可选**的 InvalidateReason 参数。
        bool ok = true;
        int seen = 0;
        foreach (MethodInfo m in typeof(Invalidation).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.Name != "Mark" && m.Name != "MarkDown" && m.Name != "RestampOnShow") continue;
            seen++;
            bool has = false;
            foreach (ParameterInfo p in m.GetParameters())
                if (p.ParameterType == typeof(InvalidateReason) && !p.IsOptional && !p.HasDefaultValue) has = true;
            if (!has) ok = false;
        }
        Check("不变量 15：reason 必填——每个 Mark 入口都带非可选的 InvalidateReason（无理由的失效不存在）",
            ok && seen >= 3);
    }

    private static void SevenUpChannelsDoNotCrossTalk()
    {
        var t = new NodeTable();
        var nodes = new NodeHandle[ChMask.UpCount];
        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i] = t.CreateNode(NodeType.Image);
            t.AddChild(t.Root, nodes[i]);
        }
        var inv = new Invalidation(t);

        bool ok = true;
        for (int i = 0; i < nodes.Length; i++)
            inv.Mark(nodes[i], ChMask.UpChannelAt(i), InvalidateReason.UserWrite);

        for (int i = 0; i < nodes.Length; i++)
        {
            Ch c = ChMask.UpChannelAt(i);
            if (inv.QueueLength(c) != 1) ok = false;                     // 每条队列各只收到自己那一条
            if (inv.DirtyOf(nodes[i]) != c) ok = false;                  // 脏字只有本通道位
            for (int j = 0; j < nodes.Length; j++)
                if (j != i && inv.IsDirty(nodes[j], c)) ok = false;      // 别的节点没被串到
        }
        Check("七条上行通道互不串扰：每通道一条队列、脏位一位一属性域", ok);
    }

    private static void DrainIsFifoAndClearsBitsAndQueue()
    {
        var t = new NodeTable();
        var a = t.CreateNode(NodeType.Image);
        var b = t.CreateNode(NodeType.Image);
        var c = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, a); t.AddChild(t.Root, b); t.AddChild(t.Root, c);
        var inv = new Invalidation(t);
        var rec = new DrainRecorder(Ch.Transform);
        inv.Register(rec);

        inv.Mark(a, Ch.Transform, InvalidateReason.UserWrite);
        inv.Mark(b, Ch.Transform, InvalidateReason.UserWrite);
        inv.Mark(c, Ch.Transform, InvalidateReason.UserWrite);
        int n = inv.DrainChannel(Ch.Transform);

        bool fifo = rec.Seen.Count == 3 && rec.Seen[0].Equals(a) && rec.Seen[1].Equals(b) && rec.Seen[2].Equals(c);
        bool cleared = inv.QueueLength(Ch.Transform) == 0
            && !inv.IsDirty(a, Ch.Transform) && !inv.IsDirty(b, Ch.Transform) && !inv.IsDirty(c, Ch.Transform);

        inv.Mark(a, Ch.Transform, InvalidateReason.UserWrite);           // 位已清 ⇒ 能重新入队
        Check("排水：入队序 = Mark 序（FIFO）；消费即清位、排空后队列清零；清位后可再次入队",
            n == 3 && fifo && cleared && inv.QueueLength(Ch.Transform) == 1 && rec.Channels[0] == Ch.Transform);
    }

    private static void DrainDropsStaleHandles()
    {
        var t = new NodeTable();
        var a = t.CreateNode(NodeType.Image);
        var b = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, a); t.AddChild(t.Root, b);
        var inv = new Invalidation(t);
        var rec = new DrainRecorder(Ch.Content);
        inv.Register(rec);

        inv.Mark(a, Ch.Content, InvalidateReason.UserWrite);
        inv.Mark(b, Ch.Content, InvalidateReason.UserWrite);
        t.Destroy(b);                                                    // 入队后死亡：队列里存的是句柄，不是指针
        int n = inv.DrainChannel(Ch.Content);

        Check("队列存句柄不存指针：入队后被 Destroy 的节点在排水时按代际校验丢弃",
            n == 1 && rec.Seen.Count == 1 && rec.Seen[0].Equals(a) && inv.Diagnostics.StaleDropped == 1);
    }

    private static void MarkDuringDrainLandsInNextBatch()
    {
        var t = new NodeTable();
        var a = t.CreateNode(NodeType.Image);
        var x = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, a); t.AddChild(t.Root, x);
        var inv = new Invalidation(t);
        var rec = new DrainRecorder(Ch.Color);
        inv.Register(rec);
        rec.Reentrant = () => inv.Mark(x, Ch.Color, InvalidateReason.LayoutDerived);

        inv.Mark(a, Ch.Color, InvalidateReason.UserWrite);
        int first = inv.DrainChannel(Ch.Color);
        int queuedAfter = inv.QueueLength(Ch.Color);
        rec.Reentrant = null;
        int second = inv.DrainChannel(Ch.Color);

        Check("双缓冲：排水期间的 Mark 落进下一批（不丢、只延迟；也不写穿正在读的快照）",
            first == 1 && queuedAfter == 1 && second == 1
            && rec.Seen.Count == 2 && rec.Seen[0].Equals(a) && rec.Seen[1].Equals(x));
    }

    private static void DrainWithoutDrainerKeepsQueueAndBits()
    {
        var t = new NodeTable();
        var a = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, a);
        var inv = new Invalidation(t);

        inv.Mark(a, Ch.Layout, InvalidateReason.UserWrite);
        int n = inv.DrainChannel(Ch.Layout);                             // Layout 还没有消费者（M1-16 才上线）

        Check("没有注册排水器的通道不排水：脏位与队列原样留到下一次（跨帧存活靠脏位）",
            n == 0 && inv.QueueLength(Ch.Layout) == 1 && inv.IsDirty(a, Ch.Layout)
            && inv.Diagnostics.NoDrainerSkips == 1);
    }

    private static void QueueGrowsBeyondInitialCapacity()
    {
        const int count = 300;                                           // > 初始 256 槽，走一次倍增
        var t = new NodeTable();
        var nodes = new NodeHandle[count];
        for (int i = 0; i < count; i++)
        {
            nodes[i] = t.CreateNode(NodeType.Image);
            t.AddChild(t.Root, nodes[i]);
        }
        var inv = new Invalidation(t);
        var rec = new DrainRecorder(Ch.Text);
        inv.Register(rec);
        for (int i = 0; i < count; i++) inv.Mark(nodes[i], Ch.Text, InvalidateReason.BindingRow);

        int n = inv.DrainChannel(Ch.Text);
        bool ordered = rec.Seen.Count == count;
        for (int i = 0; ordered && i < count; i++) ordered = rec.Seen[i].Equals(nodes[i]);

        Check("环形队列按需倍增（初始 256 槽）：越过容量边界不丢条目、不乱序", n == count && ordered);
    }

    private static void RegisterIsUniquePerChannel()
    {
        var t = new NodeTable();
        var inv = new Invalidation(t);
        var first = new DrainRecorder(Ch.Content | Ch.Transform);
        var second = new DrainRecorder(Ch.Transform);
        inv.Register(first);
        bool dup = AssertFires(() => inv.Register(second));
        bool downRejected = AssertFires(() => inv.Register(new DrainRecorder(Ch.DownColor)));

        Check("排水注册表：一个通道只能有一个消费者；下行通道不许注册排水器（走子树戳）",
            ReferenceEquals(inv.DrainerOf(Ch.Content), first) && (!DebugGates || (dup && downRejected)));
    }

    private static void DrainChannelsBatch()
    {
        var t = new NodeTable();
        var a = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, a);
        var inv = new Invalidation(t);
        var rec = new DrainRecorder(Ch.Content | Ch.Color);
        inv.Register(rec);

        inv.Mark(a, Ch.Content | Ch.Color, InvalidateReason.GearPage);
        int n = inv.DrainChannels(Ch.Content | Ch.Color);

        Check("一次 Mark 携多位 = 每条通道各入一队；批量排水按通道分别回调",
            n == 2 && rec.Calls == 2 && rec.Channels.Contains(Ch.Content) && rec.Channels.Contains(Ch.Color)
            && inv.Diagnostics.Marks == 1 && inv.Diagnostics.Enqueued == 2);
    }

    // ── 不变量 13：下行子树戳 ───────────────────────────────────────────────

    private static void DownStampCoversAncestorChain()
    {
        var t = ChainTree(4, out var chain);
        var side = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, side);                                        // 旁支：不该被戳到
        var inv = new Invalidation(t);
        inv.BeginFrame(7);

        inv.Mark(chain[3], Ch.DownColor, InvalidateReason.UserWrite);
        long stamps = inv.Diagnostics.DownStamps;
        bool chainStamped = inv.IsStampedThisFrame(t.Root) && inv.IsStampedThisFrame(chain[0])
            && inv.IsStampedThisFrame(chain[1]) && inv.IsStampedThisFrame(chain[2])
            && inv.IsStampedThisFrame(chain[3]);
        bool sideClean = !inv.IsStampedThisFrame(side);

        inv.Mark(chain[3], Ch.DownVisible, InvalidateReason.Timeline);   // 本帧已戳 ⇒ 上溯立刻停
        Check("下行置戳：自身 + 祖先链盖本帧戳（P6 从根按戳路由），旁支不戳；本帧已戳即停",
            stamps == 5 && chainStamped && sideClean
            && inv.Diagnostics.DownStamps == 5 && inv.Diagnostics.DownStampStops == 1);
    }

    private static void DownDrainRoutesOnlyStampedBranch()
    {
        var t = ForkTree(out var a, out var ac, out _, out var bc);
        var inv = new Invalidation(t);
        inv.BeginFrame(3);
        inv.Mark(a, Ch.DownColor, InvalidateReason.UserWrite);

        var seen = new List<uint>();
        t.Phase = FramePhase.P6_Settle;
        int visited = inv.DrainDown((idx, bits) => seen.Add(idx));

        bool onlyA = seen.Count == 3 && seen[0] == a.Index
            && seen.Contains(ac[0].Index) && seen.Contains(ac[1].Index)
            && !seen.Contains(bc[0].Index) && !seen.Contains(bc[1].Index);
        Check("不变量 13：P6 只下钻 stamp==frameId 的分支；下钻根的整棵子树重算，未戳分支整棵不看",
            visited == 3 && onlyA && !inv.IsDirty(a, Ch.DownColor));
    }

    private static void DownDrainSkipsHiddenSubtree()
    {
        var t = ForkTree(out var a, out var ac, out _, out _);
        var inv = new Invalidation(t);
        inv.BeginFrame(1);
        t.SetVisible(a, false);                                          // setter 自带 Visible|DownVisible

        var seen = new List<uint>();
        t.Phase = FramePhase.P6_Settle;
        int visited = inv.DrainDown((idx, bits) => seen.Add(idx));

        Check("隐藏子树免下钻：转不可见的节点自身重算一次，其子树整棵跳过",
            visited == 1 && seen.Count == 1 && seen[0] == a.Index
            && !seen.Contains(ac[0].Index) && inv.Diagnostics.HiddenSkips == 1);
    }

    private static void HiddenSubtreeRedrilledOnShow()
    {
        var t = ForkTree(out var a, out var ac, out _, out _);
        var inv = new Invalidation(t);
        inv.BeginFrame(1);
        t.SetVisible(a, false);
        t.Phase = FramePhase.P6_Settle;
        inv.DrainDown((idx, bits) => { });

        // 帧 2：隐藏期间子树内部改 α —— 置戳照做，但下钻在隐藏节点处停住（无人读隐藏子树的级联值）
        t.Phase = FramePhase.P3_Commands;
        inv.BeginFrame(2);
        t.SetAlpha(ac[0], 0.5f);
        t.Phase = FramePhase.P6_Settle;
        var hidden = new List<uint>();
        int whileHidden = inv.DrainDown((idx, bits) => hidden.Add(idx));

        // 帧 3：重新显示 —— Visible 写口的 DownVisible 位给整棵子树补一次下钻
        t.Phase = FramePhase.P3_Commands;
        inv.BeginFrame(3);
        t.SetVisible(a, true);
        t.Phase = FramePhase.P6_Settle;
        var shown = new List<uint>();
        int onShow = inv.DrainDown((idx, bits) => shown.Add(idx));

        Check("不变量 13：隐藏期间的下行变化不下钻；重新显示时由 Visible 通道补戳，整棵子树一次补算",
            whileHidden == 0 && onShow == 3 && shown[0] == a.Index
            && shown.Contains(ac[0].Index) && shown.Contains(ac[1].Index));
    }

    private static void DownChannelsNeverEnterQueues()
    {
        var t = ChainTree(3, out var chain);
        var inv = new Invalidation(t);
        inv.BeginFrame(5);
        inv.Mark(chain[2], Ch.DownColor | Ch.DownVisible | Ch.DownLayer, InvalidateReason.GearPage);

        Check("方向语义：下行通道不入任何上行队列（只置位 + 置戳）",
            TotalQueued(inv) == 0 && inv.Diagnostics.Enqueued == 0
            && inv.IsDirty(chain[2], Ch.DownLayer) && inv.IsStampedThisFrame(t.Root));
    }

    // ── M1-14b 修复 1：走查期间的 MarkDown 不许「出生即过期」 ────────────────
    //
    // 2026-08 审计（BUSY 场景实测）：DrainDown 趟末把正被消费的代作废；走查回调里发出的
    // MarkDown 盖的正是这一代——目标分支已出栈时，下一趟从根第一步就判「本代无事」，
    // 脏位搁浅到该节点被无关原因再次戳中为止（visited=1、分支永久滞留）。
    // M1-14 消灭的「出生即过期」换了个位置复发；修法是走查期间置戳入挂起集、趟末按新代补戳。

    private static void MarkDownDuringDrainLandsInNextGeneration()
    {
        var t = new NodeTable();
        var d = t.CreateNode(NodeType.Component);
        var dc = t.CreateNode(NodeType.Image);
        var e = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, d);
        t.AddChild(d, dc);
        t.AddChild(t.Root, e);                       // 兄弟序：d 先于 e 出栈
        var inv = new Invalidation(t);

        inv.BeginFrame(1);
        inv.Mark(e, Ch.DownColor, InvalidateReason.UserWrite);
        t.Phase = FramePhase.P6_Settle;
        int busy = inv.DrainDown((idx, bits) =>
        {
            // 走查回调（BUSY 窗口）：对已出栈的 d 分支发下行 Mark
            if (idx == e.Index) inv.MarkDown(d, Ch.DownColor, InvalidateReason.CascadeDown);
        });

        t.Phase = FramePhase.P3_Commands;
        inv.BeginFrame(2);
        t.Phase = FramePhase.P6_Settle;
        var seen = new List<uint>();
        int next = inv.DrainDown((idx, bits) => seen.Add(idx));

        Check("修复 1：走查期间对已出栈分支的 MarkDown 落进下一代——下一趟整支被下钻到，脏位不搁浅",
            busy == 1 && next == 2 && seen.Contains(d.Index) && seen.Contains(dc.Index)
            && !inv.IsDirty(d, Ch.DownColor));
    }

    // ── M1-14b 修复 2：重接父不许打破「已戳 ⇒ 祖先全已戳」 ──────────────────

    private static void ReattachedStampedSubtreeIsRedrilled()
    {
        var t = new NodeTable();
        var p1 = t.CreateNode(NodeType.Component);
        var p2 = t.CreateNode(NodeType.Component);
        var s = t.CreateNode(NodeType.Component);
        var x = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, p1); t.AddChild(t.Root, p2);
        t.AddChild(p1, s); t.AddChild(s, x);
        var inv = new Invalidation(t);
        inv.BeginFrame(1);
        t.Phase = FramePhase.P6_Settle;
        inv.DrainDown((idx, bits) => { });           // 定形一代

        // 审计场景原样：帧外先写 α（s 戳当前代），再把已戳子树挂到未戳的 p2 下
        t.Phase = FramePhase.P3_Commands;
        inv.BeginFrame(2);
        t.SetAlpha(s, 0.5f);
        t.AddChild(p2, s);

        t.Phase = FramePhase.P6_Settle;
        var seen = new List<uint>();
        int visited = inv.DrainDown((idx, bits) => seen.Add(idx));

        Check("修复 2：已戳子树重接到未戳父下，重接钩子经 RestampOnShow 补挂——本代照常下钻整支，脏位不丢",
            visited >= 2 && seen.Contains(s.Index) && seen.Contains(x.Index)
            && !inv.IsDirty(s, Ch.DownColor));
    }

    private static void DetachedWritesSurviveReattachAcrossGenerations()
    {
        var t = new NodeTable();
        var p1 = t.CreateNode(NodeType.Component);
        var p2 = t.CreateNode(NodeType.Component);
        var s = t.CreateNode(NodeType.Component);
        var x = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, p1); t.AddChild(t.Root, p2);
        t.AddChild(p1, s); t.AddChild(s, x);
        var inv = new Invalidation(t);

        t.Phase = FramePhase.P3_Commands;
        inv.BeginFrame(1);
        t.RemoveFromParent(s);                       // 摘链
        t.SetAlpha(x, 0.5f);                         // 树外写：置戳只到摘链根 s 为止
        t.Phase = FramePhase.P6_Settle;
        int offTree = inv.DrainDown((idx, bits) => { });   // s 不在树上，这一代等不到它；代被作废

        t.Phase = FramePhase.P3_Commands;
        inv.BeginFrame(2);
        t.AddChild(p2, s);                           // 隔代接回：子树内部的戳停留在旧代
        t.Phase = FramePhase.P6_Settle;
        var seen = new List<uint>();
        int visited = inv.DrainDown((idx, bits) => seen.Add(idx));

        Check("修复 2：摘链期间的下行写在隔代接回后仍被收走（补挂整棵子树，只补父链救不了内部旧戳）",
            offTree == 0 && visited >= 2 && seen.Contains(x.Index)
            && !inv.IsDirty(x, Ch.DownColor));
    }

    // ── M1-14b 修复 3：代号 u32 回绕整列清戳 ────────────────────────────────

    private static void EpochWrapClearsStaleStamps()
    {
        var t = new NodeTable();
        var a = t.CreateNode(NodeType.Component);
        var b = t.CreateNode(NodeType.Image);
        var c = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, a); t.AddChild(a, b); t.AddChild(t.Root, c);
        var inv = new Invalidation(t);
        t.Phase = FramePhase.P6_Settle;

        // 制造「陈年戳 == 回绕后的新代号」的撞车地形：a/b 停留在代 1 的戳，root 被代 5 盖过。
        inv.Mark(b, Ch.DownColor, InvalidateReason.UserWrite);   // 代 1：戳 b,a,root
        inv.DrainDown((idx, bits) => { });                        // 消费，代 → 2
        inv.ForceStampEpochForTest(5);
        inv.Mark(c, Ch.DownColor, InvalidateReason.UserWrite);   // 代 5：戳 c,root（a/b 仍是 1）
        inv.DrainDown((idx, bits) => { });                        // 消费，代 → 6

        inv.ForceStampEpochForTest(uint.MaxValue);
        inv.DrainDown((idx, bits) => { });                        // 空趟：回绕，代 → 1 并整列清戳

        // 不清列的话：b 的陈年戳 1 == 新代 1，「本代已戳即停」在 b 上骗停，root（戳 5）不被盖——
        // 从根路由第一步就死，b 的 DownColor 永久搁浅（审计：「最坏只多一次冗余下钻」不成立）。
        inv.Mark(b, Ch.DownColor, InvalidateReason.UserWrite);
        var seen = new List<uint>();
        int visited = inv.DrainDown((idx, bits) => seen.Add(idx));

        Check("修复 3：代号回绕整列清戳——陈年戳撞上新代号骗停置戳的形状在结构上不存在",
            visited == 1 && seen.Contains(b.Index) && !inv.IsDirty(b, Ch.DownColor)
            && inv.FrameStamp == 2);
    }

    // ── BoundsD：置父链、遇脏即停、query-pull ───────────────────────────────

    private static void BoundsStopsAtDirtyAncestor()
    {
        var t = ChainTree(4, out var chain);                             // root → 0 → 1 → 2 → 3
        var inv = new Invalidation(t);

        inv.Mark(chain[3], Ch.BoundsD, InvalidateReason.LayoutDerived);
        long steps1 = inv.Diagnostics.BoundsSteps;
        bool allDirty = inv.IsBoundsDirty(chain[3]) && inv.IsBoundsDirty(chain[2])
            && inv.IsBoundsDirty(chain[1]) && inv.IsBoundsDirty(chain[0]) && inv.IsBoundsDirty(t.Root);

        inv.Mark(chain[1], Ch.BoundsD, InvalidateReason.LayoutDerived);  // 已脏 ⇒ 一步都不走
        long steps2 = inv.Diagnostics.BoundsSteps;

        Check("BoundsD：自身+父链逐级置位，遇脏即停（父链访问次数可查），且不入队列",
            steps1 == 5 && allDirty && steps2 == 5 && inv.Diagnostics.BoundsStops == 1
            && TotalQueued(inv) == 0);
    }

    private static void BoundsConsumeClearsSubtree()
    {
        var t = new NodeTable();
        var a = t.CreateNode(NodeType.Component);
        var b = t.CreateNode(NodeType.Image);
        var c = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, a); t.AddChild(a, b); t.AddChild(a, c);
        var inv = new Invalidation(t);

        inv.Mark(c, Ch.BoundsD, InvalidateReason.LayoutDerived);         // c → a → root 置脏，b 不脏
        bool pulled = inv.ConsumeBounds(a);                              // query-pull：清整棵 a 子树
        bool subtreeClean = !inv.IsBoundsDirty(a) && !inv.IsBoundsDirty(b) && !inv.IsBoundsDirty(c);
        bool rootStillDirty = inv.IsBoundsDirty(t.Root);

        long before = inv.Diagnostics.BoundsSteps;
        inv.Mark(c, Ch.BoundsD, InvalidateReason.LayoutDerived);         // 清过之后再脏：能一路走回 a
        long walked = inv.Diagnostics.BoundsSteps - before;

        Check("BoundsD query-pull：消费清整棵子树（维持「脏 ⇒ 祖先全脏」封闭性），root 由自己的消费者清",
            pulled && subtreeClean && rootStillDirty && walked == 2
            && inv.IsBoundsDirty(a) && inv.Diagnostics.BoundsPulled == 1);
    }

    // ── 不变量 5：全局失效只在 P2 ───────────────────────────────────────────

    private static void GlobalInvalidateOnlyAppliesAtP2()
    {
        var t = new NodeTable();
        var inv = new Invalidation(t);
        int glyph = 0;
        inv.GlyphStoreFanOut = () => glyph++;

        t.Phase = FramePhase.P4_State;
        inv.RequestGlobalInvalidate(GlobalSource.GlyphStore);            // 任何时刻只挂起
        bool pendingNotApplied = glyph == 0 && inv.IsGlobalPending(GlobalSource.GlyphStore);
        bool gate = AssertFires(() => inv.ApplyPendingGlobal());         // 非 P2 生效 = 断言 + 真拒绝
        bool stillPending = glyph == 0 && inv.HasPendingGlobal;

        t.Phase = FramePhase.P2_GlobalInvalidate;
        int applied = inv.ApplyPendingGlobal();
        int again = inv.ApplyPendingGlobal();

        Check("不变量 5：RequestGlobalInvalidate 只挂起，生效点只有 P2；生效后挂起集清空",
            pendingNotApplied && stillPending && applied == 1 && glyph == 1 && again == 0
            && !inv.HasPendingGlobal && (!DebugGates || gate));
    }

    private static void GlobalSourcesAreIndependent()
    {
        var t = new NodeTable();
        var inv = new Invalidation(t);
        int glyph = 0, screen = 0;
        inv.GlyphStoreFanOut = () => glyph++;
        inv.ScreenSizeFanOut = () => screen++;

        inv.RequestGlobalInvalidate(GlobalSource.ScreenSize);
        t.Phase = FramePhase.P2_GlobalInvalidate;
        int applied = inv.ApplyPendingGlobal();

        Check("封闭两源各自挂起：只请求 ScreenSize 不会连带触发字形库换代",
            applied == 1 && screen == 1 && glyph == 0 && !inv.IsGlobalPending(GlobalSource.GlyphStore));
    }

    private static void GlobalFanOutMarksWithGlobalReason()
    {
        var t = new NodeTable();
        var text = t.CreateNode(NodeType.Text);
        t.AddChild(t.Root, text);
        var inv = new Invalidation(t);
        // fan-out 归源自己执行（本平面不建订阅注册表）：字形库对绑定期登记的订阅节点定向 Mark。
        inv.GlyphStoreFanOut = () => inv.Mark(text, Ch.Text, InvalidateReason.GlobalInvalidate);

        inv.RequestGlobalInvalidate(GlobalSource.GlyphStore);
        t.Phase = FramePhase.P2_GlobalInvalidate;
        inv.ApplyPendingGlobal();

        Check("全局失效的 fan-out 由源执行，Mark 携 reason=GlobalInvalidate（延迟表里唯一的跨帧行）",
            inv.QueueLength(Ch.Text) == 1 && inv.IsDirty(text, Ch.Text)
            && inv.Diagnostics.MarksOf(InvalidateReason.GlobalInvalidate) == 1
            && inv.Diagnostics.GlobalApplied == 1 && inv.Diagnostics.GlobalRequests == 1);
    }

    // ── 机制⑫：诊断 ────────────────────────────────────────────────────────

    private static void DiagnosticsAggregateByChannelAndReason()
    {
        var t = ChainTree(2, out var chain);
        var inv = new Invalidation(t);
        inv.BeginFrame(11);

        inv.Mark(chain[0], Ch.Transform, InvalidateReason.UserWrite);
        inv.Mark(chain[1], Ch.Transform, InvalidateReason.TweenDirect);
        inv.Mark(chain[1], Ch.Color | Ch.DownColor, InvalidateReason.GearPage);
        inv.Mark(chain[0], Ch.Text, InvalidateReason.BindingRow);

        var d = inv.Diagnostics;
        Check("机制⑫：每通道 Mark 计数 + 按 reason 聚合（回答「这个 quad 本帧为何重写」）",
            d.Marks == 4
            && d.MarksOf(Ch.Transform) == 2 && d.MarksOf(Ch.Color) == 1 && d.MarksOf(Ch.DownColor) == 1
            && d.MarksOf(Ch.Text) == 1 && d.MarksOf(Ch.Structure) == 0
            && d.MarksOf(InvalidateReason.UserWrite) == 1 && d.MarksOf(InvalidateReason.TweenDirect) == 1
            && d.MarksOf(InvalidateReason.GearPage) == 1 && d.MarksOf(InvalidateReason.BindingRow) == 1
            && d.MarksOf(InvalidateReason.Timeline) == 0);
    }

    private static void DiagnosticsLatchAtFrameEnd()
    {
        var t = new NodeTable();
        var n = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, n);
        var inv = new Invalidation(t);
        inv.Mark(n, Ch.Content, InvalidateReason.UserWrite);
        inv.LatchFrame();                                                // P9 锁存

        Check("P9 诊断锁存：本帧计数进 LastFrame 并清零（帧计数不越帧累加）",
            inv.LastFrame.Marks == 1 && inv.LastFrame.MarksOf(Ch.Content) == 1
            && inv.Diagnostics.Marks == 0 && inv.Diagnostics.MarksOf(Ch.Content) == 0
            && inv.QueueLength(Ch.Content) == 1);                        // 队列不受锁存影响
    }

    // ── M1-06 接线：等值切断 × Mark 的联合行为 ─────────────────────────────

    private static void EqualityCutoffJoinsMark()
    {
        var t = new NodeTable();
        var n = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, n);
        var inv = new Invalidation(t);

        t.SetPosition(n, 3f, 4f);                                        // 变了：一次 Mark
        long afterFirst = inv.Diagnostics.Marks;
        t.SetPosition(n, 3f, 4f);                                        // 同值：切断在 setter 侧，钩子根本不被调
        t.SetSize(n, 0f, 0f);
        t.SetVisible(n, true);
        long afterSame = inv.Diagnostics.Marks;

        t.SetSize(n, 200f, 100f, WriteSource.Layout);                    // 变了：来源 → reason 分流
        var d = inv.Diagnostics;

        Check("等值切断 × Mark 联合：写同值零 Mark；写不同值一次 Mark，通道与 reason 正确",
            afterFirst == 1 && afterSame == 1 && d.Marks == 2
            && d.MarksOf(InvalidateReason.UserWrite) == 1 && d.MarksOf(InvalidateReason.LayoutDerived) == 1
            && inv.QueueLength(Ch.Transform) == 1 && inv.QueueLength(Ch.Layout) == 1
            && inv.DirtyOf(n) == (Ch.Transform | Ch.Layout));
    }

    private static void AlphaMarksUpAndDownInOneCall()
    {
        var t = ChainTree(2, out var chain);
        var inv = new Invalidation(t);
        inv.BeginFrame(9);

        t.SetAlpha(chain[1], 0.5f);                                      // Ch.Color | Ch.DownColor

        Check("一次写同时给上行与下行位：Color 入队、DownColor 置戳，两条路各按方向语义处理",
            inv.QueueLength(Ch.Color) == 1 && inv.IsDirty(chain[1], Ch.DownColor)
            && inv.IsStampedThisFrame(chain[1]) && inv.IsStampedThisFrame(chain[0])
            && inv.IsStampedThisFrame(t.Root) && inv.Diagnostics.Marks == 1);
    }

    // ── 不变量 9 / 机制⑫的调用点记录 ───────────────────────────────────────

    private static void MarkInRenderDrainAssertsButKeepsWrite()
    {
        var t = new NodeTable();
        var n = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, n);
        var inv = new Invalidation(t);
        t.Phase = FramePhase.P7_RenderDrain;

        bool fired = AssertFires(() => inv.Mark(n, Ch.Content, InvalidateReason.UserWrite));
        bool kept = inv.QueueLength(Ch.Content) == 1 && inv.IsDirty(n, Ch.Content);
        t.Phase = FramePhase.P3_Commands;

        Check("不变量 9：P7/P8 的 Mark 在 debug 断言，但记录照做——降级是延迟一帧排水，不是丢弃",
            kept && (!DebugGates || fired));
    }

    private static void MarkSiteRingRecordsCallSite()
    {
        var t = new NodeTable();
        var n = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, n);
        var inv = new Invalidation(t);
        inv.BeginFrame(42);
        inv.Mark(n, Ch.Structure, InvalidateReason.GearPage);

        bool recorded = inv.SiteCount == 1;
        if (recorded)
        {
            MarkSite s = inv.SiteAt(0);
            recorded = s.Index == n.Index && s.Channels == Ch.Structure
                && s.Reason == InvalidateReason.GearPage && s.Frame == 42
                && s.Member == nameof(MarkSiteRingRecordsCallSite) && s.Line > 0;
        }
        Check("机制⑫：debug 构建把调用点记进环形缓冲（release 构建整体剔除，计数恒 0）",
            DebugGates ? recorded : inv.SiteCount == 0);
    }
}
