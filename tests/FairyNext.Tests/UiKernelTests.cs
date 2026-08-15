using FairyNext.Contracts;
using FairyNext.Core;

namespace FairyNext.Tests;

/// <summary>
/// M1-08 相位机 + Clock 用例（Program 的 partial 分片）。
/// 逐条对齐 docs/architecture.md「平面二 · 时间宪法」不变量清单：
/// 9 P7/P8 禁改（debug 断言 + release 入下帧队列，不丢只延迟）/ 10 波次有界 + 责任链 /
/// 11 句柄换代只在 P9、TimerHandle 代际 Cancel 静默 no-op / 14 推进序确定性（(deadline, 插入序) 双键），
/// 外加相位表 P0–P9 的执行序、PostInput 单入口只在 P0 排空、FrameDiag 的 P9 锁存时点。
/// </summary>
public static partial class Program
{
    // ── 工具与夹具 ──────────────────────────────────────────────────────────

    /// <summary>P3 波次测试用命令：执行时按 <see cref="Remaining"/> 再生一条后继。</summary>
    private struct SpawnCommand
    {
        public int Id;
        public int Remaining;
    }

    private static FrameTime Frame0(float dt = 0.016f) => FrameTime.First(dt, dt);

    /// <summary>建「root → n」的最小树并接上内核。</summary>
    private static UiKernel KernelWithLeaf(out NodeTable table, out NodeHandle leaf)
    {
        table = new NodeTable();
        leaf = table.CreateNode(NodeType.Image);
        table.AddChild(table.Root, leaf);
        return new UiKernel(table);
    }

    private static void UiKernelSuite()
    {
        PhaseOrderIsP0ToP9EachExactlyOnce();
        PhaseDrivesTableAndFrameOutsideIsP3();
        FrameIdMonotonicWithFrameHooks();
        TickIsNotReentrant();
        PhaseDriverIsExclusive();

        WriteGateAssertsInRenderDrain();
        WriteGateDefersMarkToNextFrame();
        WriteGateDefersCrossChannelMarkInP7();

        CommandWavesRunUntilQuiet();
        CommandWaveLimitDefersOverflow();
        CommandOverflowCarriesResponsibilityChain();
        DeferredCommandsResumeNextFrame();

        ScaledAndUnscaledDomainsDiffer();
        ManualDomainAdvancesOnlyByExplicitStep();
        TimersFireByDeadlineThenInsertionOrder();
        TimerHandleGenerationMakesStaleCancelNoOp();
        EveryRepeatsAndCancelsFromInsideCallback();
        TimersFireInP1();

        PostInputDrainsOnlyInP0();
        InputTapIsTheSingleRecordingHook();

        FrameDiagLatchesAtP9();
        GlobalInvalidateAppliesInP2();
        SettleAppliesStructureAndDrillsDown();
        HandleGenerationBumpsOnlyAtP9();
        DrainerReceivesFrameContext();
    }

    // ── 相位序 ──────────────────────────────────────────────────────────────

    private static void PhaseOrderIsP0ToP9EachExactlyOnce()
    {
        var t = new NodeTable();
        var k = new UiKernel(t);
        var seen = new List<FramePhase>();
        k.PhaseWatch = p => seen.Add(p);

        k.Tick(Frame0());

        bool ordered = seen.Count == 10;
        for (int i = 0; ordered && i < seen.Count; i++) ordered = (int)seen[i] == i;
        Check("相位表：一帧走完 P0→P9，各恰好一次且顺序正确", ordered);
    }

    private static void PhaseDrivesTableAndFrameOutsideIsP3()
    {
        var t = new NodeTable();
        var k = new UiKernel(t);
        bool mismatch = false;
        k.PhaseWatch = p =>
        {
            if (t.Phase != p || k.CurrentPhase != p) mismatch = true;
        };

        k.Tick(Frame0());

        Check("相位机独占驱动 NodeTable.Phase；帧外窗口报告 P3（写入自由的重入契约）",
            !mismatch && k.CurrentPhase == FramePhase.P3_Commands && !k.InFrame);
    }

    private static void FrameIdMonotonicWithFrameHooks()
    {
        var t = new NodeTable();
        var k = new UiKernel(t);
        var trace = new List<string>();
        k.BeforeFrame += id => trace.Add("before" + id);
        k.AfterFrame += id => trace.Add("after" + id);
        k.PhaseWatch = p =>
        {
            if (p == FramePhase.P0_Input) trace.Add("P0");
            if (p == FramePhase.P9_FrameEnd) trace.Add("P9");
        };

        FrameTime ft = Frame0();
        k.Tick(ft);
        ft = ft.Step(0.016f, 0.016f);
        k.Tick(ft);

        bool order = trace.Count == 8
            && trace[0] == "before1" && trace[1] == "P0" && trace[2] == "P9" && trace[3] == "after1"
            && trace[4] == "before2" && trace[5] == "P0" && trace[6] == "P9" && trace[7] == "after2";
        Check("frameId 单调递增（首帧 = 1）；BeforeFrame 在 P0 前、AfterFrame 在 P9 后各一次",
            order && k.FrameId == 2);
    }

    private static void TickIsNotReentrant()
    {
        var t = new NodeTable();
        var k = new UiKernel(t);
        int phases = 0;
        k.PhaseWatch = _ => phases++;
        k.BeforeFrame += _ => k.Tick(Frame0());          // 钩子里再 Tick = 违约

        bool fired = AssertFires(() => k.Tick(Frame0()));

        Check("相位机不可重入：钩子里再 Tick 被真拒绝（帧号只走一格、相位只走一轮）",
            k.FrameId == 1 && phases == 10 && (!DebugGates || fired));
    }

    private static void PhaseDriverIsExclusive()
    {
        var t = new NodeTable();
        var k = new UiKernel(t);
        bool fired = AssertFires(() => t.Phase = FramePhase.P7_RenderDrain);

        Check("接了内核的树域不许旁路设相位：断言 + 真拒绝（旁路 = P7/P8 写门可被绕过）",
            t.Phase == FramePhase.P3_Commands && k.CurrentPhase == FramePhase.P3_Commands
            && (!DebugGates || fired));
    }

    // ── 不变量 9：P7/P8 写门（debug 断言 + release 入下帧队列）────────────────

    private static void WriteGateAssertsInRenderDrain()
    {
        UiKernel k = KernelWithLeaf(out _, out NodeHandle leaf);
        k.SubmitStep = (ref FrameContext ctx) =>
            ctx.Invalidation.Mark(leaf, Ch.Content, InvalidateReason.UserWrite);

        bool fired = AssertFires(() => k.Tick(Frame0()));

        Check("不变量 9（debug 面）：P8 里的 authored 写响门，并计入 phaseViolation",
            k.Diagnostics.PhaseViolations == 1 && (!DebugGates || fired));
    }

    private static void WriteGateDefersMarkToNextFrame()
    {
        UiKernel k = KernelWithLeaf(out _, out NodeHandle leaf);
        var rec = new DrainRecorder(Ch.Content);
        k.Invalidation.Register(rec);
        int frame = 0;
        k.SubmitStep = (ref FrameContext ctx) =>
        {
            if (frame == 1) ctx.Invalidation.Mark(leaf, Ch.Content, InvalidateReason.UserWrite);
        };

        FrameTime ft = Frame0();
        frame = 1;
        AssertFires(() => k.Tick(ft));                   // 违约写发生在本帧 P8
        bool notThisFrame = rec.Seen.Count == 0;
        long deferred = k.Diagnostics.DeferredMarks;

        frame = 2;
        k.Tick(ft.Step(0.016f, 0.016f));                 // 下一帧照常排水

        Check("不变量 9（release 面）：P8 的 Mark 不丢、只延迟——本帧不排，下一帧 P7 排到",
            notThisFrame && rec.Seen.Count == 1 && rec.Seen[0].Equals(leaf)
            && rec.Phases[0] == FramePhase.P7_RenderDrain && deferred == 0);
    }

    private static void WriteGateDefersCrossChannelMarkInP7()
    {
        var t = new NodeTable();
        var a = t.CreateNode(NodeType.Image);
        var b = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, a);
        t.AddChild(t.Root, b);
        var k = new UiKernel(t);
        var content = new DrainRecorder(Ch.Content);
        var transform = new DrainRecorder(Ch.Transform);
        k.Invalidation.Register(content);
        k.Invalidation.Register(transform);
        // content 排水器在 P7 里写 transform：若不冻结水位，同帧后半段就被消化了
        content.Reentrant = () => k.Invalidation.Mark(b, Ch.Transform, InvalidateReason.UserWrite);
        k.Invalidation.Mark(a, Ch.Content, InvalidateReason.UserWrite);

        FrameTime ft = Frame0();
        AssertFires(() => k.Tick(ft));
        bool sameFrameClean = content.Seen.Count == 1 && transform.Seen.Count == 0;
        long deferredEntries = k.Diagnostics.DeferredMarks;

        content.Reentrant = null;
        k.Tick(ft.Step(0.016f, 0.016f));

        Check("不变量 9：P7 内跨通道的连锁失效也被冻结在水位以上，下一帧才排（不丢、只延迟）",
            sameFrameClean && deferredEntries == 1
            && transform.Seen.Count == 1 && transform.Seen[0].Equals(b));
    }

    // ── 不变量 10：P3 波次有界 + 责任链 ─────────────────────────────────────

    private static CommandPump<SpawnCommand> SpawnPump(UiKernel kernel,
        List<int> executed, List<int> waves, List<FramePhase> phases)
    {
        CommandPump<SpawnCommand>? pump = null;
        pump = new CommandPump<SpawnCommand>(
            (in SpawnCommand c, ref FrameContext ctx) =>
            {
                executed.Add(c.Id);
                waves.Add(ctx.Wave);
                phases.Add(ctx.Phase);
                if (c.Remaining > 0)
                    pump!.Enqueue(new SpawnCommand { Id = c.Id + 1, Remaining = c.Remaining - 1 });
            },
            c => "cmd" + c.Id, "spawn");
        kernel.RegisterCommandPump(pump);
        return pump;
    }

    private static void CommandWavesRunUntilQuiet()
    {
        var t = new NodeTable();
        var k = new UiKernel(t);
        var executed = new List<int>();
        var waves = new List<int>();
        var phases = new List<FramePhase>();
        CommandPump<SpawnCommand> pump = SpawnPump(k, executed, waves, phases);
        pump.Enqueue(new SpawnCommand { Id = 0, Remaining = 2 });

        k.Tick(Frame0());
        FrameDiag d = k.Diagnostics;

        bool waveNumbers = waves.Count == 3 && waves[0] == 0 && waves[1] == 1 && waves[2] == 2;
        bool inP3 = phases.Count == 3 && phases.TrueForAll(p => p == FramePhase.P3_Commands);
        Check("P3：命令按波次排空到静止（波内新入队的构成下一波），全部在 P3 内执行",
            d.CommandWaves == 3 && d.CommandsExecuted == 3 && !d.CommandWaveOverflow
            && d.CommandsDeferred == 0 && waveNumbers && inP3 && pump.PendingCount == 0);
    }

    private static void CommandWaveLimitDefersOverflow()
    {
        var t = new NodeTable();
        var k = new UiKernel(t);
        var executed = new List<int>();
        CommandPump<SpawnCommand> pump = SpawnPump(k, executed, new List<int>(), new List<FramePhase>());
        pump.Enqueue(new SpawnCommand { Id = 0, Remaining = 99 });    // 自我再生的命令

        k.Tick(Frame0());
        FrameDiag d = k.Diagnostics;

        Check("不变量 10：波次撞上 Abi.CommandWaveLimit 即停，余量延帧（在队不丢）",
            Abi.CommandWaveLimit == 4 && d.CommandWaves == Abi.CommandWaveLimit
            && d.CommandsExecuted == Abi.CommandWaveLimit && d.CommandWaveOverflow
            && d.CommandsDeferred == 1 && pump.PendingCount == 1);
    }

    private static void CommandOverflowCarriesResponsibilityChain()
    {
        var t = new NodeTable();
        var k = new UiKernel(t);
        CommandPump<SpawnCommand> pump =
            SpawnPump(k, new List<int>(), new List<int>(), new List<FramePhase>());
        pump.Enqueue(new SpawnCommand { Id = 0, Remaining = 99 });

        k.Tick(Frame0());
        FrameDiag d = k.Diagnostics;

        bool chainOk = d.ChainCount == 4;
        for (int i = 0; chainOk && i < d.ChainCount; i++)
        {
            CommandLink link = d.ChainAt(i);
            chainOk = link.Wave == i
                && link.ParentLabel == "cmd" + i && link.ChildLabel == "cmd" + (i + 1)
                && link.ParentSeq == i && link.ChildSeq == i + 1;
        }
        Check("不变量 10：超限诊断必携责任链——逐波指认「哪条命令生成了下一波的哪条」",
            d.CommandWaveOverflow && chainOk);
    }

    private static void DeferredCommandsResumeNextFrame()
    {
        var t = new NodeTable();
        var k = new UiKernel(t);
        var executed = new List<int>();
        CommandPump<SpawnCommand> pump = SpawnPump(k, executed, new List<int>(), new List<FramePhase>());
        pump.Enqueue(new SpawnCommand { Id = 0, Remaining = 99 });

        FrameTime ft = Frame0();
        k.Tick(ft);
        int afterFirst = executed.Count;
        k.Tick(ft.Step(0.016f, 0.016f));

        bool contiguous = executed.Count == 8;
        for (int i = 0; contiguous && i < executed.Count; i++) contiguous = executed[i] == i;
        Check("超限余量延帧继续：下一帧从断点接着跑，命令序连续不丢",
            afterFirst == 4 && contiguous && k.Diagnostics.CommandsExecuted == 4);
    }

    // ── 机制⑪：三时域 Clock ─────────────────────────────────────────────────

    private static void ScaledAndUnscaledDomainsDiffer()
    {
        var t = new NodeTable();
        var k = new UiKernel(t);
        int scaled = 0, unscaled = 0;
        k.Clock.After(0.5f, TimeDomain.Scaled, _ => scaled++);
        k.Clock.After(0.5f, TimeDomain.Unscaled, _ => unscaled++);

        // timeScale = 0.5：真实 0.4s/帧，缩放 0.2s/帧
        FrameTime ft = FrameTime.First(0.2f, 0.4f);
        k.Tick(ft);
        bool noneYet = scaled == 0 && unscaled == 0;

        ft = ft.Step(0.2f, 0.4f);
        k.Tick(ft);                                     // uNow=0.8 ≥ 0.5；sNow=0.4 < 0.5
        bool onlyUnscaled = unscaled == 1 && scaled == 0;

        ft = ft.Step(0.2f, 0.4f);
        k.Tick(ft);                                     // sNow=0.6 ≥ 0.5

        Check("三时域：Unscaled 按真实 dt 推进、Scaled 吃 timeScale——同一延时的两个定时器分帧到期",
            noneYet && onlyUnscaled && scaled == 1 && unscaled == 1
            && k.Clock.Now(TimeDomain.Unscaled) > k.Clock.Now(TimeDomain.Scaled));
    }

    private static void ManualDomainAdvancesOnlyByExplicitStep()
    {
        var t = new NodeTable();
        var k = new UiKernel(t);
        int fired = 0;
        k.Clock.After(1f, TimeDomain.Manual, _ => fired++);

        FrameTime ft = FrameTime.First(10f, 10f);       // 宿主时间飞过 10 秒
        k.Tick(ft);
        bool untouched = fired == 0 && k.Clock.Now(TimeDomain.Manual) == 0.0;

        k.Clock.StepManual(1.0);                        // 唯一的推进方式
        k.Tick(ft.Step(0.016f, 0.016f));

        Check("Manual 时域只由 StepManual 推进（回放门的前提：宿主时钟污染不了它）",
            untouched && fired == 1 && k.Clock.Now(TimeDomain.Manual) == 1.0);
    }

    private static void TimersFireByDeadlineThenInsertionOrder()
    {
        var t = new NodeTable();
        var k = new UiKernel(t);
        var log = new List<string>();
        k.Clock.After(1f, TimeDomain.Unscaled, _ => log.Add("a"));   // 同 deadline，先插
        k.Clock.After(1f, TimeDomain.Unscaled, _ => log.Add("b"));   // 同 deadline，后插
        k.Clock.After(0.5f, TimeDomain.Unscaled, _ => log.Add("c")); // 更早到期，最后插

        k.Tick(FrameTime.First(2f, 2f));

        Check("推进序 = (deadline, 插入序) 双键（API 承诺）：先按到期时刻，同刻回到 After/Every 调用序",
            log.Count == 3 && log[0] == "c" && log[1] == "a" && log[2] == "b"
            && k.Diagnostics.TimersFired == 3);
    }

    private static void TimerHandleGenerationMakesStaleCancelNoOp()
    {
        var t = new NodeTable();
        var k = new UiKernel(t);
        int first = 0, second = 0;
        TimerHandle h = k.Clock.After(1f, TimeDomain.Unscaled, _ => first++);
        k.Clock.Cancel(h);
        bool dead = !k.Clock.IsActive(h);
        k.Clock.Cancel(h);                              // 重复取消：静默 no-op

        TimerHandle reused = k.Clock.After(1f, TimeDomain.Unscaled, _ => second++);
        k.Clock.Cancel(h);                              // 陈旧句柄不许误伤复用同槽的新定时器

        k.Tick(FrameTime.First(2f, 2f));

        Check("不变量 11：TimerHandle 代际——Cancel 后旧句柄静默 no-op，且伤不到复用同槽的新定时器",
            dead && first == 0 && second == 1
            && reused.Index == h.Index && reused.Gen != h.Gen && k.Clock.ActiveCount == 0);
    }

    private static void EveryRepeatsAndCancelsFromInsideCallback()
    {
        var t = new NodeTable();
        var k = new UiKernel(t);
        int n = 0;
        TimerHandle self = default;
        self = k.Clock.Every(1f, TimeDomain.Unscaled, h =>
        {
            n++;
            if (n == 3) k.Clock.Cancel(h);              // 回调内取消自己（重排先于回调 ⇒ 取消必胜）
        });

        FrameTime ft = FrameTime.First(1f, 1f);
        for (int i = 0; i < 5; i++)
        {
            k.Tick(ft);
            ft = ft.Step(1f, 1f);
        }

        Check("Every 周期触发，且回调内 Cancel 自己立即生效（不会被重排复活）",
            n == 3 && !k.Clock.IsActive(self) && k.Clock.ActiveCount == 0);
    }

    private static void TimersFireInP1()
    {
        var t = new NodeTable();
        var k = new UiKernel(t);
        FramePhase at = FramePhase.P9_FrameEnd;
        k.Clock.After(0f, TimeDomain.Unscaled, _ => at = k.CurrentPhase);

        k.Tick(Frame0());

        Check("定时器推进发生在 P1（相位表的时钟列）", at == FramePhase.P1_Clock);
    }

    // ── PostInput 单入口 ────────────────────────────────────────────────────

    private static void PostInputDrainsOnlyInP0()
    {
        var t = new NodeTable();
        var k = new UiKernel(t);
        var got = new List<int>();
        var seenPhase = new List<FramePhase>();
        k.InputHandler = (in InputPacket p, ref FrameContext ctx) =>
        {
            got.Add(p.Id);
            seenPhase.Add(ctx.Phase);
            if (p.Id == 0) k.PostInput(new InputPacket(InputKind.PointerUp, 1));   // 回调内再投递
        };

        k.PostInput(new InputPacket(InputKind.PointerDown, 0, 3f, 4f));
        FrameTime ft = Frame0();
        k.Tick(ft);
        bool firstFrame = got.Count == 1 && got[0] == 0 && seenPhase[0] == FramePhase.P0_Input
            && k.PendingInputCount == 1 && k.Diagnostics.InputsDrained == 1;

        k.Tick(ft.Step(0.016f, 0.016f));

        Check("PostInput 单入口：只在 P0 排空，且排的是进 P0 瞬间的快照——回调内投递的包属于下一帧",
            firstFrame && got.Count == 2 && got[1] == 1 && k.PendingInputCount == 0);
    }

    private static void InputTapIsTheSingleRecordingHook()
    {
        var t = new NodeTable();
        var k = new UiKernel(t);
        var tape = new List<InputPacket>();
        var frames = new List<ulong>();
        k.InputTap = (ulong f, in InputPacket p) => { tape.Add(p); frames.Add(f); };
        k.InputHandler = (in InputPacket p, ref FrameContext ctx) => { };

        k.PostInput(new InputPacket(InputKind.PointerDown, 7));
        k.PostInput(new InputPacket(InputKind.PointerMove, 7, 1f, 2f));
        k.PostInputFromTape(new InputPacket(InputKind.PointerUp, 7));   // 回放注入不再录带

        bool seq = tape.Count == 2 && tape[0].Seq == 0 && tape[1].Seq == 1
            && tape[0].Kind == InputKind.PointerDown && tape[1].Kind == InputKind.PointerMove;
        bool framed = frames.Count == 2 && frames[0] == 0 && frames[1] == 0;   // 投递时尚在第 0 帧
        k.Tick(Frame0());

        Check("录带挂点：PostInput 逐包过 InputTap 并补单调 Seq；PostInputFromTape 走同一队列但不回录",
            seq && framed && k.Diagnostics.InputsDrained == 3);
    }

    // ── FrameDiag / P2 / P6 / P9 ────────────────────────────────────────────

    private static void FrameDiagLatchesAtP9()
    {
        UiKernel k = KernelWithLeaf(out NodeTable t, out NodeHandle leaf);
        ulong duringFrame = ulong.MaxValue;
        ulong inAfterFrame = ulong.MaxValue;
        k.StateStep = (ref FrameContext ctx) => ctx.Invalidation.Mark(leaf, Ch.Content, InvalidateReason.UserWrite);
        k.SubmitStep = (ref FrameContext ctx) => duringFrame = k.Diagnostics.FrameId;   // P8：仍是上一帧的账
        k.AfterFrame += id => inAfterFrame = k.Diagnostics.FrameId;                     // P9 锁存之后

        k.Tick(Frame0());
        FrameDiag d = k.Diagnostics;

        Check("FrameDiag 锁存在 P9：帧内读到的是上一帧的账，AfterFrame 起读到本帧完整账（含失效聚合与相位计时）",
            duringFrame == 0 && inAfterFrame == 1 && d.FrameId == 1
            && d.Invalidation.Marks == 1 && d.Invalidation.MarksOf(Ch.Content) == 1
            && d.Invalidation.MarksOf(InvalidateReason.UserWrite) == 1
            && d.TotalTicks > 0 && d.TicksOf(FramePhase.P6_Settle) >= 0
            && t.Phase == FramePhase.P3_Commands);
    }

    private static void GlobalInvalidateAppliesInP2()
    {
        var t = new NodeTable();
        var k = new UiKernel(t);
        int fan = 0;
        FramePhase at = FramePhase.P9_FrameEnd;
        k.Invalidation.GlyphStoreFanOut = () => { fan++; at = k.CurrentPhase; };
        k.Invalidation.RequestGlobalInvalidate(GlobalSource.GlyphStore);   // 帧外只挂起

        bool pendingBefore = k.Invalidation.HasPendingGlobal;
        k.Tick(Frame0());

        Check("不变量 5：挂起的全局失效只在 P2 由相位机放行（fan-out 归源，时点归本平面）",
            pendingBefore && fan == 1 && at == FramePhase.P2_GlobalInvalidate
            && k.Diagnostics.GlobalsApplied == 1 && !k.Invalidation.HasPendingGlobal);
    }

    private static void SettleAppliesStructureAndDrillsDown()
    {
        var t = new NodeTable();
        var a = t.CreateNode(NodeType.Component);
        var b = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, a);
        t.AddChild(a, b);
        var k = new UiKernel(t);
        var visited = new List<uint>();
        FramePhase at = FramePhase.P0_Input;
        k.DownStep = (idx, bits) => { visited.Add(idx); at = k.CurrentPhase; };
        t.SetAlpha(a, 0.5f);                              // Ch.Color | Ch.DownColor

        var c = t.CreateNode(NodeType.Image);
        t.AddChild(a, c);                                 // 结构变化：paintOrder 待重展
        uint beforePaint = t.PaintIndexOf(b);             // 第一个 P6 之前：序里只有根
        k.Tick(Frame0());

        Check("P6：ApplyStructure 定形 paintOrder（此前挂上的节点一律不在序里），随后按子树戳下钻（父先于子）",
            beforePaint == NodeTable.NotInTree
            && t.PaintIndexOf(b) != NodeTable.NotInTree && t.PaintIndexOf(c) != NodeTable.NotInTree
            && at == FramePhase.P6_Settle && visited.Count == 3 && visited[0] == a.Index
            && k.Diagnostics.DownVisited == 3);
    }

    private static void HandleGenerationBumpsOnlyAtP9()
    {
        UiKernel k = KernelWithLeaf(out NodeTable t, out NodeHandle leaf);
        uint idx = leaf.Index;
        ushort gen0 = t.GenerationOf(idx);
        ushort genAtSubmit = 0;
        k.SubmitStep = (ref FrameContext ctx) => genAtSubmit = t.GenerationOf(idx);

        t.Destroy(leaf);                                   // 标记即死：句柄立刻失效
        bool deadImmediately = !t.IsAlive(leaf);
        k.Tick(Frame0());

        Check("不变量 11：Destroy 标记即死，gen++ 由内核统一放在 P9（P8 时代际还没换）",
            deadImmediately && genAtSubmit == gen0 && t.GenerationOf(idx) == (ushort)(gen0 + 1));
    }

    private static void DrainerReceivesFrameContext()
    {
        UiKernel k = KernelWithLeaf(out _, out NodeHandle leaf);
        var rec = new DrainRecorder(Ch.Content);
        k.Invalidation.Register(rec);
        k.Invalidation.Mark(leaf, Ch.Content, InvalidateReason.UserWrite);

        k.Tick(Frame0());

        Check("IChannelDrain 收到本帧 FrameContext（M1-07 留的接缝）：帧号与相位由内核填，五通道排在 P7",
            rec.Calls == 1 && rec.Frames[0] == 1 && rec.Phases[0] == FramePhase.P7_RenderDrain
            && rec.Channels[0] == Ch.Content && k.Diagnostics.DrainedHandles == 1);
    }
}
