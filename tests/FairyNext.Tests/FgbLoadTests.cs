using System.Buffers.Binary;
using System.Runtime.InteropServices;
using FairyNext.AbiGen;
using FairyNext.Backend.Mock;
using FairyNext.Compiler;
using FairyNext.Compiler.Shape;
using FairyNext.Contracts;
using FairyNext.Core;
using FairyNext.Core.Fgb;
using FairyNext.Core.Layout;
using FairyNext.Core.Rendering;
using FairyNext.Numerics;

namespace FairyNext.Tests;

/// <summary>
/// M1-22 装载三操作 + 同步实例化（Program 的 partial 分片）。
///
/// 本包上线的门与它们各自证明什么：
/// ① **装载门矩阵**（门 1-5）——结构性拒载与哈希级降级的**二分**是可执行的：
///    结构性 ⇒ <c>package == null</c> 且报告说得出哪一门；哈希级 ⇒ 包能用、画面照常、有声计数。
///    分档判据住 <see cref="FgbGateClass"/>，两条路径各有用例。
/// ② **实例化等价性**（等价性金样的第三条腿）——从 FGB 装载并实例化出的树，与 M1-20a 的
///    <c>TreeBuilder</c> 建的那棵树：21 根列逐位同（拓扑列按组件内相对换算）、P5 收敛后的
///    resolved 逐位同、Extract 出的 <c>CanonicalStream</c> 逐字节同。M1-20b 写下的边界
///    「另一份宿主从 FGB 装载后再 Extract 归 M1-22」在这里兑现——门的两端自此各有独立的树来源。
/// ③ **arm-not-mount**——装载 + 实例化之后，CONT 一条没解、纹理一张没装、实例仍是 Armed；
///    第一次 Extract 才绑。首用时身份链对不上的实例转 inactive + 计数，**不画错**。
/// ④ **fuzz 常设门**（M1-19 的雏形转正）——2048 轮、seed 固定，语料扩到十一种段内记录；
///    断言：要么干净拒载（package == null + 门号），要么正常装载且**全视图可走、可实例化**，
///    绝不越界、绝不半载、绝不 panic。
///
/// 对照物纪律同前两包：等价性比的是**两个驱动**（编译器离线 vs 装载实例化），不是两份实现；
/// 常量来自编译产物本身与 ABI 表，不拿实现验实现。
/// </summary>
public static partial class Program
{
    /// <summary>装载/实例化跑全集的六个样例包。</summary>
    private static readonly string[] LoadFixtures =
        { "VirtualList", "Cooldown", "ScrollPane", "TextMeshPro", "PullToRefresh", "TurnPage" };

    private static void FgbLoadSuite()
    {
        string? root = RepoRoot.Find(AppContext.BaseDirectory);
        if (root == null) { Check("load: 定位仓库根", false); return; }
        string dir = RepoRoot.ToAbsolute(root, FuiFixtureDir);

        // ① 装载门矩阵 + 报告
        LoadGuard("门矩阵", () => LoadHappyPathAndReport(dir));
        LoadGuard("门 5", () => LoadGateFileName(dir));
        LoadGuard("门 4a", () => LoadGateIdentityDegrade(dir));
        LoadGuard("门 4b", () => LoadGateDeps(dir));
        LoadGuard("结构性拒载", () => LoadStructuralRejects(dir));
        LoadGuard("降级二分", () => LoadDegradeBisection(dir));

        // ② 绑定：PTCH 回填
        LoadGuard("PTCH 回填", () => LoadPatchBackfill(dir));
        LoadGuard("PTCH 破坏性", () => LoadPatchIsDestructive(dir));

        // ③ 实例化等价性（金样第三条腿）
        LoadGuard("列等价", () => InstantiateColumnsEqualTreeBuilder(dir));
        LoadGuard("resolved 等价", () => InstantiateResolvedEqualsCompile(dir));
        LoadGuard("Extract 等价", () => InstantiateExtractEqualsFrozen(dir));
        LoadGuard("嵌套 slab", () => InstantiateNestedSlab(dir));
        LoadGuard("基址回填往返", () => InstantiateRebaseRoundTrip(dir));

        // ④ arm-not-mount / Pool / fuzz
        LoadGuard("arm-not-mount", () => ArmNotMount(dir));
        LoadGuard("arm 失败面", () => ArmChainInactiveNotWrongPicture(dir));
        LoadGuard("Pool", () => PoolReuseNoResidue(dir));
        LoadGuard("fuzz 常设门", () => LoadFuzzGate(dir));
    }

    /// <summary>
    /// 逐条包异常：逃出 runner 的异常 = 没有 <c>RESULT</c> 判定行 = CI 什么也读不到。
    /// 装载与实例化会碰内核的门断言（<c>UiAssert</c> 在 Debug 下抛），故每条用例外面都要有括号——
    /// 包在整套外面也行，但那样第一条炸了后面十六条就全看不见了，杀变异时尤其难受。
    /// </summary>
    private static void LoadGuard(string name, Action body)
    {
        try { body(); }
        catch (Exception e) { Check("load " + name + ": 用例逃出异常 —— " + e.GetType().Name + ": " + e.Message, false); }
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    /// <summary>纹理通道：**与包内编号同号**。等价性比较需要两侧的段键可比——</summary>
    /// <remarks>
    /// 段键里的纹理编号在装载期被 PTCH 换成宿主发的号，而编译期的流里是包内号。
    /// 让宿主原号发放，两侧就落在同一个坐标系里；回填本身另有专门用例（<c>LoadPatchBackfill</c>）
    /// 用**刻意错开**的编号来证明它真的写进去了。
    /// </remarks>
    private sealed class IdentityAssetSource : IAssetSource
    {
        public int Acquires;
        public bool TryResolve(in FgbTexRef symbol, out uint texId) { texId = symbol.LocalTexId; return true; }
        public bool TryAcquire(uint texId) { Acquires++; return true; }
    }

    /// <summary>吸收型排水器（无渲染管线的宿主用；一个通道一个消费者是内核纪律）。</summary>
    private sealed class LoadSink : IChannelDrain
    {
        public Ch Consumes => Ch.Content | Ch.Transform | Ch.Color | Ch.Visible | Ch.Structure;
        public void Drain(ref FrameContext ctx, Ch channel, ReadOnlySpan<NodeHandle> queue) { }
    }

    /// <summary>一个装载宿主：真树 + 真内核 + 真布局引擎（+ 可选真渲染管线）。</summary>
    private sealed class LoadHost
    {
        public NodeTable Table = null!;
        public UiKernel Kernel = null!;
        public LayoutEngine Layout = null!;
        public FgbInstantiator Inst = null!;
        public RenderStream? Stream;
        public RenderPipeline? Pipe;
    }

    private static int _loadTickEpoch;

    private static LoadHost NewLoadHost(FgbPackage pkg, bool pipeline)
    {
        var table = new NodeTable(7);
        var inval = new Invalidation(table);
        var kernel = new UiKernel(table, inval);
        var layout = new LayoutEngine(kernel) { IdempotenceGate = true, DifferentialGate = true };
        layout.Attach();
        var host = new LoadHost
        {
            Table = table,
            Kernel = kernel,
            Layout = layout,
            Inst = new FgbInstantiator(table, pkg, layout),
        };
        if (pipeline)
        {
            host.Stream = new RenderStream("load-host");
            host.Pipe = new RenderPipeline(kernel, host.Stream, host.Inst.ContentSource, new NullBackend())
            {
                Present = false,
                DerivedOracle = true,
            };
            host.Pipe.Attach();
        }
        else
        {
            inval.Register(new LoadSink());
        }
        return host;
    }

    /// <summary>驱动一帧（时刻单调——Clock 不许回退）。</summary>
    private static void LoadTick(LoadHost h, int times = 1)
    {
        double baseNow = 100.0 + _loadTickEpoch * 0.5;
        _loadTickEpoch += times + 1;
        for (int i = 0; i < times; i++)
        {
            var t = new FrameTime(1f / 60f, 1f / 60f, baseNow + (i + 1) / 60.0, baseNow + (i + 1) / 60.0);
            h.Kernel.Tick(in t);
        }
    }

    /// <summary>编译一个样例包（定形 + 冻结共用一个世界；失败在测试面是一条 FAIL 不是异常）。</summary>
    private static CompileResult? LoadCompile(string dir, string name, out ShapedPackage shaped)
    {
        shaped = ShapeFixture(dir, name);
        try { return FgbCompiler.Freeze(shaped); }
        catch (FgmCompileException e) { Console.WriteLine("     " + name + " 冻结失败：\n" + e.Message); return null; }
    }

    private static FgbPackage? LoadPkg(CompileResult r, out FgbLoadReport report,
        string? fileName = null, int scale = -1, int branch = -1,
        IAssetSource? textures = null, IFgbPackageRegistry? packages = null,
        bool verifySelfHash = true, ushort slabDomain = 0)
    {
        var opts = new FgbLoadOptions(fileName, scale, branch, textures, packages, verifySelfHash, slabDomain);
        FgbPackage.TryLoad(r.Blob.ToArray(), in opts, out FgbPackage? p, out report);
        return p;
    }

    /// <summary>改一段头字节后把 selfHash 补钉回去（否则一切都停在门 3，深门永远没语料）。</summary>
    private static byte[] LoadRehash(byte[] blob)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(AbiLayout.FgbHeaderSelfHashOffset), 0ul);
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(AbiLayout.FgbHeaderSelfHashOffset),
            FgbBlobView.ComputeSelfHash(blob));
        return blob;
    }

    private static byte[] LoadPatchU64(byte[] src, int offset, ulong v)
    {
        var copy = (byte[])src.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan(offset), v);
        return copy;
    }

    private static byte[] LoadPatchU32(byte[] src, int offset, uint v)
    {
        var copy = (byte[])src.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(offset), v);
        return copy;
    }

    /// <summary>blob 里某段的字节区间（改段内记录的语料口；测试自己解目录，不走被测代码）。</summary>
    private static bool LoadSectionRange(byte[] blob, uint fourcc, out int offset, out int length)
    {
        offset = 0; length = 0;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(AbiLayout.FgbHeaderSectionCountOffset));
        for (int i = 0; i < (int)count; i++)
        {
            int e = Abi.FgbHeaderSize + i * Abi.FgbSectionDirEntrySize;
            if (BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(e + AbiLayout.FgbDirFourccOffset)) != fourcc) continue;
            offset = (int)BinaryPrimitives.ReadUInt64LittleEndian(blob.AsSpan(e + AbiLayout.FgbDirOffsetOffset));
            length = (int)BinaryPrimitives.ReadUInt64LittleEndian(blob.AsSpan(e + AbiLayout.FgbDirLengthOffset));
            return true;
        }
        return false;
    }

    private static FgbGate LoadGateOf(byte[] blob, in FgbLoadOptions opts)
    {
        FgbPackage.TryLoad(blob, in opts, out FgbPackage? p, out FgbLoadReport rep);
        return p == null ? rep.RejectedBy : FgbGate.None;
    }

    // ── ① 装载门矩阵 ────────────────────────────────────────────────────────

    /// <summary>
    /// 正路：六包全部装得上、出口 Loaded、逐门判决表把跑过的门一条不落地记下来。
    /// 报告的确定性另钉一条：**两次独立编译 + 装载的 <see cref="FgbLoadReport.Describe"/> 逐字符相同**
    /// ——报告要能当断言锚用，就不能带时间戳、地址、遍历序这类会漂的东西。
    /// </summary>
    private static void LoadHappyPathAndReport(string dir)
    {
        bool ok = true;
        int comps = 0, plans = 0, patches = 0;
        foreach (string name in LoadFixtures)
        {
            CompileResult? r = LoadCompile(dir, name, out ShapedPackage s);
            if (r == null) { ok = false; continue; }
            FgbPackage? p = LoadPkg(r, out FgbLoadReport rep, textures: new IdentityAssetSource());
            if (p == null)
            {
                Console.WriteLine("     " + name + " 装载被拒：" + rep.Detail);
                ok = false;
                continue;
            }
            comps += p.ComponentCount;
            plans += p.PlanStepCount;
            patches += rep.PatchCount;
            ok &= rep.Outcome == FgbLoadOutcome.Loaded
                && rep.RejectedBy == FgbGate.None
                && p.ComponentCount == s.Components.Count
                && !p.Degraded
                && rep.ElapsedTicks >= 0;
            // 五道门全在判决表里（门 1/2/3 由容器记，4/5 由身份门记）——「逐门逐项可读」不是口号。
            foreach (FgbGate g in new[]
            {
                FgbGate.Magic, FgbGate.SectionBounds, FgbGate.SelfHash, FgbGate.SectionMissing,
                FgbGate.SectionShape, FgbGate.NodeSectionShape, FgbGate.CompRange, FgbGate.NodeTopology,
                FgbGate.PlanShape, FgbGate.PatchRange, FgbGate.FileName, FgbGate.IdentityMismatch,
                FgbGate.DepsMismatch,
            })
            {
                bool present = false;
                for (int i = 0; i < rep.Gates.Count; i++) present |= rep.Gates[i].Gate == g;
                if (!present) Console.WriteLine("     " + name + " 判决表缺门 " + g);
                ok &= present;
            }
            for (int i = 0; i < rep.Gates.Count; i++) ok &= !rep.Gates[i].Tripped;
            // 组件名可解（AssetServer.Resolve 的 M1 形态）
            for (int i = 0; i < s.Components.Count; i++)
            {
                ShapedComponent sc = s.Components[i];
                ok &= p.TryResolve(FnvHash.Hash32(sc.Item.Name ?? sc.Item.Id), out int ci) && ci == i;
                ok &= p.StringAt(p.Component(i).NameStr) == (sc.Item.Name ?? sc.Item.Id);
            }
        }
        Check("load 门矩阵: 六包装载出口 Loaded（" + comps + " 模板 / " + plans + " 步 / "
            + patches + " 条回填），十三道门逐条判决入报告且全绿", ok && comps >= 25 && plans > comps && patches > 0);

        // 报告的确定性（同输入两次的人读面逐字符同）
        CompileResult? a = LoadCompile(dir, "PullToRefresh", out _);
        CompileResult? b = LoadCompile(dir, "PullToRefresh", out _);
        if (a == null || b == null) { Check("load 报告: 确定性", false); return; }
        LoadPkg(a, out FgbLoadReport ra, fileName: null, textures: new IdentityAssetSource());
        LoadPkg(b, out FgbLoadReport rb, fileName: null, textures: new IdentityAssetSource());
        string da = ra.Describe(), db = rb.Describe();
        if (da != db) Console.WriteLine("     报告不确定：\n" + da + "----\n" + db);
        Check("load 报告: Describe 确定性（同输入两次逐字符同）+ 出口/门/段三段齐",
            da == db && da.StartsWith("load loaded", StringComparison.Ordinal)
            && da.Contains("gate 19 FileName skip") && da.Contains("sections STRT="));
    }

    /// <summary>
    /// 门 5：文件名**以 id 定身份**。正名通过；换 id / 换档位 / 缺 <c>_id</c> 段一律拒载
    /// （部署错配是「改名没重烘」，不是内容陈旧，故它在结构性一档而不是降级一档）。
    /// </summary>
    private static void LoadGateFileName(string dir)
    {
        CompileResult? r = LoadCompile(dir, "PullToRefresh", out ShapedPackage s);
        if (r == null) { Check("load 门 5: 文件名身份", false); return; }
        string id = s.Package!.Id;
        var tex = new IdentityAssetSource();

        FgbPackage? good = LoadPkg(r, out FgbLoadReport okRep,
            fileName: "PullToRefresh_" + id + ".s1.b0.fgb", textures: tex);
        // 缺省变体段 == .s0.b0；本包编译时 scale=1 故省略写法必须被判不符（不是「宽容」）。
        FgbPackage? noVariant = LoadPkg(r, out FgbLoadReport nvRep, fileName: "PullToRefresh_" + id + ".fgb");
        FgbPackage? wrongId = LoadPkg(r, out FgbLoadReport idRep, fileName: "PullToRefresh_zzzz.s1.b0.fgb");
        FgbPackage? wrongScale = LoadPkg(r, out FgbLoadReport scRep, fileName: "PullToRefresh_" + id + ".s7.b0.fgb");
        FgbPackage? malformed = LoadPkg(r, out FgbLoadReport mfRep, fileName: "PullToRefresh.fgb");
        FgbPackage? notFgb = LoadPkg(r, out FgbLoadReport exRep, fileName: "PullToRefresh_" + id + ".bin");

        bool ok = good != null && okRep.Outcome == FgbLoadOutcome.Loaded
            && noVariant == null && nvRep.RejectedBy == FgbGate.FileName
            && wrongId == null && idRep.RejectedBy == FgbGate.FileName
            && wrongScale == null && scRep.RejectedBy == FgbGate.FileName
            && malformed == null && mfRep.RejectedBy == FgbGate.FileName
            && notFgb == null && exRep.RejectedBy == FgbGate.FileName;

        // 纯字符串解析面（路径前缀、变体段顺序无关、重复段拒）
        bool parse = FgbFileName.TryParse("a/b/Pkg_abcd.b3.s2.fgb", out FgbFileName f1, out _)
            && f1.Id == "abcd" && f1.Name == "Pkg" && f1.ScaleLevel == 2 && f1.BranchId == 3
            && FgbFileName.TryParse("Pkg_abcd.fgb", out FgbFileName f2, out _)
            && f2.ScaleLevel == 0 && f2.BranchId == 0
            && !FgbFileName.TryParse("Pkg_abcd.s1.s2.fgb", out _, out _)
            && !FgbFileName.TryParse("Pkgabcd.fgb", out _, out _);
        Check("load 门 5: 文件名以 id 定身份——正名过，换 id/换档/缺 _id/错扩展名四路拒载；"
            + "解析面支持路径前缀与变体段乱序、重复段拒", ok && parse);
    }

    /// <summary>门 4a：档位与宿主全局设定不符 ⇒ **降级**（包仍可用）——不是拒载。</summary>
    private static void LoadGateIdentityDegrade(string dir)
    {
        CompileResult? r = LoadCompile(dir, "ScrollPane", out _);
        if (r == null) { Check("load 门 4a: 档位降级", false); return; }
        FgbPackage? match = LoadPkg(r, out FgbLoadReport mRep, scale: 1, branch: 0, textures: new IdentityAssetSource());
        FgbPackage? bad = LoadPkg(r, out FgbLoadReport bRep, scale: 3, branch: 0, textures: new IdentityAssetSource());
        bool ok = match != null && !match.Degraded && mRep.Outcome == FgbLoadOutcome.Loaded
            && bad != null && bad.Degraded && bRep.Outcome == FgbLoadOutcome.Degraded
            && bRep.RejectedBy == FgbGate.None
            && bRep.Tripped(FgbGate.IdentityMismatch)
            && bRep.DegradedComponents == bad.ComponentCount
            && bRep.Describe().Contains("gate 18 IdentityMismatch degrade");
        Check("load 门 4a: scaleLevel 与全局设定不符 ⇒ 降级 + 计数（包可用、报告说得出哪一门），"
            + "相符则 Loaded", ok);
    }

    /// <summary>
    /// 门 4b：DEPS 逐项 + combinedRefHash **重算**。四种语料各一条断言：
    /// 未知（编译面给不出，记 unverified，**不降级**）/ 依赖缺席 / 期望哈希不符 / 相符。
    /// </summary>
    private static void LoadGateDeps(string dir)
    {
        CompileResult? r = LoadCompile(dir, "Cooldown", out ShapedPackage s);
        if (r == null) { Check("load 门 4b: DEPS", false); return; }
        int deps = s.Package!.Dependencies.Length;
        if (deps == 0) { Check("load 门 4b: Cooldown 依赖表非空（语料前提）", false); return; }
        ulong depPkgId = FnvHash.Hash64(s.Package!.Dependencies[0].Id ?? "");

        // ① 未知：编译产物原样（expectedSourceHash 全零）——记 unverified，不降级。
        FgbPackage? unk = LoadPkg(r, out FgbLoadReport unkRep, textures: new IdentityAssetSource());
        bool ok = unk != null && !unk.Degraded && unkRep.DepsUnverified == deps
            && unkRep.DepCount == deps && !unkRep.Tripped(FgbGate.DepsMismatch);

        // ② 把第一条依赖的期望哈希写成非零 —— 从此它可被比对。
        byte[] blob = r.Blob.ToArray();
        if (!LoadSectionRange(blob, AbiLayout.FgbSectionDeps, out int depOff, out _))
        {
            Check("load 门 4b: DEPS 段在场", false);
            return;
        }
        const ulong Want = 0x1234_5678_9ABC_DEF0UL;
        byte[] armed = LoadRehash(LoadPatchU64(blob, depOff + AbiLayout.FgbDepExpectedSourceHashOffset, Want));

        var optsNoReg = new FgbLoadOptions(null, -1, -1, new IdentityAssetSource(), null, true, 0);
        FgbPackage.TryLoad((byte[])armed.Clone(), in optsNoReg, out FgbPackage? miss, out FgbLoadReport missRep);
        ok &= miss != null && miss.Degraded && missRep.Tripped(FgbGate.DepsMismatch)
            && missRep.Detail.Length >= 0 && missRep.Outcome == FgbLoadOutcome.Degraded;

        var regBad = new FgbPackageRegistry();
        regBad.Register(depPkgId, Want ^ 1UL);
        var optsBad = new FgbLoadOptions(null, -1, -1, new IdentityAssetSource(), regBad, true, 0);
        FgbPackage.TryLoad((byte[])armed.Clone(), in optsBad, out FgbPackage? mism, out FgbLoadReport mismRep);
        ok &= mism != null && mism.Degraded && mismRep.Tripped(FgbGate.DepsMismatch);

        var regOk = new FgbPackageRegistry();
        regOk.Register(depPkgId, Want);
        var optsOk = new FgbLoadOptions(null, -1, -1, new IdentityAssetSource(), regOk, true, 0);
        FgbPackage.TryLoad((byte[])armed.Clone(), in optsOk, out FgbPackage? good, out FgbLoadReport goodRep);
        ok &= good != null && !good.Degraded && !goodRep.Tripped(FgbGate.DepsMismatch)
            && goodRep.DepsUnverified == deps - 1;

        // ③ 头内 combinedRefHash 写成垃圾 ⇒ 重算不符 ⇒ 降级（**重算**而不是信头里的数）。
        byte[] combined = LoadRehash(LoadPatchU64(blob, AbiLayout.FgbHeaderCombinedRefHashOffset, 0xDEADBEEFUL));
        var optsC = new FgbLoadOptions(null, -1, -1, new IdentityAssetSource(), null, true, 0);
        FgbPackage.TryLoad(combined, in optsC, out FgbPackage? cp, out FgbLoadReport cRep);
        ok &= cp != null && cp.Degraded && cRep.Tripped(FgbGate.DepsMismatch);

        Check("load 门 4b: DEPS 四路——未知(0)记 unverified 不降级 / 依赖缺席降级 / 期望哈希不符降级 / "
            + "相符 Loaded；头内 combinedRefHash 与重算不符也降级", ok);
    }

    /// <summary>
    /// 结构性拒载的十一路语料：容器四路（magic/版本/selfHash/截断）+ 段内七路
    /// （缺段 / STRT 池越界 / CNST 账不符 / COMP 区间越段 / NODE 相对下标越块 / 拓扑成环 /
    /// PLAN 后序破坏 / PTCH 目标越界）。**每一路都必须 package == null 且门号说得出是谁**。
    /// </summary>
    private static void LoadStructuralRejects(string dir)
    {
        CompileResult? r = LoadCompile(dir, "VirtualList", out _);
        if (r == null) { Check("load 结构性拒载", false); return; }
        byte[] blob = r.Blob.ToArray();
        var opts = new FgbLoadOptions(null, -1, -1, new IdentityAssetSource(), null, true, 0);
        bool ok = true;

        ok &= LoadGateOf(LoadPatchU32(blob, AbiLayout.FgbHeaderMagicOffset, 0xDEADBEEF), in opts) == FgbGate.Magic;
        ok &= LoadGateOf(LoadPatchU32(blob, AbiLayout.FgbHeaderFormatVersionOffset, 999u), in opts) == FgbGate.FormatVersion;
        ok &= LoadGateOf(LoadPatchU64(blob, AbiLayout.FgbHeaderSelfHashOffset, 1ul), in opts) == FgbGate.SelfHash;
        ok &= LoadGateOf(blob.AsSpan(0, 32).ToArray(), in opts) == FgbGate.Truncated;

        // 缺段：把 COMP 的 fourcc 改成未知值（未知段被跳过 ⇒ 必备段缺席）。
        byte[] noComp = (byte[])blob.Clone();
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(noComp.AsSpan(AbiLayout.FgbHeaderSectionCountOffset));
        for (int i = 0; i < (int)count; i++)
        {
            int e = Abi.FgbHeaderSize + i * Abi.FgbSectionDirEntrySize;
            if (BinaryPrimitives.ReadUInt32LittleEndian(noComp.AsSpan(e + AbiLayout.FgbDirFourccOffset))
                != AbiLayout.FgbSectionComp) continue;
            BinaryPrimitives.WriteUInt32LittleEndian(noComp.AsSpan(e + AbiLayout.FgbDirFourccOffset), 0x5A5A5A5Au);
        }
        ok &= LoadGateOf(LoadRehash(noComp), in opts) == FgbGate.SectionMissing;

        // STRT：池字节数写大 ⇒ 布局账不符。
        ok &= SectionMutate(blob, AbiLayout.FgbSectionStrt, AbiLayout.FgbStrtHeaderPoolBytesOffset, 0xFFFFu, in opts)
            == FgbGate.SectionShape;
        // CNST：算子数写大 ⇒ 四数组账不符。
        ok &= SectionMutate(blob, AbiLayout.FgbSectionCnst, AbiLayout.FgbCnstHeaderOpCountOffset, 4096u, in opts)
            == FgbGate.SectionShape;
        // COMP[0]：quadStart 写大 ⇒ 区间越段。
        ok &= SectionMutate(blob, AbiLayout.FgbSectionComp, AbiLayout.FgbCompQuadStartOffset, 1u << 20, in opts)
            == FgbGate.CompRange;
        // PLAN[0]：compIndex 写大 ⇒ 越 COMP 段。
        ok &= SectionMutate(blob, AbiLayout.FgbSectionPlan, AbiLayout.FgbPlanCompIndexOffset, 9999u, in opts)
            == FgbGate.PlanShape;
        // PTCH[0]：texRef 写大 ⇒ 越 TREF 段。
        ok &= SectionMutate(blob, AbiLayout.FgbSectionPtch, AbiLayout.FgbPatchTexRefOffset, 9999u, in opts)
            == FgbGate.PatchRange;

        // NODE：把第 1 行的 parent 相对下标写成越块值 ⇒ 回填后会指到别的实例的槽上。
        ok &= NodeColMutate(blob, AbiLayout.NodeColParent, 1, 0xFFFFu, in opts) == FgbGate.CompRange;
        // NODE：把第 1 行的 parent 写成自己 ⇒ 拓扑成环（paintOrder 展开会死循环，只能死在门里）。
        ok &= NodeColMutate(blob, AbiLayout.NodeColParent, 1, 2u, in opts) == FgbGate.NodeTopology;
        // NODE：局部 0 的 parent 非零 ⇒ 模板根不是根。
        ok &= NodeColMutate(blob, AbiLayout.NodeColParent, 0, 1u, in opts) == FgbGate.NodeTopology;

        // CNST 下标池：把某个组件的算子下标改成「包内合法、组件内越界」——段级的界拦不住它，
        // 而求值现场是按组件切片直接下标寻址的，越界在那里是数组越界不是错画面。
        ok &= CnstCrossComponentMutate(dir, in opts) == FgbGate.CompRange;

        Check("load 结构性拒载: 十三路语料（magic/版本/selfHash/截断 + 缺段/STRT/CNST/COMP/PLAN/PTCH/"
            + "NODE 越块/NODE 成环/根非根/CNST 跨组件下标）逐路 package == null 且门号命中", ok);
    }

    private static FgbGate SectionMutate(byte[] blob, uint fourcc, int fieldOffset, uint value, in FgbLoadOptions opts)
    {
        if (!LoadSectionRange(blob, fourcc, out int off, out int len) || len == 0) return FgbGate.None;
        return LoadGateOf(LoadRehash(LoadPatchU32(blob, off + fieldOffset, value)), in opts);
    }

    /// <summary>
    /// 造一条「包内合法、组件内越界」的 CNST 算子下标。挑的是**第一个算子数少于包内总数**的组件——
    /// 只有它的切片外还有别人的算子可指，这条门才有对象可拦。
    /// </summary>
    private static FgbGate CnstCrossComponentMutate(string dir, in FgbLoadOptions opts)
    {
        CompileResult? r = LoadCompile(dir, "ScrollPane", out _);
        if (r == null) return FgbGate.None;
        FgbPackage? p = LoadPkg(r, out _, textures: new IdentityAssetSource());
        if (p == null) return FgbGate.None;

        ReadOnlySpan<byte> cnst = p.CnstSection;
        int totalOps = (int)FgbRecordIo.ReadU32(cnst, AbiLayout.FgbCnstHeaderOpCountOffset);
        int fans = (int)FgbRecordIo.ReadU32(cnst, AbiLayout.FgbCnstHeaderFanCountOffset);
        int victim = -1;
        for (int i = 0; i < p.ComponentCount; i++)
        {
            FgbComponentDef d = p.Component(i);
            if (d.CnstIdxCount > 0 && d.CnstOpCount > 0 && d.CnstOpCount < totalOps) { victim = i; break; }
        }
        if (victim < 0 || totalOps < 2) return FgbGate.None;

        FgbComponentDef vd = p.Component(victim);
        byte[] blob = r.Blob.ToArray();
        if (!LoadSectionRange(blob, AbiLayout.FgbSectionCnst, out int off, out _)) return FgbGate.None;
        int at = off + FgbCnstSection.IndicesOffset(totalOps, fans)
            + vd.CnstIdxStart * FgbCnstSection.IndexBytes;
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(at), (ushort)(totalOps - 1));
        return LoadGateOf(LoadRehash(blob), in opts);
    }

    private static FgbGate NodeColMutate(byte[] blob, int column, int row, uint value, in FgbLoadOptions opts)
    {
        if (!LoadSectionRange(blob, AbiLayout.FgbSectionNode, out int off, out int len)) return FgbGate.None;
        int nodeCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off));
        int at = off + FgbNodeSection.ColumnOffset(column, nodeCount) + row * 4;
        return LoadGateOf(LoadRehash(LoadPatchU32(blob, at, value)), in opts);
    }

    /// <summary>
    /// 降级二分**本身**：结构性 ⇒ 无包可用；哈希级 ⇒ 包可用、能实例化、能画。
    /// 「响亮失败」与「慢但正确」不是二选一——这条用例就是那句话的可执行形式。
    /// </summary>
    private static void LoadDegradeBisection(string dir)
    {
        CompileResult? r = LoadCompile(dir, "PullToRefresh", out ShapedPackage s);
        if (r == null) { Check("load 降级二分", false); return; }

        // 结构性：版本不符 —— 拒载，一个字节也不信。
        FgbPackage.TryLoad(LoadPatchU32(r.Blob.ToArray(), AbiLayout.FgbHeaderFormatVersionOffset, 99u),
            new FgbLoadOptions(), out FgbPackage? rejected, out FgbLoadReport rejRep);

        // 哈希级：档位不符 —— 降级，但包照用：实例化 + 跑一帧，画面照出。
        FgbPackage? degraded = LoadPkg(r, out FgbLoadReport degRep, scale: 9, textures: new IdentityAssetSource());
        int quads = 0;
        bool drew = false;
        if (degraded != null)
        {
            LoadHost h = NewLoadHost(degraded, pipeline: true);
            for (int i = 0; i < degraded.ComponentCount; i++) h.Inst.Instantiate(i);
            h.Kernel.Invalidation.Mark(h.Table.Root, Ch.Structure, InvalidateReason.UserWrite);
            LoadTick(h);
            quads = h.Stream!.QuadCount;
            drew = quads > 0;
        }

        Check("load 降级二分: 结构性不符 ⇒ 拒载（package == null + 门 3/1 号）；哈希级失配 ⇒ 降级但"
            + "组件照常实例化并发射 " + quads + " 个实例（「响亮失败」与「慢但正确」各得其所）",
            rejected == null && rejRep.RejectedBy == FgbGate.FormatVersion
            && rejRep.Outcome == FgbLoadOutcome.Rejected
            && degraded != null && degraded.Degraded && degRep.DegradedComponents == degraded.ComponentCount
            && drew);
    }

    // ── ② 绑定：PTCH 回填 ───────────────────────────────────────────────────

    /// <summary>
    /// PTCH 回填正确性：装载**前**段键与 CONT 里是包内编号，装载**后**全是宿主发的运行期编号，
    /// 且回填条数 == PTCH 记录数 == 报告里的 patch 数。
    /// 用刻意错开的编号（1000 起）——同号通道证明不了「写进去了」。
    /// </summary>
    private static void LoadPatchBackfill(string dir)
    {
        CompileResult? r = LoadCompile(dir, "TurnPage", out _);
        if (r == null) { Check("load PTCH: 回填", false); return; }
        byte[] before = r.Blob.ToArray();
        if (!LoadSectionRange(before, AbiLayout.FgbSectionPtch, out int pOff, out int pLen)
            || !LoadSectionRange(before, AbiLayout.FgbSectionCont, out int cOff, out _)
            || !LoadSectionRange(before, AbiLayout.FgbSectionSegs, out int sOff, out _))
        {
            Check("load PTCH: 三段在场", false);
            return;
        }
        int patches = pLen / AbiLayout.FgbPatchSize;

        var src = new NullAssetSource(1000u);
        FgbPackage? p = LoadPkg(r, out FgbLoadReport rep, textures: src);
        if (p == null) { Check("load PTCH: 装载", false); return; }

        bool ok = patches > 0 && rep.PatchCount == patches;
        int checkedCont = 0, checkedSeg = 0;
        for (int i = 0; i < patches; i++)
        {
            ReadOnlySpan<byte> rec = before.AsSpan(pOff + i * AbiLayout.FgbPatchSize, AbiLayout.FgbPatchSize);
            int texRef = (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbPatchTexRefOffset);
            int target = (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbPatchTargetOffset);
            ushort section = FgbRecordIo.ReadU16(rec, AbiLayout.FgbPatchSectionOffset);
            ushort slot = FgbRecordIo.ReadU16(rec, AbiLayout.FgbPatchSlotOffset);
            uint want = p.TextureRuntimeId(texRef);
            uint local = p.TextureSymbol(texRef).LocalTexId;
            ok &= want >= 1000u && local != want;      // 通道刻意错开：同号就证明不了什么
            if (section == Abi.FgbPatchSectionCont)
            {
                int at = cOff + target * AbiLayout.FgbContSize + AbiLayout.FgbContTexIdOffset;
                ok &= BinaryPrimitives.ReadUInt32LittleEndian(before.AsSpan(at)) == local;
                ok &= FgbRecordIo.ReadU32(p.ContSection.Slice(target * AbiLayout.FgbContSize, AbiLayout.FgbContSize),
                    AbiLayout.FgbContTexIdOffset) == want;
                checkedCont++;
            }
            else
            {
                int off = slot == 0 ? AbiLayout.FgbSegTex0Offset
                    : slot == 1 ? AbiLayout.FgbSegTex1Offset
                    : slot == 2 ? AbiLayout.FgbSegTex2Offset : AbiLayout.FgbSegTex3Offset;
                int at = sOff + target * AbiLayout.FgbSegSize + off;
                ok &= BinaryPrimitives.ReadUInt32LittleEndian(before.AsSpan(at)) == local;
                ok &= FgbRecordIo.ReadU32(p.SegsSection.Slice(target * AbiLayout.FgbSegSize, AbiLayout.FgbSegSize),
                    off) == want;
                checkedSeg++;
            }
        }

        // 解出来的内容记录拿到的就是回填后的编号（绑定不是「另存一份」，是同一份字节）。
        p.EnsureContentDecoded();
        bool decoded = false;
        for (uint cid = 1; cid < (uint)p.ContentCount; cid++)
        {
            ContentRecord rec = p.ContentAt(cid);
            if (rec.Kind != ExtractKind.Leaf || rec.Leaf.Texture.IsNone) continue;
            decoded |= rec.Leaf.Texture.Value >= 1000u;
            ok &= rec.Leaf.Texture.Value >= 1000u || rec.Leaf.Texture.Value == 0xF0F0u;
        }
        Check("load PTCH: " + patches + " 条回填（CONT " + checkedCont + " / SEGS " + checkedSeg
            + "）——装载前是包内编号、装载后是宿主编号，报告 patch 数相符，解码出的叶拿到的是回填值",
            ok && decoded && checkedCont > 0 && checkedSeg > 0);
    }

    /// <summary>
    /// 回填是**就地**的，故它是破坏性的：同一个数组二次装载被门 3 响亮拒掉。
    /// 这条不是缺陷而是取舍，用例把它钉成契约——宁可响亮失败，也不让第二次装载
    /// 悄悄用上一次的回填结果（那才是真正查不出来的那类错）。
    /// </summary>
    private static void LoadPatchIsDestructive(string dir)
    {
        CompileResult? r = LoadCompile(dir, "ScrollPane", out _);
        if (r == null) { Check("load PTCH: 就地回填的破坏性", false); return; }
        byte[] shared = r.Blob.ToArray();
        var o1 = new FgbLoadOptions(null, -1, -1, new NullAssetSource(1000u), null, true, 0);
        bool first = FgbPackage.TryLoad(shared, in o1, out FgbPackage? p1, out _);
        bool second = FgbPackage.TryLoad(shared, in o1, out FgbPackage? p2, out FgbLoadReport r2);
        Check("load PTCH: 就地回填 ⇒ 同一数组二次装载被门 3（selfHash）响亮拒，而不是静默复用",
            first && p1 != null && !second && p2 == null && r2.RejectedBy == FgbGate.SelfHash);
    }

    // ── ③ 实例化等价性 ─────────────────────────────────────────────────────

    /// <summary>
    /// 逐列比对一个实例块与它的模板树。
    /// 三条列有各自的判据（其余列逐字节相同）：
    ///  · 拓扑列（Rebase）：<c>abs = base + rel − 1</c> 的正变换；局部 0 的父/兄链由挂载决定，跳过；
    ///  · ownerInst：模板恒 0、实例恒块首（实例身份，不是模板数据）；
    ///  · resolvedRef：值是池下标（换个表就没意义），比的是**非零集合**——即「布局写集」相同。
    /// </summary>
    private static bool BlockMatchesTemplate(LoadHost h, FgbPackage pkg, ShapedComponent sc, FgbInstance inst,
        bool rootGeometryOverridden, out string why)
    {
        why = "";
        int n = sc.Locals.Count;
        uint first = sc.Locals.HandleOf(0).Index;
        uint b = inst.Segment.Base;
        // 嵌套块挂在宿主 localId 上：模板里那个占位节点没有孩子，实例里它有一个（嵌套根）。
        // 这是**挂载**的结果不是模板数据，故这些行的 firstChild 不参与比对。
        var nestedHosts = new HashSet<ushort>();
        for (int k = 0; k < inst.Nested.Count; k++)
        {
            NodeHandle host = h.Table.Parent(inst.Nested[k].Root);
            if (!host.IsNone) nestedHosts.Add(h.Table.LocalIdOf(host));
        }
        for (int col = 0; col < Abi.NodeColumns.Length; col++)
        {
            int w = Abi.NodeColumns[col].ElementSize;
            var tpl = new byte[w * n];
            var got = new byte[w * n];
            sc.Table.ExportColumn(col, first, n, tpl);
            h.Table.ExportColumn(col, b, n, got);

            if (col == AbiLayout.NodeColOwnerInst)
            {
                ReadOnlySpan<uint> t0 = MemoryMarshal.Cast<byte, uint>(tpl);
                ReadOnlySpan<uint> g0 = MemoryMarshal.Cast<byte, uint>(got);
                for (int k = 0; k < n; k++)
                    if (t0[k] != 0u || g0[k] != b) { why = "ownerInst[" + k + "] 模板 " + t0[k] + " 实例 " + g0[k]; return false; }
                continue;
            }
            if (col == AbiLayout.NodeColResolvedRef)
            {
                ReadOnlySpan<uint> t0 = MemoryMarshal.Cast<byte, uint>(tpl);
                ReadOnlySpan<uint> g0 = MemoryMarshal.Cast<byte, uint>(got);
                for (int k = 0; k < n; k++)
                    if ((t0[k] != 0u) != (g0[k] != 0u)) { why = "resolved 写集在 " + k + " 处不同"; return false; }
                continue;
            }
            if (col == AbiLayout.NodeColContentRef)
            {
                // 两侧是**两个 id 空间**：模板列是编译世界 ContentTable 的下标，冻结列是
                // canonical CONT 下标（去重把「同内容」变成「同 id」）。可比的是
                // 「有没有内容」与「解出来是不是同一种内容」——后者才是引用正确性。
                ReadOnlySpan<uint> t0 = MemoryMarshal.Cast<byte, uint>(tpl);
                ReadOnlySpan<uint> g0 = MemoryMarshal.Cast<byte, uint>(got);
                for (int k = 0; k < n; k++)
                {
                    if ((t0[k] != 0u) != (g0[k] != 0u)) { why = "contentRef 有无在 " + k + " 处不同"; return false; }
                    if (t0[k] == 0u) continue;
                    ContentRecord want = sc.Content.At(t0[k]);
                    ContentRecord real = pkg.ContentAt(g0[k]);
                    bool textLeaf = want.Kind == ExtractKind.Leaf && want.Leaf.Text != null;
                    if (real.OpensClip != want.OpensClip) { why = "contentRef[" + k + "] 裁剪域位不同"; return false; }
                    // 文本叶的样式未进 M1 冻结面 ⇒ 解成「不产渲染单元」（有声计数），是明码边界。
                    if (!textLeaf && real.Kind != want.Kind) { why = "contentRef[" + k + "] 种类不同"; return false; }
                }
                continue;
            }
            if (Abi.NodeColumns[col].Rebase)
            {
                bool link = col == AbiLayout.NodeColParent || col == AbiLayout.NodeColNextSib
                    || col == AbiLayout.NodeColPrevSib;
                ReadOnlySpan<uint> t0 = MemoryMarshal.Cast<byte, uint>(tpl);
                ReadOnlySpan<uint> g0 = MemoryMarshal.Cast<byte, uint>(got);
                for (int k = 0; k < n; k++)
                {
                    if (k == 0 && link) continue;                 // 根的链位由挂载决定
                    if (col == AbiLayout.NodeColFirstChild && nestedHosts.Contains((ushort)k)) continue;
                    uint want = t0[k] == 0u ? 0u : b + (t0[k] - first);
                    if (g0[k] != want) { why = Abi.NodeColumns[col].Name + "[" + k + "] 期望 " + want + " 实得 " + g0[k]; return false; }
                }
                continue;
            }
            if (rootGeometryOverridden && (col == AbiLayout.NodeColPosX || col == AbiLayout.NodeColPosY
                || col == AbiLayout.NodeColWidth || col == AbiLayout.NodeColHeight))
            {
                // 嵌套块的根几何由宿主框下传（见 FgbInstantiator.AttachNested 的明码边界），跳过首行。
                if (!tpl.AsSpan(w).SequenceEqual(got.AsSpan(w))) { why = Abi.NodeColumns[col].Name + " 非首行不同"; return false; }
                continue;
            }
            if (!tpl.AsSpan().SequenceEqual(got)) { why = Abi.NodeColumns[col].Name + " 列不逐字节同"; return false; }
        }
        return true;
    }

    /// <summary>
    /// 等价性金样第三条腿之一：六包全部模板 —— 装载实例化出的块，21 根列与 TreeBuilder 的树逐位同。
    /// 这一条同时是 memcpy 与基址回填的正确性锚：任一列漏搬或回填算错，都在这里逐字节现形。
    /// </summary>
    private static void InstantiateColumnsEqualTreeBuilder(string dir)
    {
        bool ok = true;
        int blocks = 0;
        foreach (string name in LoadFixtures)
        {
            CompileResult? r = LoadCompile(dir, name, out ShapedPackage s);
            if (r == null) { ok = false; continue; }
            FgbPackage? p = LoadPkg(r, out _, textures: new IdentityAssetSource());
            if (p == null) { ok = false; continue; }
            LoadHost h = NewLoadHost(p, pipeline: false);
            for (int i = 0; i < p.ComponentCount; i++)
            {
                FgbInstance inst = h.Inst.Instantiate(i);
                blocks++;
                if (!BlockMatchesTemplate(h, p, s.Components[i], inst, false, out string why))
                {
                    Console.WriteLine("     " + name + "/" + s.Components[i].Item.Id + " " + why);
                    ok = false;
                }
                // 段是分配单位、地址稳定；localId 寻址在实例上必须 O(1) 命中。
                for (ushort l = 0; l < (ushort)inst.Segment.Count; l++)
                    ok &= h.Table.LocalIdOf(inst.ChildByLocalId(h.Table, l)) == l;
            }
        }
        Check("load 实例化等价性: 六包 " + blocks + " 个顶层块——21 根列与 TreeBuilder 的树逐位同"
            + "（memcpy + 基址回填 + 实例身份 + resolved 写集）", ok && blocks >= 25);
    }

    /// <summary>
    /// 第二条腿：P5 收敛后的 **resolved 逐位同**。约束图从 CNST 段导入、经
    /// <c>LayoutEngine.Arm</c> 布防，offset/ratio 在实例化那一刻按同一份 authored 捕获，
    /// 于是求值结果必须与编译世界逐位相同。
    /// 顺带钉住 M1-16 的留缝：**LateSlotAllocs 恒零**——resolved 槽在实例化时按编译产物的
    /// 布局写集一次分齐，P5 里不再有迟到分配。
    /// 边界：带文本叶的组件不参与（M1 的文本样式未进冻结面，运行期没有度量器，
    /// 那些节点的 resolved 停在 authored——这是明码边界不是缺陷）。
    /// </summary>
    private static void InstantiateResolvedEqualsCompile(string dir)
    {
        bool ok = true;
        int comps = 0, nodes = 0;
        foreach (string name in LoadFixtures)
        {
            CompileResult? r = LoadCompile(dir, name, out ShapedPackage s);
            if (r == null) { ok = false; continue; }
            FgbPackage? p = LoadPkg(r, out _, textures: new IdentityAssetSource());
            if (p == null) { ok = false; continue; }
            for (int i = 0; i < p.ComponentCount; i++)
            {
                if (p.Component(i).HasTextLeaves) continue;
                LoadHost h = NewLoadHost(p, pipeline: false);
                FgbInstance inst = h.Inst.Instantiate(i);
                LoadTick(h, 2);
                ShapedComponent sc = s.Components[i];
                comps++;
                for (ushort l = 0; l < (ushort)inst.Segment.Count; l++)
                {
                    ResolvedGeom a = sc.Table.GetResolved(sc.Locals.HandleOf(l));
                    ResolvedGeom bg = h.Table.GetResolved(inst.ChildByLocalId(h.Table, l));
                    nodes++;
                    bool same = BitEquals.Eq(a.X, bg.X) && BitEquals.Eq(a.Y, bg.Y)
                        && BitEquals.Eq(a.W, bg.W) && BitEquals.Eq(a.H, bg.H);
                    if (!same)
                    {
                        Console.WriteLine("     " + name + "/" + sc.Item.Id + " 局部 " + l
                            + " 编译 " + a + " 实例 " + bg);
                        ok = false;
                    }
                }
                if (h.Layout.Stats.LateSlotAllocs != 0)
                {
                    Console.WriteLine("     " + name + "/" + sc.Item.Id + " 迟到槽分配 "
                        + h.Layout.Stats.LateSlotAllocs);
                    ok = false;
                }
            }
        }
        Check("load 实例化等价性: " + comps + " 个模板 " + nodes + " 个节点的 resolved 与编译世界逐位同"
            + "（CNST 导入 + Arm 捕获 + P5 求值），且迟到槽分配恒零（M1-16 留缝兑现）", ok && comps >= 8);
    }

    /// <summary>
    /// 第三条腿也是最硬的一条：**从 FGB 装载的树跑 Extract，与编译期冻结的流逐字节相同**。
    /// 门的两端自此各有独立的树来源——一端是 .fui 建出来的树，另一端是 blob memcpy 出来的树，
    /// 中间隔着整条冻结/装载/实例化链路。M1-20b 写下的边界 ① 在这里销账。
    ///
    /// 参与集：无文本叶（样式未进 M1 冻结面）且无嵌套步（嵌套块会带来编译面上不存在的子树）。
    /// </summary>
    private static void InstantiateExtractEqualsFrozen(string dir)
    {
        bool ok = true;
        int comps = 0, quads = 0;
        foreach (string name in LoadFixtures)
        {
            CompileResult? r = LoadCompile(dir, name, out ShapedPackage s);
            if (r == null) { ok = false; continue; }
            FgbPackage? p = LoadPkg(r, out _, textures: new IdentityAssetSource());
            if (p == null) { ok = false; continue; }
            for (int i = 0; i < p.ComponentCount; i++)
            {
                FgbComponentDef d = p.Component(i);
                if (d.HasTextLeaves || d.HasNested) continue;
                if (!r.TryGetComponent(s.Components[i].Item.Id, out FrozenComponent? fc)) { ok = false; continue; }
                LoadHost h = NewLoadHost(p, pipeline: true);
                h.Inst.Instantiate(i);
                h.Kernel.Invalidation.Mark(h.Table.Root, Ch.Structure, InvalidateReason.UserWrite);
                LoadTick(h, 2);

                StreamSnapshot pa = FreezeNorm(fc.Stream);
                StreamSnapshot pb = FreezeNorm(h.Stream!.Snapshot());
                byte[] ca = CanonicalStream.Canonicalize(pa);
                byte[] cb = CanonicalStream.Canonicalize(pb);
                int diff = CanonicalStream.FirstDifference(ca, cb);
                comps++;
                quads += pb.QuadCount;
                if (diff >= 0 || h.Pipe!.DerivedOracleFailures != 0)
                {
                    Console.WriteLine("     " + name + "/" + s.Components[i].Item.Id + " 首差异 @" + diff
                        + (diff < 0 ? "" : " " + CanonicalStream.Locate(pa, diff))
                        + " A=" + pa.QuadCount + " B=" + pb.QuadCount);
                    ok = false;
                }
            }
        }
        Check("load 实例化等价性: " + comps + " 个模板——**FGB 装载实例化出的树** Extract 的 CanonicalStream"
            + " 与编译期冻结的流逐字节同（" + quads + " 个实例；等价性金样的第三条腿）", ok && comps >= 5 && quads > 0);
    }

    /// <summary>
    /// 嵌套 slab：TurnPage 的 Main 是四层引用（Main → Book → Pages → Page），
    /// 后序计划把它展开成 11 步。断言四件事：
    ///  ① 展开出的块数 == planCount − 1，且每个嵌套块的列与**它自己的**模板树逐位同；
    ///  ② 每个嵌套块挂在宿主的 hostLocalId 上（挂错就是画在别处）；
    ///  ③ 块与块的 ownerInst 互不相同 —— 「跨实例块寻址」的断言这才有牙；
    ///  ④ 同一模板的两个块走**同一个 slab bin**（模板 id 相同）——GList 复用率的地基。
    /// </summary>
    private static void InstantiateNestedSlab(string dir)
    {
        CompileResult? r = LoadCompile(dir, "TurnPage", out ShapedPackage s);
        if (r == null) { Check("load 嵌套 slab", false); return; }
        FgbPackage? p = LoadPkg(r, out _, textures: new IdentityAssetSource());
        if (p == null) { Check("load 嵌套 slab: 装载", false); return; }

        int main = -1;
        for (int i = 0; i < p.ComponentCount; i++)
            if (p.Component(i).PlanCount > (main < 0 ? 1 : p.Component(main).PlanCount)) main = i;
        if (main < 0) { Check("load 嵌套 slab: 有嵌套的模板在场", false); return; }

        FgbComponentDef def = p.Component(main);
        LoadHost h = NewLoadHost(p, pipeline: false);
        FgbInstance top = h.Inst.Instantiate(main);

        var all = new List<FgbInstance>();
        void Walk(FgbInstance x) { all.Add(x); for (int i = 0; i < x.Nested.Count; i++) Walk(x.Nested[i]); }
        Walk(top);

        bool ok = all.Count == def.PlanCount && h.Inst.LiveCount == def.PlanCount;
        var owners = new HashSet<uint>();
        foreach (FgbInstance x in all)
        {
            ok &= owners.Add(x.Segment.Base);
            ok &= h.Table.OwnerInst(x.Root) == x.Segment.Base;
            ok &= BlockMatchesTemplate(h, p, s.Components[x.CompIndex], x, !ReferenceEquals(x, top), out string why);
            if (!ok) { Console.WriteLine("     嵌套块 " + x.CompIndex + " " + why); break; }
        }

        // ② 挂载点：每个嵌套块的父必须是**别的块**里的节点（宿主 localId 上的那个占位节点）
        int mounted = 0;
        foreach (FgbInstance x in all)
        {
            if (ReferenceEquals(x, top)) continue;
            NodeHandle parent = h.Table.Parent(x.Root);
            ok &= !parent.IsNone && h.Table.OwnerInst(parent) != x.Segment.Base;
            // 宿主框下传：子组件实例的自身尺寸 = 编辑器里在父模板上量的那个框，位置归零
            // （宿主已经承担了位置）。不下传就会按子组件的**源尺寸**画——那是一眼可见的错。
            ResolvedGeom hg = h.Table.GetResolved(parent);
            ResolvedGeom xg = h.Table.GetResolved(x.Root);
            ok &= BitEquals.Eq(xg.W, hg.W) && BitEquals.Eq(xg.H, hg.H)
                && BitEquals.Eq(xg.X, 0f) && BitEquals.Eq(xg.Y, 0f);
            mounted++;
        }

        // ④ 同模板同 bin
        var byComp = new Dictionary<int, int>();
        foreach (FgbInstance x in all)
        {
            byComp.TryGetValue(x.CompIndex, out int c);
            byComp[x.CompIndex] = c + 1;
            ok &= p.TemplateIdOf(x.CompIndex) == (0x8000_0000u | (uint)x.CompIndex);
        }
        Check("load 嵌套 slab: 后序 " + def.PlanCount + " 步展开出 " + all.Count + " 个块（嵌套 "
            + mounted + " 个各挂在宿主 localId 上且宿主框下传），逐块与自己的模板逐位同、ownerInst 互不相同、"
            + "同模板同 bin", ok && mounted == def.PlanCount - 1 && byComp.Count >= 3);
    }

    /// <summary>
    /// **基址回填的往返闭合**（本包对 M1-20b 那条存活变异的销账用例）。
    ///
    /// M1-20b 记在案：编译世界里组件根恒在槽 1 ⇒ 相对化是数值恒等 ⇒
    /// 「摘掉 <c>FgbFreezer.Relativize</c> 调用」在任何样例包上都不可观测。
    /// M1-22 把组件根改成**世界树根的孩子**（模板区间从槽 2 起）后，这条恒等假设消失：
    /// 冻结出的相对下标与绝对槽差一位，摘掉相对化会让装载门直接判「相对下标越块」，
    /// 或者让父指针回填到隔壁节点上。本用例正面钉住那条闭合链：
    /// 模板区间起点 ≠ 1、实例段基址 ≠ 模板起点，两次变换往返后拓扑逐位还原。
    /// </summary>
    private static void InstantiateRebaseRoundTrip(string dir)
    {
        CompileResult? r = LoadCompile(dir, "TurnPage", out ShapedPackage s);
        if (r == null) { Check("load 基址回填往返", false); return; }
        FgbPackage? p = LoadPkg(r, out _, textures: new IdentityAssetSource());
        if (p == null) { Check("load 基址回填往返: 装载", false); return; }

        // 前提：模板区间不从槽 1 起（否则相对化恒等，这条门没牙）。
        bool premise = true;
        for (int i = 0; i < s.Components.Count; i++) premise &= s.Components[i].Locals.HandleOf(0).Index != 1u;

        LoadHost h = NewLoadHost(p, pipeline: false);
        // 先占几个单槽，把 bump 顶端推开：否则第一个段恰好落在槽 2 = 模板起点，
        // 「基址与模板起点不同」这条前提就白写了（两个偏移相等时算错也看不出来）。
        for (int k = 0; k < 3; k++) h.Table.AddChild(h.Table.Root, h.Table.CreateNode());
        bool ok = premise;
        int checkedNodes = 0;
        var bases = new List<uint>();
        for (int i = 0; i < p.ComponentCount; i++)
        {
            FgbInstance inst = h.Inst.Instantiate(i);
            bases.Add(inst.Segment.Base);
            ShapedComponent sc = s.Components[i];
            uint first = sc.Locals.HandleOf(0).Index;
            ok &= inst.Segment.Base != first;              // 基址与模板起点也不同：两处偏移各自算对才行
            for (ushort l = 1; l < (ushort)inst.Segment.Count; l++)
            {
                NodeHandle tplNode = sc.Locals.HandleOf(l);
                NodeHandle insNode = inst.ChildByLocalId(h.Table, l);
                uint tplParent = sc.Table.Parent(tplNode).Index;
                uint insParent = h.Table.Parent(insNode).Index;
                ok &= insParent == inst.Segment.Base + (tplParent - first);
                checkedNodes++;
            }
        }
        Check("load 基址回填往返: 模板区间起点 ≠ 1 且实例基址 ≠ 模板起点，"
            + checkedNodes + " 条父指针经「冻结相对化 → 装载回填」往返后逐位还原"
            + "（M1-20b 那条「M1 是数值恒等」的存活变异自此销账）", ok && checkedNodes >= 30);
    }

    // ── ④ arm-not-mount / Pool / fuzz ──────────────────────────────────────

    /// <summary>
    /// arm-not-mount：**实例化只装填**。装完之后 CONT 一条没解、纹理一张没装、实例仍是 Armed；
    /// 树上却已经有节点、有几何——命中、布局、事件全都能跑。第一次 Extract 问到它才绑。
    /// 「构造到首用之间的编辑天然被吸收」也在这里验：装填后改一个节点的位置，
    /// 首用绑定照样成功（身份链不含可编辑状态）。
    /// </summary>
    private static void ArmNotMount(string dir)
    {
        CompileResult? r = LoadCompile(dir, "PullToRefresh", out ShapedPackage s);
        if (r == null) { Check("load arm-not-mount", false); return; }
        var src = new NullAssetSource(1000u);
        FgbPackage? p = LoadPkg(r, out _, textures: src);
        if (p == null) { Check("load arm-not-mount: 装载", false); return; }

        int comp = 0;
        for (int i = 0; i < p.ComponentCount; i++)
            if (!p.Component(i).HasTextLeaves && !p.Component(i).HasNested && p.Component(i).LeafCount > 0) comp = i;

        LoadHost h = NewLoadHost(p, pipeline: true);
        FgbInstance inst = h.Inst.Instantiate(comp);

        // 装填之后、首用之前：绑定的三样东西一样都没发生。
        bool armed = inst.State == FgbArmState.Armed
            && !p.ContentDecoded && p.TexturesAcquired == 0 && src.AcquiredCount == 0
            && h.Inst.RealizedCount == 0
            // 但树是真的：节点活着、几何在、localId 可寻址。
            && h.Table.IsAlive(inst.Root)
            && h.Table.ChildCount(inst.Root) > 0
            && h.Table.GetResolved(inst.Root).W > 0f;

        // 中间编辑（arm 承诺要吸收的那一类）
        NodeHandle victim = inst.ChildByLocalId(h.Table, 1);
        h.Table.SetPosition(victim, h.Table.GetPosition(victim).x + 3f, h.Table.GetPosition(victim).y);

        h.Kernel.Invalidation.Mark(h.Table.Root, Ch.Structure, InvalidateReason.UserWrite);
        LoadTick(h);

        bool realized = inst.State == FgbArmState.Realized
            && p.ContentDecoded && p.TexturesAcquired > 0 && src.AcquiredCount > 0
            && h.Inst.RealizedCount == 1 && h.Inst.InactiveCount == 0
            && h.Stream!.QuadCount > 0;

        Check("load arm-not-mount: 装填后 CONT 未解 / 纹理未装 / 实例 Armed，但树已可用；"
            + "首次 Extract 才绑（Realized + " + p.TexturesAcquired + " 张纹理 + "
            + h.Stream!.QuadCount + " 个实例），中间编辑被吸收", armed && realized);
    }

    /// <summary>
    /// arm 的失败面（运行期断言 14）：首用时模板身份链对不上 ⇒ 该实例 **inactive + 计数**，
    /// 一个实例也不发射——「不画」而不是「画错」。
    /// 语料：装填之后把块内一个节点 Destroy 掉（块不再是完整实例）。
    /// </summary>
    private static void ArmChainInactiveNotWrongPicture(string dir)
    {
        CompileResult? r = LoadCompile(dir, "ScrollPane", out _);
        if (r == null) { Check("load arm 失败面", false); return; }
        FgbPackage? p = LoadPkg(r, out _, textures: new IdentityAssetSource());
        if (p == null) { Check("load arm 失败面: 装载", false); return; }

        int comp = -1;
        for (int i = 0; i < p.ComponentCount; i++)
            if (!p.Component(i).HasNested && p.Component(i).LeafCount > 0 && p.Component(i).NodeCount > 2) comp = i;
        if (comp < 0) { Check("load arm 失败面: 语料在场", false); return; }

        // 对照组：不动它，正常发射。
        LoadHost ok1 = NewLoadHost(p, pipeline: true);
        ok1.Inst.Instantiate(comp);
        ok1.Kernel.Invalidation.Mark(ok1.Table.Root, Ch.Structure, InvalidateReason.UserWrite);
        LoadTick(ok1);
        int normalQuads = ok1.Stream!.QuadCount;

        // 实验组：装填之后把块内一个节点摘掉再画。
        LoadHost h = NewLoadHost(p, pipeline: true);
        FgbInstance inst = h.Inst.Instantiate(comp);
        NodeHandle leaf = inst.ChildByLocalId(h.Table, (ushort)(inst.Segment.Count - 1));
        h.Table.Destroy(leaf);
        h.Kernel.Invalidation.Mark(h.Table.Root, Ch.Structure, InvalidateReason.UserWrite);
        LoadTick(h);

        Check("load arm 失败面: 身份链不符 ⇒ 实例 inactive + 计数 + **零发射**（对照组 "
            + normalQuads + " 个实例），不画错",
            normalQuads > 0 && inst.State == FgbArmState.Inactive
            && h.Inst.InactiveCount == 1 && h.Inst.RealizedCount == 0
            && h.Stream!.QuadCount == 0);
    }

    /// <summary>
    /// Pool 雏形：归还 → P9 → 再租，段回到同一个 bin，而**列全部重新 memcpy**。
    /// 「复用后有残留」在结构上不可能，用例把它钉住：租来之后先把每一根可写列都改花，
    /// 归还、过 P9、再租——21 根列必须与模板逐位同，实例块字节全零，resolved 槽重新分齐。
    /// </summary>
    private static void PoolReuseNoResidue(string dir)
    {
        CompileResult? r = LoadCompile(dir, "PullToRefresh", out ShapedPackage s);
        if (r == null) { Check("load Pool", false); return; }
        FgbPackage? p = LoadPkg(r, out _, textures: new IdentityAssetSource());
        if (p == null) { Check("load Pool: 装载", false); return; }

        int comp = 0;
        for (int i = 0; i < p.ComponentCount; i++) if (p.Component(i).NodeCount > p.Component(comp).NodeCount) comp = i;

        LoadHost h = NewLoadHost(p, pipeline: false);
        var pool = new FgbInstancePool(h.Inst);

        FgbInstance a = pool.Rent(comp);
        uint baseA = a.Segment.Base;
        // 把状态改花：位置/尺寸/颜色/可见/内容引用/实例块字节，能写的都写。
        for (ushort l = 0; l < (ushort)a.Segment.Count; l++)
        {
            NodeHandle n = a.ChildByLocalId(h.Table, l);
            h.Table.SetPosition(n, 111f + l, 222f + l);
            h.Table.SetSize(n, 33f + l, 44f + l);
            h.Table.SetAlpha(n, 0.25f);
            h.Table.SetGrayed(n, true);
        }
        for (int i = 0; i < a.Block.Length; i++) a.Block[i] = 0xAB;

        pool.Return(a);
        bool dead = !h.Table.IsAlive(a.Root);
        h.Table.EndFrame();                       // P9：换代 + 回 slab（池不许绕过这道）

        FgbInstance b = pool.Rent(comp);
        bool reused = b.Segment.Base == baseA && pool.SlabReuses == 1;
        bool clean = BlockMatchesTemplate(h, p, s.Components[comp], b, false, out string why);
        if (!clean) Console.WriteLine("     池复用后残留：" + why);
        bool blockClean = true;
        for (int i = 0; i < b.Block.Length; i++) blockClean &= b.Block[i] == 0;
        bool slots = true;
        for (ushort l = 0; l < (ushort)b.Segment.Count; l++)
        {
            NodeHandle tn = s.Components[comp].Locals.HandleOf(l);
            NodeHandle bn = b.ChildByLocalId(h.Table, l);
            slots &= (s.Components[comp].Table.ResolvedRef(tn) != 0u) == (h.Table.ResolvedRef(bn) != 0u);
        }
        Check("load Pool: 归还（标记即死）→ P9 → 再租，段回同一 bin（复用 " + pool.SlabReuses
            + "）且 21 根列与模板逐位同、实例块归零、resolved 写集重新分齐——复用后零残留",
            dead && reused && clean && blockClean && slots && pool.Rents == 2 && pool.Returns == 1);
    }

    /// <summary>
    /// **fuzz 常设门**（M1-19 的雏形转正）：2048 轮、seed 固定，语料从容器四类扩到
    /// 十一种段内记录（COMP/PLAN/PTCH/CONT/LOCL/CNST/STRT/LEAF/SEGS/DEPS/NODE）。
    /// 段内变异一律**补钉 selfHash** —— 否则一切都停在门 3，段内那十几道门永远拿不到语料。
    ///
    /// 断言就一句：**要么干净拒载，要么正常装载**。
    ///  · 拒载 ⇒ package == null 且门号 ≠ None（「拒了但说不出哪一门」也是违例）；
    ///  · 装载 ⇒ 全部视图可走到底（COMP/PLAN/STRT/CONT/CNST），并且**可实例化**
    ///    （每 8 轮真建一次树 + 跑一帧）——「装得上但一实例化就越界」不算装载成功。
    /// 绝不越界、绝不半载、绝不 panic。
    /// </summary>
    private static void LoadFuzzGate(string dir)
    {
        CompileResult? r = LoadCompile(dir, "TurnPage", out _);
        if (r == null) { Check("load fuzz 常设门", false); return; }
        byte[] baseBlob = r.Blob.ToArray();

        uint[] sections =
        {
            AbiLayout.FgbSectionComp, AbiLayout.FgbSectionPlan, AbiLayout.FgbSectionPtch,
            AbiLayout.FgbSectionCont, AbiLayout.FgbSectionLocl, AbiLayout.FgbSectionCnst,
            AbiLayout.FgbSectionStrt, AbiLayout.FgbSectionLeaf, AbiLayout.FgbSectionSegs,
            AbiLayout.FgbSectionDeps, AbiLayout.FgbSectionNode,
        };

        var rng = new Random(0x22F6);     // seed 固定：红了可复现
        const int Rounds = 2048;
        int accepted = 0, rejected = 0, violations = 0, instantiated = 0, sectionHits = 0;
        string? firstViolation = null;

        for (int i = 0; i < Rounds; i++)
        {
            byte[] mut;
            bool rehash = false;
            switch (i % 8)
            {
                case 0:
                    mut = baseBlob.AsSpan(0, rng.Next(0, baseBlob.Length + 1)).ToArray();
                    break;
                case 1:
                    mut = (byte[])baseBlob.Clone();
                    mut[rng.Next(mut.Length)] ^= (byte)(1 << rng.Next(8));
                    break;
                case 2:
                    mut = (byte[])baseBlob.Clone();
                    int dirZone = Abi.FgbHeaderSize + 14 * Abi.FgbSectionDirEntrySize;
                    int pos = rng.Next(Math.Min(dirZone, mut.Length) - 4);
                    for (int b = 0; b < 4; b++) mut[pos + b] = (byte)rng.Next(256);
                    break;
                case 3:
                    if (rng.Next(2) == 0)
                    {
                        int extra = 1 + rng.Next(32);
                        mut = new byte[baseBlob.Length + extra];
                        baseBlob.CopyTo(mut, 0);
                        for (int b = 0; b < extra; b++) mut[baseBlob.Length + b] = (byte)rng.Next(256);
                    }
                    else
                    {
                        mut = (byte[])baseBlob.Clone();
                        int at = rng.Next(mut.Length - 8);
                        for (int b = 0; b < 8; b++) mut[at + b] = (byte)rng.Next(256);
                    }
                    break;
                default:
                    // 段内记录变异：随机段 → 随机偏移 → 随机字节（半数写整字，制造夸张计数）。
                    mut = (byte[])baseBlob.Clone();
                    uint fourcc = sections[rng.Next(sections.Length)];
                    if (LoadSectionRange(mut, fourcc, out int off, out int len) && len > 0)
                    {
                        int at = off + rng.Next(len);
                        if (rng.Next(2) == 0 || at + 4 > off + len) mut[at] = (byte)rng.Next(256);
                        else BinaryPrimitives.WriteUInt32LittleEndian(mut.AsSpan(at), (uint)rng.Next(int.MinValue, int.MaxValue));
                        sectionHits++;
                    }
                    rehash = true;
                    break;
            }
            if (rehash && mut.Length >= Abi.FgbHeaderSize) LoadRehash(mut);

            try
            {
                var opts = new FgbLoadOptions(null, -1, -1, new IdentityAssetSource(), null, i % 2 == 0, 0);
                if (!FgbPackage.TryLoad(mut, in opts, out FgbPackage? pkg, out FgbLoadReport rep))
                {
                    rejected++;
                    if (pkg != null) { violations++; firstViolation ??= $"[{i}] 拒载却给了包（半载）"; }
                    if (rep.RejectedBy == FgbGate.None) { violations++; firstViolation ??= $"[{i}] 拒载但无门号"; }
                    continue;
                }
                accepted++;
                if (pkg == null) { violations++; firstViolation ??= $"[{i}] 装载成功却没有包"; continue; }

                // 「正常装载」必须名副其实：全部视图走到底。
                ulong sink = 0;
                for (int ci = 0; ci < pkg.ComponentCount; ci++)
                {
                    FgbComponentDef d = pkg.Component(ci);
                    sink ^= (ulong)d.NodeCount + d.NameHash;
                    sink ^= (ulong)pkg.StringAt(d.NameStr).Length;
                    ConstraintGraph? g = pkg.ConstraintGraphOf(ci);
                    if (g != null) sink ^= (ulong)g.Ops.Length;
                }
                for (int si = 0; si < pkg.PlanStepCount; si++) sink ^= (ulong)pkg.PlanStep(si).CompIndex;
                pkg.EnsureContentDecoded();
                for (uint cid = 0; cid < (uint)pkg.ContentCount; cid++) sink ^= (ulong)pkg.ContentAt(cid).Kind;

                // 每 8 轮真实例化一次：「装得上但一 memcpy 就越界」不算装载成功。
                if (i % 8 == 4 && pkg.ComponentCount > 0)
                {
                    LoadHost h = NewLoadHost(pkg, pipeline: true);
                    h.Inst.Instantiate(rng.Next(pkg.ComponentCount));
                    h.Kernel.Invalidation.Mark(h.Table.Root, Ch.Structure, InvalidateReason.UserWrite);
                    LoadTick(h);
                    sink ^= (ulong)h.Stream!.QuadCount;
                    instantiated++;
                }
                if (sink == 0xFFFFFFFFFFFFFFFFUL) Console.WriteLine("(不可能：防死码消除)");
            }
            catch (Exception ex)
            {
                violations++;
                firstViolation ??= $"[{i}] {ex.GetType().Name}: {ex.Message}";
            }
        }

        if (firstViolation != null) Console.WriteLine("     首违例 " + firstViolation);
        Check("load fuzz 常设门: " + Rounds + " 变体零违例（要么干净拒载要么全视图可走 + 可实例化，"
            + "绝不越界/半载/panic；收 " + accepted + " 拒 " + rejected + " 实例化 " + instantiated + " 次）",
            violations == 0);
        Check("load fuzz 常设门: 语料两侧都有覆盖且段内记录被真的打到（" + sectionHits + " 次段内变异）",
            accepted > 0 && rejected > 0 && sectionHits > 400);
    }
}
