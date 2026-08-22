using System.Text;
using FairyNext.AbiGen;
using FairyNext.Backend.Mock;
using FairyNext.Compiler;
using FairyNext.Compiler.Freeze;
using FairyNext.Compiler.Shape;
using FairyNext.Contracts;
using FairyNext.Core;
using FairyNext.Core.Fgb;
using FairyNext.Core.Layout;
using FairyNext.Core.Rendering;
using FairyNext.Numerics;

namespace FairyNext.Tests;

/// <summary>
/// M1-20b FgbCompiler 后半（Program 的 partial 分片）：已定形的树 → 编译期 Extract →
/// canonical 去重 → 段冻结 → 内存计划。本包上线**三道门**：
///
/// ① **编译产物 golden（L1）**——四个样例包的 FGB **逐字节**入库（<c>tests/goldens/fgb/</c>），
///    重编译必须复现。伴生 <c>.plan.txt</c>（内存计划 + 编译产物 golden 文本）是同一份产物的
///    人读面：blob 的 diff 只能告诉你「变了」，plan 的 diff 告诉你**哪个段、哪个组件**变了。
///    刷新基线：<c>FAIRYNEXT_UPDATE_FGB_GOLDENS=1 dotnet run --project tests/FairyNext.Tests</c>
///    （刷新后必须逐行读 diff——golden 的价值全在「改动是被人看过的」）。
///
/// ② **等价性金样（架构不变量 18）**——同一 .fui 两条路径的 <c>CanonicalStream</c> 逐字节比：
///    路径 A = 编译器无头跑出的流（<c>FgbFreezer</c> 里的离线驱动：直调 <c>Extract.Rebuild</c>
///    + 自开尾剪枝）；路径 B = **运行时管线**（<c>RenderPipeline.Attach</c> → 结构脏 →
///    <c>UiKernel.Tick</c> 走完 P6 定形 / P7 五通道排水 + DrainTail 剪枝 / P8 提交）。
///    两条路径是同一个 <see cref="Extract"/> 算法的两个**驱动**——门比的是驱动等价，
///    这既是「编译器 = 无头运行时」的机器可验形式，也是降级阶梯「组件级降回运行时 Extract」
///    照样画对的凭据。**边界写明**：M1 的路径 B 复用 20a 建的那棵树（建树本身就是运行时 API
///    的产物，见 20a 的「手工建等价树逐位同」用例）；「另一份宿主从 FGB 装载后再 Extract」
///    归 M1-22——那时门的两端才各自有独立的树来源。
///
/// ③ **FGB 读回 sanity**——写出的 blob 用 <see cref="FgbBlobView"/> 打开，NODE 列、COMP 区间、
///    CNST 四数组、LOCL、STRT 逐项与原树/原图对账。写侧不许自证：账要从 blob 里读回来对。
///
/// 三道门之外另有两道**自检**的可执行形态：冻结前置门 FGM903（离线 Extract 之前自验派生列与
/// paintOrder 已定形——「上游 Tick 过所以一定新鲜」是关于实现的推断，不变量是关于产物的断言）、
/// canonical 后置扫描（不变量 8，用例从 blob 独立重扫一遍，不复用编译器自己那次）。
///
/// 对照物纪律同 20a：常量来自编辑器授权 XML 与 fork 语义，不拿实现验实现；
/// 涉及运行期结构的账（ConstraintOp 宽、NODE 列序）一律与**运行期真值**对，不与写侧常量对。
/// </summary>
public static partial class Program
{
    private const string FgbGoldenDir = "tests/goldens/fgb";

    /// <summary>入 golden 的四个包（覆盖：虚拟列表 / 滚动容器 / 下拉刷新 / 九组件翻页）。</summary>
    private static readonly string[] FgbGoldenPackages = { "VirtualList", "ScrollPane", "PullToRefresh", "TurnPage" };

    /// <summary>六个样例包（等价性金样与读回对账跑全集）。</summary>
    private static readonly string[] FreezeFixtures =
        { "VirtualList", "Cooldown", "ScrollPane", "TextMeshPro", "PullToRefresh", "TurnPage" };

    private static void CompilerFreezeSuite()
    {
        string? root = RepoRoot.Find(AppContext.BaseDirectory);
        if (root == null) { Check("freeze: 定位仓库根", false); return; }
        string dir = RepoRoot.ToAbsolute(root, FuiFixtureDir);
        string goldenDir = RepoRoot.ToAbsolute(root, FgbGoldenDir);

        // 门 ①：编译产物 golden（逐包一条，失败时报首差异字节）
        foreach (string name in FgbGoldenPackages) FreezeGolden(goldenDir, dir, name);

        // 门 ②：等价性金样
        FreezeEquivalenceOracle(dir);
        FreezeEquivalenceHasTeeth(dir);
        FreezeTailPruneMirrored(dir);

        // 门 ③：读回 sanity 与逐段对账
        FreezeBlobOpensAndHeader(dir);
        FreezeNodeColumnLedger(dir);
        FreezeCompRangesLedger(dir);
        FreezeCnstLedger(dir);
        FreezeLocalLedger(dir);
        FreezeStrtLedger(dir);
        FreezeLeafContentRefLedger(dir);

        // canonical 去重（机制 10 / 不变量 8）
        FreezeCanonicalDedup(dir);
        FreezeCanonicalDistinctScan(dir);

        // 内存计划（机制 11）的断言锚
        FreezeMemoryPlanSectionLedger(dir);
        FreezeMemoryPlanPoolBudget(dir);

        // 确定性 / 纯函数面 / 前置门 / 跨包边界
        FreezeDeterminism(dir);
        FreezeRelativizePureFunction();
        FreezeStaleDerivedRejected(dir);
        FreezeRecordWidthsMatchRuntimeStructs();
        FreezeCrossPackageDeferred(dir);
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    private static bool UpdateFgbGoldens =>
        Environment.GetEnvironmentVariable("FAIRYNEXT_UPDATE_FGB_GOLDENS") == "1";

    /// <summary>
    /// 整包编译（走全量门面 <see cref="FgbCompiler.Compile"/>：golden 比的是发布路径）。
    /// Error 级诊断在编译面是 <see cref="FgmCompileException"/>，在**测试面**必须变成一条 FAIL：
    /// 异常逃出 runner 就没有 <c>RESULT</c> 判定行了，而判定行是 CI 唯一读的东西。
    /// </summary>
    private static CompileResult? CompileFixture(string dir, string name)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(dir, name + ".fui"));
        CompileOptions opts = ShapeOpts();
        try { return FgbCompiler.Compile(bytes, in opts); }
        catch (FgmCompileException e) { Console.WriteLine("     " + name + " 编译失败：\n" + e.Message); return null; }
    }

    /// <summary>定形 + 冻结（需要同时拿到已定形的树与冻结产物时用；两者共用同一个世界）。</summary>
    private static CompileResult? FreezeFixture(string dir, string name, out ShapedPackage shaped)
    {
        shaped = ShapeFixture(dir, name);
        try { return FgbCompiler.Freeze(shaped); }
        catch (FgmCompileException e) { Console.WriteLine("     " + name + " 冻结失败：\n" + e.Message); return null; }
    }

    private static StreamSnapshot FreezeNorm(StreamSnapshot s) => new StreamSnapshot(
        s.Quads, s.BaseColor, s.Clips, s.Slots, s.Segments, s.Runs, 0u, null);

    /// <summary>冻结失败在测试面就是一条 FAIL（带包名），不是异常。</summary>
    private static void FreezeCompileFailed(string name) =>
        Check("freeze: " + name + " 冻结成功（Error 级 FGM 诊断 = 编译器 bug）", false);

    private static bool TryOpenBlob(CompileResult r, out FgbBlobView view)
    {
        bool ok = FgbBlobView.TryOpen(r.Blob, out view, out FgbLoadReport report);
        if (!ok) Console.WriteLine("     blob 打开失败：" + report);
        return ok;
    }

    // ── 门 ①：编译产物 golden（L1）─────────────────────────────────────────

    /// <summary>
    /// 一个包的 FGB 与人读账单逐字节复现。golden 覆盖的不只是「值对不对」——段序、
    /// canonical 的登记序、字符串池的排布**全部**是产物的一部分，任何一处漂移都在这里现形。
    /// </summary>
    private static void FreezeGolden(string goldenDir, string fuiDir, string name)
    {
        CompileResult? r = CompileFixture(fuiDir, name);
        if (r == null) { Check("freeze golden: " + name + " 编译成功", false); return; }
        byte[] blob = r.Blob.ToArray();
        string plan = r.MemoryPlan + r.ReactiveGraph;
        string blobPath = Path.Combine(goldenDir, name + ".fgb");
        string planPath = Path.Combine(goldenDir, name + ".plan.txt");

        if (UpdateFgbGoldens)
        {
            Directory.CreateDirectory(goldenDir);
            File.WriteAllBytes(blobPath, blob);
            File.WriteAllText(planPath, plan, new UTF8Encoding(false));
            Console.WriteLine("     [update] " + name + ".fgb " + blob.Length + "B");
        }

        if (!File.Exists(blobPath) || !File.Exists(planPath))
        {
            Check("freeze golden: " + name + " 基线在库（" + FgbGoldenDir + "）", false);
            return;
        }

        byte[] want = File.ReadAllBytes(blobPath);
        int at = FirstByteDiff(want, blob);
        if (at >= 0)
        {
            Console.WriteLine("     " + name + " 首差异字节 @" + at
                + "（基线 " + want.Length + "B，本次 " + blob.Length + "B）"
                + "；刷新基线：FAIRYNEXT_UPDATE_FGB_GOLDENS=1");
        }
        string wantPlan = File.ReadAllText(planPath).Replace("\r\n", "\n");
        bool planSame = wantPlan == plan.Replace("\r\n", "\n");
        if (!planSame) Console.WriteLine("     " + name + " 内存计划与基线不同：\n" + plan);
        Check("freeze golden: " + name + " FGB 逐字节复现 + 内存计划账单同（L1）", at < 0 && planSame);
    }

    private static int FirstByteDiff(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) if (a[i] != b[i]) return i;
        return a.Length == b.Length ? -1 : n;
    }

    // ── 门 ②：等价性金样（架构不变量 18）───────────────────────────────────

    /// <summary>
    /// 路径 A（编译器离线驱动）与路径 B（运行时管线驱动）的流规范形逐字节相等。
    /// structEpoch 归零后再比（同 <c>IncrementalGate</c> 的纪律：代数是「编过几次」，不是画面）。
    /// </summary>
    private static void FreezeEquivalenceOracle(string dir)
    {
        int comps = 0;
        bool ok = true;
        foreach (string name in FreezeFixtures)
        {
            CompileResult? a = FreezeFixture(dir, name, out ShapedPackage _);
            if (a == null) { ok = false; continue; }

            // 路径 B：另起一份定形世界（Shape 的确定性由 20a 的用例钉住），接真管线跑一整帧。
            ShapedPackage b = ShapeFixture(dir, name);
            foreach (ShapedComponent sc in b.Components)
            {
                comps++;
                RenderPipeline pipe = AttachRuntimePipeline(sc, out RenderStream stream);
                sc.Kernel.Invalidation.Mark(sc.Root, Ch.Structure, InvalidateReason.UserWrite);
                var ft = new FrameTime(1f / 60f, 1f / 60f, 9.0, 9.0);
                sc.Kernel.Tick(in ft);

                if (!a.TryGetComponent(sc.Item.Id, out FrozenComponent? fc))
                {
                    Console.WriteLine("     " + name + "/" + sc.Item.Id + " 冻结账缺席");
                    ok = false;
                    continue;
                }
                StreamSnapshot pa = FreezeNorm(fc.Stream);
                StreamSnapshot pb = FreezeNorm(stream.Snapshot());
                byte[] ca = CanonicalStream.Canonicalize(pa);
                byte[] cb = CanonicalStream.Canonicalize(pb);
                int d = CanonicalStream.FirstDifference(ca, cb);
                bool same = d < 0 && pipe.DerivedOracleFailures == 0 && pipe.PaintOrderFailures == 0
                    && fc.Extract.Quads == pb.QuadCount;
                if (!same)
                {
                    Console.WriteLine("     " + name + "/" + sc.Item.Id + " 首差异 @" + d
                        + (d < 0 ? "" : " " + CanonicalStream.Locate(pa, d))
                        + " A=" + pa.QuadCount + "quads B=" + pb.QuadCount
                        + " derivedOracle=" + pipe.DerivedOracleFailures
                        + " paintOrder=" + pipe.PaintOrderFailures);
                }
                ok &= same;
            }
        }
        Check("freeze 等价性金样: 六包 " + comps + " 组件——编译期 Extract == 运行时管线 Extract "
            + "逐字节（CanonicalStream，不变量 18）", ok && comps >= 25);
    }

    /// <summary>
    /// 门有牙：把路径 A 的流动一个 quad 的一个分量，金样必须红，且首差异定位到
    /// <c>Quads[i].rect</c> 并归因到 Transform 通道。等价性金样若比的是空集合或
    /// 规范形吃掉了差异，这条用例先红——门的证明必须包含「它抓得住」。
    /// </summary>
    private static void FreezeEquivalenceHasTeeth(string dir)
    {
        CompileResult? r = FreezeFixture(dir, "TurnPage", out ShapedPackage _);
        if (r == null || !r.TryGetComponent("gawe1", out FrozenComponent? fc) || fc.Stream.QuadCount == 0)
        {
            Check("freeze 等价性金样有牙: Page 有实例", false);
            return;
        }
        StreamSnapshot clean = FreezeNorm(fc.Stream);
        QuadInstance[] q = fc.Stream.Quads.ToArray();
        q[0].Rect.x = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(q[0].Rect.x) ^ 1);
        var dirty = new StreamSnapshot(q, fc.Stream.BaseColor, fc.Stream.Clips, fc.Stream.Slots,
            fc.Stream.Segments, fc.Stream.Runs, 0u, null);

        byte[] ca = CanonicalStream.Canonicalize(clean);
        byte[] cb = CanonicalStream.Canonicalize(dirty);
        int d = CanonicalStream.FirstDifference(ca, cb);
        CanonicalSite site = d < 0 ? default : CanonicalStream.Locate(clean, d);
        Check("freeze 等价性金样有牙: 单实例 1ulp 扰动 ⇒ 金样红 + 定位到 Quads[0].rect ⇐ Transform",
            d >= 0 && site.Section == CanonicalSection.Quads && site.Index == 0
            && site.Field == "rect" && (site.Channel & Ch.Transform) != 0);
    }

    /// <summary>
    /// 等价性金样的**尾剪枝**半边。离线驱动靠 <c>Extract.PruneAfterRebuild</c> 补上管线
    /// <c>DrainTail</c> 的那道包含剪枝；两条路径少了任何一道，被摘 clipIndex 的实例数就不同。
    ///
    /// 为什么要造语料：六个样例包的编译产物**一个裁剪域都没有**——`overflow=scroll` 的
    /// 容器裁剪不在 M1 编译面（归 M2-06），于是 <c>PruneContainedClips</c> 在真语料上恒剪 0，
    /// 「离线不开尾剪枝」这个缺陷在样例包上不可观测。这里给**组件根**挂上「只开裁剪域、不画」的
    /// 内容记录（<c>ContentTable</c> 的合法形态，与 M2-06 届时产的是同一种记录），
    /// 把那条路径变成可观测的。
    /// </summary>
    private static void FreezeTailPruneMirrored(string dir)
    {
        CompileResult? a = FreezeClipped(dir, out int prunedA);
        if (a == null) { Check("freeze 等价性金样: 尾剪枝被镜像", false); return; }

        // 路径 B：同样注入裁剪域，但走真管线（尾剪枝在 DrainTail 里）
        ShapedPackage sb = ShapeFixture(dir, "PullToRefresh");
        ShapedComponent scb = ClipInjectTarget(sb);
        InjectClipDomain(scb);
        RenderPipeline pipe = AttachRuntimePipeline(scb, out RenderStream stream);
        scb.Kernel.Invalidation.Mark(scb.Root, Ch.Structure, InvalidateReason.UserWrite);
        var ft = new FrameTime(1f / 60f, 1f / 60f, 11.0, 11.0);
        scb.Kernel.Tick(in ft);

        a.TryGetComponent(scb.Item.Id, out FrozenComponent? fc);
        StreamSnapshot pa = FreezeNorm(fc!.Stream);
        StreamSnapshot pb = FreezeNorm(stream.Snapshot());
        int d = CanonicalStream.FirstDifference(
            CanonicalStream.Canonicalize(pa), CanonicalStream.Canonicalize(pb));
        if (d >= 0) Console.WriteLine("     尾剪枝首差异 @" + d + " " + CanonicalStream.Locate(pa, d));
        Check("freeze 等价性金样: 尾剪枝被镜像——注入裁剪域后离线剪 " + prunedA
            + " 条 == 管线 DrainTail 剪的那批（逐字节）",
            prunedA > 0 && d < 0 && pipe.DerivedOracleFailures == 0);
    }

    /// <summary>注入裁剪域后冻结（路径 A）。</summary>
    private static CompileResult? FreezeClipped(string dir, out int pruned)
    {
        pruned = 0;
        ShapedPackage s = ShapeFixture(dir, "PullToRefresh");
        ShapedComponent sc = ClipInjectTarget(s);
        InjectClipDomain(sc);
        CompileResult? r;
        try { r = FgbCompiler.Freeze(s); }
        catch (FgmCompileException e) { Console.WriteLine("     注入裁剪域后冻结失败：\n" + e.Message); return null; }
        if (r.TryGetComponent(sc.Item.Id, out FrozenComponent? fc)) pruned = fc.Extract.Pruned;
        return r;
    }

    /// <summary>
    /// 挑裁剪域宿主：**组件根**（localId 0）——它有孩子、自己不画，且不撞「Group 纯度」
    /// 不变量（typeId==Group ⇒ contentRef==0）。两条路径必须挑到同一个，故判据是纯函数。
    /// </summary>
    private static ShapedComponent ClipInjectTarget(ShapedPackage s)
    {
        foreach (ShapedComponent sc in s.Components)
            if (sc.Table.ChildCount(sc.Root) > 1 && sc.Table.ContentRef(sc.Root) == 0u) return sc;
        return s.Components[0];
    }

    private static void InjectClipDomain(ShapedComponent sc)
    {
        uint cid = sc.Content.Add(new ContentRecord
        {
            Kind = ExtractKind.None,
            OpensClip = true,
            Clip = ClipShape.Rect,
            DebugName = "clip-inject",
        });
        sc.Table.SetContentRef(sc.Root, cid);
    }

    /// <summary>把一个已定形世界接上真管线（先摘 20a 的吸收排水器——一个通道一个消费者）。</summary>
    private static RenderPipeline AttachRuntimePipeline(ShapedComponent sc, out RenderStream stream)
    {
        Invalidation inval = sc.Kernel.Invalidation;
        IChannelDrain? sink = inval.DrainerOf(Ch.Structure);
        if (sink != null) inval.Unregister(sink);
        stream = new RenderStream(sc.Item.Id);
        var pipe = new RenderPipeline(sc.Kernel, stream, sc.Content, new NullBackend())
        {
            Present = false,
            DerivedOracle = true,
        };
        pipe.Attach();
        return pipe;
    }

    // ── 门 ③：读回 sanity 与逐段对账 ────────────────────────────────────────

    /// <summary>blob 打开即验完（M1-19 的门矩阵）+ 规范段序 + 四维身份进头。</summary>
    private static void FreezeBlobOpensAndHeader(string dir)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(dir, "PullToRefresh.fui"));
        var opts = new CompileOptions(2, 3, new[] { new CompileFont("", SynthFontT()) });
        CompileResult r;
        try { r = FgbCompiler.Compile(bytes, in opts); }
        catch (FgmCompileException e) { Console.WriteLine("     " + e.Message); Check("freeze 读回: blob 打开", false); return; }
        if (!TryOpenBlob(r, out FgbBlobView v)) { Check("freeze 读回: blob 打开", false); return; }

        // 规范段序（M1-22 补三段后的形态）：身份与索引先行 → 模板本体 → 求值图 →
        // 渲染冻结 → **装载期补丁最后**（PTCH 是唯一会被写的段，排在末尾读起来才对得上它的角色）。
        uint[] want =
        {
            AbiLayout.FgbSectionStrt, AbiLayout.FgbSectionTref, AbiLayout.FgbSectionDeps,
            AbiLayout.FgbSectionComp, AbiLayout.FgbSectionNode, AbiLayout.FgbSectionPlan,
            AbiLayout.FgbSectionCont, AbiLayout.FgbSectionLocl,
            AbiLayout.FgbSectionCnst, AbiLayout.FgbSectionQuad, AbiLayout.FgbSectionSegs,
            AbiLayout.FgbSectionLeaf, AbiLayout.FgbSectionClip, AbiLayout.FgbSectionPtch,
        };
        bool order = v.SectionCount == want.Length;
        for (int i = 0; order && i < want.Length; i++) order &= v.FourccAt(i) == want[i];

        ulong sourceHash = 0ul;
        if (FuiPackageOf(dir, "PullToRefresh") is { } pkg) sourceHash = pkg.SourceHash;

        ulong pkgIdHash = FuiPackageOf(dir, "PullToRefresh") is { } pk ? FnvHash.Hash64(pk.Id ?? "") : 0ul;
        bool head = v.FormatVersion == Abi.FgbFormatVersion
            && v.SelfHash == FgbBlobView.ComputeSelfHash(r.Blob.Span)
            && v.SourceHash == sourceHash && sourceHash != 0ul
            && v.PkgId == pkgIdHash && pkgIdHash != 0ul
            && v.ScaleLevel == 2 && v.BranchId == 3
            && v.CombinedRefHash == 0ul;
        Check("freeze 读回: 打开即验完 + 规范段序十四段 + 四维身份进头（pkgId/scale/branch/sourceHash）",
            order && head);
    }

    private static FairyNext.Compiler.Fui.FuiPackage? FuiPackageOf(string dir, string name)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(dir, name + ".fui"));
        return FairyNext.Compiler.Fui.FuiPackage.TryParse(bytes, out FairyNext.Compiler.Fui.FuiPackage? p, out _)
            ? p : null;
    }

    /// <summary>
    /// NODE 列逐列对账：**非拓扑列**与树导出逐字节同；拓扑列（Rebase 位）满足
    /// 「哨兵 0 保留，其余 = 绝对槽 − 组件首槽 + 1」；contentRef 列 = canonical 重映射后的 CONT 下标。
    /// 对账的是**性质**不是写侧代码的复读——照抄一遍写法只能证明它没变，证不了它对。
    /// </summary>
    private static void FreezeNodeColumnLedger(string dir)
    {
        CompileResult? r = FreezeFixture(dir, "TurnPage", out ShapedPackage s);
        if (r == null) { FreezeCompileFailed("TurnPage"); return; }
        if (!TryOpenBlob(r, out FgbBlobView v)
            || !v.TryGetSection(AbiLayout.FgbSectionNode, out ReadOnlySpan<byte> nodePayload)
            || !FgbNodeSection.TryView(nodePayload, out FgbNodeView view))
        {
            Check("freeze 读回对账: NODE 列", false);
            return;
        }

        bool ok = view.NodeCount == TotalLocals(s);
        int checkedCols = 0;
        foreach (ShapedComponent sc in s.Components)
        {
            if (!r.TryGetComponent(sc.Item.Id, out FrozenComponent? fc)) { ok = false; continue; }
            uint first = sc.Locals.HandleOf(0).Index;
            // 组件根是世界树根的**孩子**（M1-22 起），故模板区间从槽 2 起——相对化在真语料上
            // 不再是数值恒等（M1-20b 记在案的那条存活变异自此有门管得住）。
            // 这条断言把前提钉住：根一旦挪位，golden 随之变，改动不会悄悄发生。
            ok &= first == 2u;
            for (int col = 0; col < Abi.NodeColumns.Length; col++)
            {
                int w = Abi.NodeColumns[col].ElementSize;
                ReadOnlySpan<byte> got = view.Column(col).Slice(fc.NodeStart * w, fc.NodeCount * w);
                var tree = new byte[w * fc.NodeCount];
                sc.Table.ExportColumn(col, first, fc.NodeCount, tree);
                checkedCols++;

                if (Abi.NodeColumns[col].Rebase)
                {
                    bool linkCol = col == AbiLayout.NodeColParent || col == AbiLayout.NodeColNextSib
                        || col == AbiLayout.NodeColPrevSib;
                    for (int i = 0; i < fc.NodeCount; i++)
                    {
                        uint abs = BitConverter.ToUInt32(tree, i * 4);
                        uint rel = BitConverter.ToUInt32(got.Slice(i * 4, 4).ToArray(), 0);
                        // 模板根的父/前后兄是**编译宿主**的链位（它挂在世界树根下），不是模板的一部分：
                        // 冻结成哨兵 0，实例化时由挂载重新写。其余行按 rel = abs − first + 1。
                        if (i == 0 && linkCol) { ok &= rel == 0u; continue; }
                        ok &= abs == 0u ? rel == 0u : rel == abs - first + 1u;
                    }
                }
                else if (col == AbiLayout.NodeColContentRef)
                {
                    for (int i = 0; i < fc.NodeCount; i++)
                    {
                        uint cid = BitConverter.ToUInt32(tree, i * 4);
                        uint frozen = BitConverter.ToUInt32(got.Slice(i * 4, 4).ToArray(), 0);
                        // 树侧 0 ⇒ blob 侧 0；树侧非 0 ⇒ 落在 CONT 段内且指向同一份内容
                        ok &= cid == 0u ? frozen == 0u : frozen != 0u;
                    }
                }
                else
                {
                    ok &= got.SequenceEqual(tree);
                }
            }
        }
        Check("freeze 读回对账: NODE " + checkedCols + " 列区间——非拓扑列逐字节同树、"
            + "拓扑列相对化（哨兵 0 保留）、contentRef 走 canonical 重映射", ok && checkedCols >= 21);
    }

    private static int TotalLocals(ShapedPackage s)
    {
        int n = 0;
        foreach (ShapedComponent sc in s.Components) n += sc.Locals.Count;
        return n;
    }

    /// <summary>COMP 记录的区间账 == FrozenComponent 的记账，且每段区间都落在该段内。</summary>
    private static void FreezeCompRangesLedger(string dir)
    {
        CompileResult? r = FreezeFixture(dir, "ScrollPane", out ShapedPackage s);
        if (r == null) { FreezeCompileFailed("ScrollPane"); return; }
        if (!TryOpenBlob(r, out FgbBlobView v)
            || !v.TryGetSection(AbiLayout.FgbSectionComp, out ReadOnlySpan<byte> comp))
        {
            Check("freeze 读回对账: COMP 区间", false);
            return;
        }
        int quadRecords = SectionRecords(v, AbiLayout.FgbSectionQuad, Abi.QuadInstanceSize);
        int leafRecords = SectionRecords(v, AbiLayout.FgbSectionLeaf, AbiLayout.FgbLeafSize);
        int segRecords = SectionRecords(v, AbiLayout.FgbSectionSegs, AbiLayout.FgbSegSize);
        int clipRecords = SectionRecords(v, AbiLayout.FgbSectionClip, Abi.ClipEntrySize);
        int loclRecords = SectionRecords(v, AbiLayout.FgbSectionLocl, AbiLayout.FgbLocalSize);

        bool ok = comp.Length == s.Components.Count * AbiLayout.FgbCompSize;
        for (int i = 0; ok && i < s.Components.Count; i++)
        {
            ShapedComponent sc = s.Components[i];
            ReadOnlySpan<byte> rec = comp.Slice(i * AbiLayout.FgbCompSize, AbiLayout.FgbCompSize);
            if (!r.TryGetComponent(sc.Item.Id, out FrozenComponent? fc)) { ok = false; break; }

            ok &= FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompNameHashOffset)
                == FnvHash.Hash32(sc.Item.Name ?? sc.Item.Id);
            ok &= FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompNodeStartOffset) == (uint)fc.NodeStart;
            ok &= FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompNodeCountOffset) == (uint)sc.Locals.Count;
            ok &= FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompQuadStartOffset) == (uint)fc.QuadStart;
            ok &= FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompQuadCountOffset) == (uint)fc.QuadCount;
            ok &= FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompLeafStartOffset) == (uint)fc.LeafStart;
            ok &= FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompLeafCountOffset) == (uint)fc.LeafCount;
            ok &= FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompCnstOpStartOffset) == (uint)fc.OpStart;
            ok &= FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompCnstOpCountOffset) == (uint)fc.OpCount;
            ok &= FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompInstanceBytesOffset) == fc.InstanceBytes;
            ok &= FgbRecordIo.ReadF32(rec, AbiLayout.FgbCompSourceWidthOffset) == sc.Source.SourceWidth;
            ok &= FgbRecordIo.ReadF32(rec, AbiLayout.FgbCompSourceHeightOffset) == sc.Source.SourceHeight;

            // LOCL / FanOut 桶 / 掩码逐节点一条，故它们的起点必等于 NODE 行起点——
            // 读侧（与 LOCL/CNST 两条对账用例）就是按这条恒等式直接下标寻址的。
            ok &= FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompLocalStartOffset) == (uint)fc.NodeStart;
            ok &= FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompLocalCountOffset) == (uint)fc.NodeCount;
            ok &= FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompCnstFanStartOffset) == (uint)fc.NodeStart;

            // 区间不越段（结构性断言：装载期切片的合法性全靠这条）
            ok &= fc.QuadStart + fc.QuadCount <= quadRecords;
            ok &= fc.LeafStart + fc.LeafCount <= leafRecords;
            ok &= (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompSegStartOffset)
                + (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompSegCountOffset) <= segRecords;
            ok &= (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompClipStartOffset)
                + (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompClipCountOffset) <= clipRecords;
            ok &= (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompLocalStartOffset)
                + (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompLocalCountOffset) <= loclRecords;

            // 有约束图 ⇒ flags bit0；有文本叶 ⇒ bit1
            ushort flags = FgbRecordIo.ReadU16(rec, AbiLayout.FgbCompFlagsOffset);
            ok &= ((flags & 1) != 0) == (fc.OpCount > 0);
            ok &= ((flags & 2) != 0) == (sc.Text != null && sc.Text.EntryCount > 0);
            ok &= FgbRecordIo.ReadU16(rec, AbiLayout.FgbCompCtrlCountOffset) == 0;
        }
        Check("freeze 读回对账: COMP 五组区间 == 冻结账，且逐段不越界（装载期切片的合法性）", ok);
    }

    private static int SectionRecords(FgbBlobView v, uint fourcc, int stride) =>
        v.TryGetSection(fourcc, out ReadOnlySpan<byte> s) ? s.Length / stride : 0;

    /// <summary>CNST 四数组 == ConstraintGraph 原值（算子下标即拓扑序；FanOut/下标池组件内相对）。</summary>
    private static void FreezeCnstLedger(string dir)
    {
        CompileResult? r = FreezeFixture(dir, "PullToRefresh", out ShapedPackage s);
        if (r == null) { FreezeCompileFailed("PullToRefresh"); return; }
        if (!TryOpenBlob(r, out FgbBlobView v)
            || !v.TryGetSection(AbiLayout.FgbSectionCnst, out ReadOnlySpan<byte> cnst))
        {
            Check("freeze 读回对账: CNST", false);
            return;
        }
        int opCount = (int)FgbRecordIo.ReadU32(cnst, AbiLayout.FgbCnstHeaderOpCountOffset);
        int fanCount = (int)FgbRecordIo.ReadU32(cnst, AbiLayout.FgbCnstHeaderFanCountOffset);
        int idxCount = (int)FgbRecordIo.ReadU32(cnst, AbiLayout.FgbCnstHeaderIndexCountOffset);
        int maskCount = (int)FgbRecordIo.ReadU32(cnst, AbiLayout.FgbCnstHeaderMaskCountOffset);

        ReadOnlySpan<ConstraintOp> ops = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ConstraintOp>(
            cnst.Slice(FgbCnstSection.OpsOffset, opCount * FgbCnstSection.OpBytes));
        ReadOnlySpan<FanOut> fans = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, FanOut>(
            cnst.Slice(FgbCnstSection.FansOffset(opCount), fanCount * FgbCnstSection.FanBytes));
        ReadOnlySpan<ushort> idx = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(
            cnst.Slice(FgbCnstSection.IndicesOffset(opCount, fanCount), idxCount * FgbCnstSection.IndexBytes));
        ReadOnlySpan<byte> masks = cnst.Slice(
            FgbCnstSection.MasksOffset(opCount, fanCount, idxCount), maskCount);

        bool ok = fanCount == TotalLocals(s) && maskCount == fanCount;
        int seenOps = 0;
        foreach (ShapedComponent sc in s.Components)
        {
            if (!r.TryGetComponent(sc.Item.Id, out FrozenComponent? fc)) { ok = false; continue; }
            ConstraintGraph? g = sc.Constraints;
            ok &= fc.OpCount == (g?.Ops.Length ?? 0);
            for (int i = 0; i < fc.OpCount; i++)
            {
                ConstraintOp a = g!.Ops[i];
                ConstraintOp b = ops[fc.OpStart + i];
                ok &= a.SrcNode == b.SrcNode && a.DstNode == b.DstNode
                    && a.Kind == b.Kind && a.AxisEdges == b.AxisEdges && a.Next == b.Next;
                // 拓扑序：写 src 的算子必在前（不变量 10 的产物形态）
                for (int j = i + 1; j < fc.OpCount; j++)
                    if (g.Ops[j].DstNode == b.SrcNode && g.Ops[j].Axis == b.Axis && b.SrcNode != b.DstNode)
                        ok = false;
                seenOps++;
            }
            // FanOut 桶按局部 id 直接下标；无关系的组件占零桶（读侧无分支）
            for (int l = 0; l < sc.Locals.Count; l++)
            {
                FanOut f = fans[fc.NodeStart + l];
                if (g == null) { ok &= f.Start == 0 && f.Count == 0; continue; }
                ok &= f.Start == g.FanOutBySrc[l].Start && f.Count == g.FanOutBySrc[l].Count;
                ok &= masks[fc.NodeStart + l] == g.NodeMasks[l];
                for (int k = 0; k < f.Count; k++)
                {
                    ushort opIndex = idx[/* 组件内相对 */ (int)f.Start + k
                        + (int)FgbRecordIo.ReadU32(CompRec(v, s, sc), AbiLayout.FgbCompCnstIdxStartOffset)];
                    ok &= opIndex == g.FanOpIndices[f.Start + k];
                    ok &= opIndex < fc.OpCount;      // 组件内相对：加 COMP.cnstOpStart 才归位
                }
            }
        }
        Check("freeze 读回对账: CNST 四数组 == 原图（" + seenOps + " 算子；索引即拓扑序，"
            + "FanOut/下标池组件内相对）", ok && seenOps > 0);
    }

    private static byte[] CompRec(FgbBlobView v, ShapedPackage s, ShapedComponent sc)
    {
        v.TryGetSection(AbiLayout.FgbSectionComp, out ReadOnlySpan<byte> comp);
        int i = 0;
        for (; i < s.Components.Count; i++) if (ReferenceEquals(s.Components[i], sc)) break;
        return comp.Slice(i * AbiLayout.FgbCompSize, AbiLayout.FgbCompSize).ToArray();
    }

    /// <summary>LOCL：localId ⇄ 编辑器 id 往返（哈希、name、GGroup 位）。</summary>
    private static void FreezeLocalLedger(string dir)
    {
        CompileResult? r = FreezeFixture(dir, "TurnPage", out ShapedPackage s);
        if (r == null) { FreezeCompileFailed("TurnPage"); return; }
        if (!TryOpenBlob(r, out FgbBlobView v)
            || !v.TryGetSection(AbiLayout.FgbSectionLocl, out ReadOnlySpan<byte> locl)
            || !v.TryGetSection(AbiLayout.FgbSectionStrt, out ReadOnlySpan<byte> strt))
        {
            Check("freeze 读回对账: LOCL", false);
            return;
        }
        bool ok = locl.Length == TotalLocals(s) * AbiLayout.FgbLocalSize;
        int groups = 0, withId = 0;
        foreach (ShapedComponent sc in s.Components)
        {
            if (!r.TryGetComponent(sc.Item.Id, out FrozenComponent? fc)) { ok = false; continue; }
            for (ushort l = 0; l < sc.Locals.Count; l++)
            {
                ReadOnlySpan<byte> rec = locl.Slice((fc.NodeStart + l) * AbiLayout.FgbLocalSize,
                    AbiLayout.FgbLocalSize);
                ok &= FgbRecordIo.ReadU16(rec, AbiLayout.FgbLocalLocalIdOffset) == l;
                string? want = sc.Locals.EditorIdOf(l);
                uint sid = FgbRecordIo.ReadU32(rec, AbiLayout.FgbLocalEditorIdStrOffset);
                ok &= StrtAt(strt, sid) == (want ?? string.Empty);
                ok &= FgbRecordIo.ReadU32(rec, AbiLayout.FgbLocalEditorIdHashOffset)
                    == (want == null ? 0u : FnvHash.Hash32(want));
                if (want != null) withId++;
                if ((FgbRecordIo.ReadU16(rec, AbiLayout.FgbLocalFlagsOffset) & 1) != 0)
                {
                    groups++;
                    ok &= sc.Table.TypeOf(sc.Locals.HandleOf(l)) == NodeType.Group;
                }
            }
        }
        Check("freeze 读回对账: LOCL " + withId + " 条 localId ⇄ 编辑器 id 往返 + 哈希链 + "
            + groups + " 个 GGroup 真节点位", ok && withId > 0 && groups > 0);
    }

    /// <summary>STRT：下标 0 = 空串哨兵；条目窗口读回 == 登记的串。</summary>
    private static void FreezeStrtLedger(string dir)
    {
        CompileResult? r = FreezeFixture(dir, "Cooldown", out ShapedPackage s);
        if (r == null) { FreezeCompileFailed("Cooldown"); return; }
        if (!TryOpenBlob(r, out FgbBlobView v)
            || !v.TryGetSection(AbiLayout.FgbSectionStrt, out ReadOnlySpan<byte> strt)
            || !v.TryGetSection(AbiLayout.FgbSectionComp, out ReadOnlySpan<byte> comp))
        {
            Check("freeze 读回对账: STRT", false);
            return;
        }
        int count = (int)FgbRecordIo.ReadU32(strt, AbiLayout.FgbStrtHeaderCountOffset);
        int poolBytes = (int)FgbRecordIo.ReadU32(strt, AbiLayout.FgbStrtHeaderPoolBytesOffset);
        bool ok = strt.Length == FgbStrtSection.PayloadBytes(count, poolBytes)
            && count >= 1 && StrtAt(strt, 0) == string.Empty;

        // 组件名逐条从池里读回来（写侧登记 → 读侧窗口，绕一圈对上才算对）
        for (int i = 0; i < s.Components.Count; i++)
        {
            ReadOnlySpan<byte> rec = comp.Slice(i * AbiLayout.FgbCompSize, AbiLayout.FgbCompSize);
            uint sid = FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompNameStrOffset);
            ShapedComponent sc = s.Components[i];
            ok &= StrtAt(strt, sid) == (sc.Item.Name ?? sc.Item.Id);
        }

        // 池内窗口互不越界、首尾相接（写侧是顺序追加，读侧据此可切零拷贝 span）
        uint at = 0;
        for (int i = 0; ok && i < count; i++)
        {
            int e = FgbStrtSection.EntryOffset(i);
            ok &= FgbRecordIo.ReadU32(strt, e + AbiLayout.FgbStrtEntryOffsetOffset) == at;
            at += FgbRecordIo.ReadU32(strt, e + AbiLayout.FgbStrtEntryLengthOffset);
        }
        ok &= at == (uint)poolBytes;
        Check("freeze 读回对账: STRT " + count + " 条——空串哨兵 + 窗口首尾相接 + 组件名往返", ok);
    }

    private static string StrtAt(ReadOnlySpan<byte> strt, uint id)
    {
        int count = (int)FgbRecordIo.ReadU32(strt, AbiLayout.FgbStrtHeaderCountOffset);
        if (id >= (uint)count) return "<越界>";
        int e = FgbStrtSection.EntryOffset((int)id);
        int off = (int)FgbRecordIo.ReadU32(strt, e + AbiLayout.FgbStrtEntryOffsetOffset);
        int len = (int)FgbRecordIo.ReadU32(strt, e + AbiLayout.FgbStrtEntryLengthOffset);
        int pool = FgbStrtSection.PoolOffset(count);
        return Encoding.UTF8.GetString(strt.Slice(pool + off, len).ToArray());
    }

    /// <summary>
    /// LEAF.contentRef 指向的 CONT 记录 == 该叶在树侧的内容真值（canonical 去重后引用仍正确）。
    /// 这条是去重的**正确性**半边：共享 id 省了字节，但省错了就是画错。
    /// </summary>
    private static void FreezeLeafContentRefLedger(string dir)
    {
        CompileResult? r = FreezeFixture(dir, "TurnPage", out ShapedPackage s);
        if (r == null) { FreezeCompileFailed("TurnPage"); return; }
        if (!TryOpenBlob(r, out FgbBlobView v)
            || !v.TryGetSection(AbiLayout.FgbSectionLeaf, out ReadOnlySpan<byte> leaf)
            || !v.TryGetSection(AbiLayout.FgbSectionCont, out ReadOnlySpan<byte> cont))
        {
            Check("freeze 读回对账: LEAF.contentRef", false);
            return;
        }
        bool ok = true;
        int leaves = 0;
        foreach (ShapedComponent sc in s.Components)
        {
            if (!r.TryGetComponent(sc.Item.Id, out FrozenComponent? fc)) { ok = false; continue; }
            for (int i = 0; i < fc.LeafCount; i++)
            {
                ReadOnlySpan<byte> rec = leaf.Slice((fc.LeafStart + i) * AbiLayout.FgbLeafSize,
                    AbiLayout.FgbLeafSize);
                ushort local = FgbRecordIo.ReadU16(rec, AbiLayout.FgbLeafLocalIdOffset);
                uint cid = FgbRecordIo.ReadU32(rec, AbiLayout.FgbLeafContentRefOffset);
                ok &= cid * AbiLayout.FgbContSize + AbiLayout.FgbContSize <= (uint)cont.Length;
                ReadOnlySpan<byte> crec = cont.Slice((int)cid * AbiLayout.FgbContSize, AbiLayout.FgbContSize);

                ContentRecord truth = sc.Content.At(sc.Table.ContentRef(sc.Locals.HandleOf(local)));
                ok &= truth.Kind == ExtractKind.Leaf;
                ok &= FgbRecordIo.ReadU32(crec, AbiLayout.FgbContTexIdOffset) == truth.Leaf.Texture.Value;
                ok &= FgbRecordIo.ReadU32(crec, AbiLayout.FgbContBaseColorOffset) == truth.Leaf.BaseColor;
                ok &= FgbRecordIo.ReadU32(crec, AbiLayout.FgbContEmitFlagsOffset) == truth.Leaf.EmitFlags;
                ok &= FgbRecordIo.ReadI32(crec, AbiLayout.FgbContSlackHintOffset) == truth.Leaf.SlackHint;
                ok &= FgbRecordIo.ReadU8(crec, AbiLayout.FgbContBlendOffset) == (byte)truth.Leaf.Blend;
                ok &= FgbRecordIo.ReadF32(crec, AbiLayout.FgbContUvX0Offset) == truth.Leaf.Region.Uv.x;
                ok &= FgbRecordIo.ReadF32(crec, AbiLayout.FgbContUvY1Offset) == truth.Leaf.Region.Uv.w;
                ok &= FgbRecordIo.ReadF32(crec, AbiLayout.FgbContSourceWidthOffset) == truth.Leaf.Region.SourceWidth;
                ok &= ((FgbRecordIo.ReadU8(crec, AbiLayout.FgbContFlagsOffset) & (1 << 3)) != 0)
                    == (truth.Leaf.Text != null);
                // 叶的实例区间必须落在自己组件的 QUAD 区间内（LEAF 存组件内相对下标）
                uint qs = FgbRecordIo.ReadU32(rec, AbiLayout.FgbLeafQuadStartOffset);
                uint qc = FgbRecordIo.ReadU32(rec, AbiLayout.FgbLeafQuadCountOffset);
                uint slack = FgbRecordIo.ReadU32(rec, AbiLayout.FgbLeafQuadSlackOffset);
                ok &= qc <= slack && qs + slack <= (uint)fc.QuadCount;
                leaves++;
            }
        }
        Check("freeze 读回对账: " + leaves + " 条 LEAF.contentRef 指向的 CONT == 树侧内容真值"
            + "（canonical 共享后引用仍正确）+ 实例区间落在组件内", ok && leaves > 0);
    }

    // ── canonical 去重（机制 10 / 不变量 8）────────────────────────────────

    /// <summary>
    /// 去重真的省了字节：包内出现次数 &gt; 1 的内容记录与字符串各只占一份存储，
    /// 且 blob 的 CONT/STRT 段严格小于「逐次登记」的线性和。
    /// </summary>
    private static void FreezeCanonicalDedup(string dir)
    {
        CompileResult? r = FreezeFixture(dir, "TurnPage", out ShapedPackage s);
        if (r == null) { FreezeCompileFailed("TurnPage"); return; }
        if (!TryOpenBlob(r, out FgbBlobView v)
            || !v.TryGetSection(AbiLayout.FgbSectionCont, out ReadOnlySpan<byte> cont)
            || !v.TryGetSection(AbiLayout.FgbSectionLeaf, out ReadOnlySpan<byte> leaf))
        {
            Check("freeze canonical: 去重", false);
            return;
        }
        // 「线性和」= 每个组件各自持有一份内容记录时的条数（也就是不去重的下界）。
        int linear = 0;
        foreach (ShapedComponent sc in s.Components)
        {
            for (ushort l = 0; l < sc.Locals.Count; l++)
                if (sc.Table.ContentRef(sc.Locals.HandleOf(l)) != 0) linear++;
        }
        int stored = cont.Length / AbiLayout.FgbContSize;

        // 共享确实发生：至少有两条不同的叶（跨组件）指向同一个 CONT id。
        var seen = new Dictionary<uint, int>();
        int leaves = leaf.Length / AbiLayout.FgbLeafSize;
        for (int i = 0; i < leaves; i++)
        {
            uint cid = FgbRecordIo.ReadU32(
                leaf.Slice(i * AbiLayout.FgbLeafSize, AbiLayout.FgbLeafSize),
                AbiLayout.FgbLeafContentRefOffset);
            seen[cid] = seen.TryGetValue(cid, out int n) ? n + 1 : 1;
        }
        int shared = 0;
        foreach (KeyValuePair<uint, int> kv in seen) if (kv.Value > 1) shared++;

        // 去重率的另一半从账本读（分子 = 去重后条数，分母 = 登记次数）：CONT 与 STRT 都必须真的合并过。
        (int cs, int ci) = CanonicalRatio(r.MemoryPlan, "content=");
        (int ss, int si) = CanonicalRatio(r.MemoryPlan, "strings=");
        bool ok = stored < linear && shared > 0
            && cs == stored && cs < ci && ss < si;
        Console.WriteLine("     TurnPage CONT stored=" + stored + " linear=" + linear
            + " sharedIds=" + shared + " content=" + cs + "/" + ci + " strings=" + ss + "/" + si);
        Check("freeze canonical: 相同子结构共享存储（CONT " + stored + " 条覆盖 " + linear
            + " 处引用，" + shared + " 个 id 被多叶共享；账本 content=" + cs + "/" + ci
            + " strings=" + ss + "/" + si + "）", ok);
    }

    /// <summary>从内存计划的 canonical 行取 "名=去重后/登记次数"。</summary>
    private static (int Count, int Inserts) CanonicalRatio(string plan, string key)
    {
        int at = plan.IndexOf("  canonical ", StringComparison.Ordinal);
        if (at < 0) return (0, 0);
        int k = plan.IndexOf(key, at, StringComparison.Ordinal);
        if (k < 0) return (0, 0);
        int start = k + key.Length;
        int end = plan.IndexOfAny(new[] { ' ', '\n' }, start);
        string[] parts = plan.Substring(start, end - start).Split('/');
        return (int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>不变量 8 的后置扫描：产物里不存在字节等同却 id 不同的记录（CONT/TREF 全扫）。</summary>
    private static void FreezeCanonicalDistinctScan(string dir)
    {
        bool ok = true;
        int scanned = 0;
        foreach (string name in FreezeFixtures)
        {
            CompileResult? r = FreezeFixture(dir, name, out ShapedPackage _);
            if (r == null || !TryOpenBlob(r, out FgbBlobView v)) { ok = false; continue; }
            ok &= DistinctRecords(v, AbiLayout.FgbSectionCont, AbiLayout.FgbContSize, ref scanned);
            ok &= DistinctRecords(v, AbiLayout.FgbSectionTref, AbiLayout.FgbTexRefSize, ref scanned);
        }
        Check("freeze canonical: 后置扫描（不变量 8）——六包 " + scanned
            + " 条 CONT/TREF 记录两两不字节等同", ok && scanned > 0);
    }

    private static bool DistinctRecords(FgbBlobView v, uint fourcc, int stride, ref int scanned)
    {
        if (!v.TryGetSection(fourcc, out ReadOnlySpan<byte> s)) return true;
        int n = s.Length / stride;
        scanned += n;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (s.Slice(i * stride, stride).SequenceEqual(s.Slice(j * stride, stride))) return false;
        return true;
    }

    // ── 内存计划（机制 11）的断言锚 ────────────────────────────────────────

    /// <summary>段账自洽：内存计划里逐段的「records=N×WB」与 blob 里那段的实际字节数相等。</summary>
    private static void FreezeMemoryPlanSectionLedger(string dir)
    {
        CompileResult? r = FreezeFixture(dir, "VirtualList", out ShapedPackage s);
        if (r == null) { FreezeCompileFailed("VirtualList"); return; }
        if (!TryOpenBlob(r, out FgbBlobView v)) { Check("freeze 内存计划: 段账", false); return; }

        bool ok = true;
        int lines = 0;
        foreach (string line in r.MemoryPlan.Split('\n'))
        {
            string t = line.Trim();
            if (!t.StartsWith("section ", StringComparison.Ordinal)) continue;
            string[] parts = t.Split(' ');
            string name = parts[1];
            int bytes = int.Parse(parts[2].TrimEnd('B'), System.Globalization.CultureInfo.InvariantCulture);
            uint fourcc = FourccOf(name);
            ok &= v.TryGetSection(fourcc, out ReadOnlySpan<byte> payload) && payload.Length == bytes;
            lines++;
        }
        // 段字节之和 + 头 + 目录 + 对齐填充 = blob；账本不许漏段
        int sum = 0;
        for (int i = 0; i < v.SectionCount; i++) sum += v.SectionAt(i).Length;
        ok &= lines == 14 && sum > 0 && sum < r.Blob.Length;
        ok &= r.MemoryPlan.Contains("blob=" + r.Blob.Length + "B sections=14");
        Check("freeze 内存计划: 逐段账 == blob 实际段字节（" + lines + " 段）+ 头行自洽", ok);
    }

    private static uint FourccOf(string name) => name switch
    {
        "STRT" => AbiLayout.FgbSectionStrt,
        "TREF" => AbiLayout.FgbSectionTref,
        "COMP" => AbiLayout.FgbSectionComp,
        "NODE" => AbiLayout.FgbSectionNode,
        "CONT" => AbiLayout.FgbSectionCont,
        "LOCL" => AbiLayout.FgbSectionLocl,
        "CNST" => AbiLayout.FgbSectionCnst,
        "QUAD" => AbiLayout.FgbSectionQuad,
        "SEGS" => AbiLayout.FgbSectionSegs,
        "LEAF" => AbiLayout.FgbSectionLeaf,
        "CLIP" => AbiLayout.FgbSectionClip,
        "PLAN" => AbiLayout.FgbSectionPlan,
        "PTCH" => AbiLayout.FgbSectionPtch,
        "DEPS" => AbiLayout.FgbSectionDeps,
        _ => 0u,
    };

    /// <summary>
    /// 池预算是**承诺不是估计**（运行期断言 15 的编译期半边）：逐组件的 pool 行
    /// == nodeCount × 80B + instanceBytes，且 instanceBytes == 实例块头（M1 无控制器状态）。
    /// </summary>
    private static void FreezeMemoryPlanPoolBudget(string dir)
    {
        CompileResult? r = FreezeFixture(dir, "PullToRefresh", out ShapedPackage s);
        if (r == null) { FreezeCompileFailed("PullToRefresh"); return; }
        bool ok = true;
        int rows = 0;
        long poolSum = 0;
        foreach (FrozenComponent fc in r.Components)
        {
            long want = (long)fc.NodeCount * Abi.NodeBytesPerNode + fc.InstanceBytes;
            ok &= fc.InstanceBytes == (uint)Abi.FgbInstanceHeaderBytes;
            ok &= r.MemoryPlan.Contains(" nodes=" + fc.NodeCount + " quads=" + fc.QuadCount)
                && r.MemoryPlan.Contains(" instanceBytes=" + fc.InstanceBytes + " pool=" + want + "B");
            poolSum += want;
            rows++;
        }
        ok &= r.MemoryPlan.Contains("pool-budget total=" + poolSum + "B nodes=" + TotalLocals(s));
        ok &= r.MemoryPlan.Contains("mask observable=0/" + Abi.MaxObservableProps);
        Check("freeze 内存计划: " + rows + " 行池预算 == nodeCount×80B + instanceBytes，"
            + "总账 " + poolSum + "B + 掩码占用率行在场", ok && rows > 0);
    }

    // ── 确定性 / 纯函数面 / 前置门 / 跨包边界 ──────────────────────────────

    /// <summary>同字节两次全量编译 ⇒ blob 逐字节同 + 两份账单文本逐字符同。</summary>
    private static void FreezeDeterminism(string dir)
    {
        bool ok = true;
        foreach (string name in FreezeFixtures)
        {
            CompileResult? a = CompileFixture(dir, name);
            CompileResult? b = CompileFixture(dir, name);
            if (a == null || b == null) { ok = false; continue; }
            int at = FirstByteDiff(a.Blob.ToArray(), b.Blob.ToArray());
            bool same = at < 0 && a.MemoryPlan == b.MemoryPlan && a.ReactiveGraph == b.ReactiveGraph
                && a.Diagnostics.ToString() == b.Diagnostics.ToString();
            if (!same) Console.WriteLine("     " + name + " 两次编译不同 @" + at);
            ok &= same;
        }
        Check("freeze 确定性: 六包两次 Compile 逐字节同（blob + 内存计划 + 产物 golden 文本 + 诊断）", ok);
    }

    /// <summary>
    /// 拓扑列相对化的纯函数面（<c>firstAbs ≠ 1</c> 的参数化）。
    /// **杀变异存活项，记在案**：编译世界里组件根恒在槽 1 ⇒ 相对化在 M1 是数值恒等，
    /// 于是「把 <c>FgbFreezer</c> 里那句 <c>Relativize</c> 调用整个摘掉」在任何样例包上都不可观测
    /// （golden 与 NODE 对账都照绿）。本用例钉的是**函数本身**；调用点要等 M1-22 的实例化基址
    /// 回填给出 <c>firstAbs ≠ 1</c> 的真语料才有门可上。这与 M1-20a 的 <c>LateSlotAllocs</c>
    /// 跳线同类：写明它现在守不住什么，比假装它守得住强。
    /// </summary>
    private static void FreezeRelativizePureFunction()
    {
        uint[] col = { 0u, 7u, 9u, 12u, 0u };
        byte[] bytes = new byte[col.Length * 4];
        Buffer.BlockCopy(col, 0, bytes, 0, bytes.Length);
        FgbFreezer.Relativize(bytes, 7u);
        var got = new uint[col.Length];
        Buffer.BlockCopy(bytes, 0, got, 0, bytes.Length);
        bool rel = got[0] == 0u && got[1] == 1u && got[2] == 3u && got[3] == 6u && got[4] == 0u;

        // 逆变换（M1-22 的实例化回填）：abs = base + rel − 1
        bool inverse = true;
        const uint instBase = 100u;
        for (int i = 0; i < col.Length; i++)
        {
            uint back = got[i] == 0u ? 0u : instBase + got[i] - 1u;
            inverse &= back == (col[i] == 0u ? 0u : instBase + col[i] - 7u);
        }

        uint[] map = { 0u, 5u, 0u, 9u };
        byte[] cbytes = new byte[4 * 4];
        Buffer.BlockCopy(new uint[] { 0u, 1u, 3u, 99u }, 0, cbytes, 0, cbytes.Length);
        FgbFreezer.RemapContentRef(cbytes, map);
        var cgot = new uint[4];
        Buffer.BlockCopy(cbytes, 0, cgot, 0, cbytes.Length);
        bool remap = cgot[0] == 0u && cgot[1] == 5u && cgot[2] == 9u && cgot[3] == 0u;

        Check("freeze 相对化: Relativize 参数化（firstAbs=7，哨兵 0 保留，逆变换闭合）"
            + " + contentRef 重映射（越界落哨兵）", rel && inverse && remap);
    }

    /// <summary>
    /// 冻结前置门 FGM903：派生列未定形时**不许**继续冻结（离线 Extract 会沿陈旧 world 发射，
    /// 产物看起来完全正常——这正是它必须是一道有声门而不是注释的理由）。
    /// </summary>
    private static void FreezeStaleDerivedRejected(string dir)
    {
        ShapedPackage s = ShapeFixture(dir, "Cooldown");
        ShapedComponent sc = s.Components[0];
        // 改一个节点的 authored 位置但不跑 Tick：world 列自此陈旧（P6 是它唯一的写者）。
        NodeHandle victim = sc.Locals.HandleOf(1);
        ResolvedGeom g = sc.Table.GetResolved(victim);
        sc.Table.SetPosition(victim, g.X + 13f, g.Y + 7f);

        bool threw = false;
        string msg = string.Empty;
        try { FgbCompiler.Freeze(s); }
        catch (FgmCompileException e) { threw = true; msg = e.Message; }
        Check("freeze 前置门: 派生列陈旧 ⇒ FGM903 编译错（离线 Extract 不许沿陈旧 world 发射）",
            threw && msg.Contains(FgmCodes.FreezeSelfCheck) && msg.Contains("派生列未定形"));
    }

    /// <summary>
    /// CNST 段的三个记录宽必须等于**运行期 struct 的实宽**——段是 <c>Cast</c> 直读的，
    /// 宽度写死在段布局里而 struct 变了，症状是整段错位而不是编译错。
    /// </summary>
    private static void FreezeRecordWidthsMatchRuntimeStructs()
    {
        int op = System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ConstraintOp[1].AsSpan()).Length;
        int fan = System.Runtime.InteropServices.MemoryMarshal.AsBytes(new FanOut[1].AsSpan()).Length;
        int quad = System.Runtime.InteropServices.MemoryMarshal.AsBytes(new QuadInstance[1].AsSpan()).Length;
        int clip = System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ClipEntry[1].AsSpan()).Length;
        Check("freeze 记录宽: CNST/QUAD/CLIP 的段内宽 == 运行期 struct 实宽（Cast 直读的前提）",
            op == FgbCnstSection.OpBytes && fan == FgbCnstSection.FanBytes
            && sizeof(ushort) == FgbCnstSection.IndexBytes
            && quad == Abi.QuadInstanceSize && clip == Abi.ClipEntrySize);
    }

    /// <summary>
    /// 跨包依赖：DEPS 不写、combinedRefHash 恒零，**且有声**（FGM304）——不是静默省略。
    /// 两支都判：有依赖的包必出诊断，没依赖的包必**不**出（诊断恒出等于诊断无信息量）。
    /// </summary>
    private static void FreezeCrossPackageDeferred(string dir)
    {
        bool ok = true;
        int withDeps = 0, without = 0;
        foreach (string name in FreezeFixtures)
        {
            CompileResult? r = FreezeFixture(dir, name, out ShapedPackage s);
            if (r == null) { ok = false; continue; }
            int deps = s.Package!.Dependencies.Length;
            bool said = r.Diagnostics.Has(FgmCodes.CrossPackageDeferred);
            ok &= TryOpenBlob(r, out FgbBlobView v)
                && v.CombinedRefHash == 0ul
                && v.TryGetSection(AbiLayout.FgbSectionDeps, out ReadOnlySpan<byte> depSec)
                && depSec.Length == deps * AbiLayout.FgbDepSize
                && said == (deps > 0);
            // DEPS 的**条目**写得出（id 在描述符里），**期望哈希**写不出（要链上各包的 sourceHash）。
            // 逐条必须是零 = 「未知」——装载门 4 据此记 unverified 而不判不符；
            // 写成非零就是编造对照物，那比不写更糟。
            if (ok)
            {
                v.TryGetSection(AbiLayout.FgbSectionDeps, out ReadOnlySpan<byte> dsec);
                for (int i = 0; i < deps; i++)
                    ok &= FgbRecordIo.ReadU64(dsec.Slice(i * AbiLayout.FgbDepSize, AbiLayout.FgbDepSize),
                              AbiLayout.FgbDepExpectedSourceHashOffset) == 0ul
                        && FgbRecordIo.ReadU64(dsec.Slice(i * AbiLayout.FgbDepSize, AbiLayout.FgbDepSize),
                              AbiLayout.FgbDepPkgIdOffset) != 0ul;
            }
            if (deps > 0) withDeps++; else without++;
        }
        Check("freeze 跨包: DEPS 逐条 {pkgId, expectedSourceHash = 0（未知）} + combinedRefHash 恒零"
            + " + FGM304 与依赖存在与否同真值（有依赖 " + withDeps + " 包 / 无依赖 " + without + " 包）", ok);
    }
}
