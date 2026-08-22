using FairyNext.AbiGen;
using FairyNext.Compiler;
using FairyNext.Compiler.Fui;
using FairyNext.Compiler.Shape;
using FairyNext.Core;
using FairyNext.Core.Layout;
using FairyNext.Core.Rendering;
using FairyNext.Core.Text;
using FairyNext.Numerics;

namespace FairyNext.Tests;

/// <summary>
/// M1-20a FgbCompiler 前半（Program 的 partial 分片）：.fui → 建树 → 编译期 P5 + 度量。
///
/// 对照物三源：
///  ① **编辑器授权 XML**（`~/ECS/FairyGUI-unity/UIProject/assets/…` @ 08a2d56）——建树的
///     坐标/尺寸/组归属常量抄自它（与 FuiReaderTests 同一条纪律：不拿实现验实现）；
///  ② **fork 关系语义**（Relations/RelationItem）——resize 后的期望值按 fork 公式手推
///    （offset 捕获 / percent 比例 / Ext 对边钉住 / pivotAsAnchor 的**两套定点**：源是父时
///    定点是原点、源是兄弟时定点是锚点，见 RelationItem.cs 391-431）；
///  ③ **手工建等价树**——同一组件用运行时 API 从常量重建、同款字体度量，逐位对比编译产物
///     （「编译器 = 无头运行时」的直接证据）。
///
/// 本包上线的门：**约束环拒绝 FGM101（L0，带闭合环路径）**（负例 = 字节手术把两条 Width
/// 关系接成环——真实样例包不带环）；**LateSlotAllocs 恒零升门 FGM902**（编译期布局写集在
/// 布防期定死）。后者的**边界写明**：迟到槽分配只在 `LayoutEngine.PlaceBox`（流式层）产生，
/// 而 `LinearLayout` 尚未进编译面（归 M2-07），所以它现在是**跳线**——计数由用例直接断言、
/// 门由 FGM902 兜住，但没有资产能触发它；真语料随虚拟列表进驻。杀变异记录见交付报告。
/// </summary>
public static partial class Program
{
    private static void CompilerShapeSuite()
    {
        string? root = RepoRoot.Find(AppContext.BaseDirectory);
        if (root == null) { Check("shape: 定位仓库根", false); return; }
        string dir = RepoRoot.ToAbsolute(root, FuiFixtureDir);

        // 关系翻译表与锚定（约束编译的纯函数面）
        ShapeRelationTableGolden();
        ShapeAnchorPinRuntimeSemantics();
        ShapeAnchorPinInjectionRules();
        ShapeAnchorParentWidthKeepsOrigin();
        ShapeGroupPivotColumnIsAuthored();
        ShapeAnchorPairingValidation();
        ShapePercentTranslationSemantics();

        // GGroup 归属规划（纯函数面）
        ShapeGroupPlanRules();

        // 建树：字段级对照（授权 XML 常量）
        ShapeHeaderTreeAndGroups(dir);
        ShapeVirtualListMainTree(dir);
        ShapePivotAnchorOriginConversion(dir);
        ShapeAnchoredGroupKeepsPivotColumn(dir);
        ShapeFooterContentAndDefaults(dir);
        ShapeTextParsedAndMeasured(dir);

        // relation → 约束：resize 后与 fork 公式手推值对拍
        ShapeGroupConstraintTracksResize(dir);
        ShapeItemRelationsTrackResize(dir);
        ShapeButtonChildrenBindParent(dir);

        // 门与等价性
        ShapeCycleRejectedFgm101(dir);
        ShapeAllFixturesGatesGreen(dir);
        ShapeCompiledEqualsHandBuiltRuntime(dir);
        ShapeRuntimePerturbReturnsBitIdentical(dir);
        ShapeDeterminism(dir);
        ShapeGarbageRejected();
        ShapeWithoutFontsStillShapes(dir);
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    private static byte[]? _shapeFontCache;

    private static CompileOptions ShapeOpts()
    {
        _shapeFontCache ??= SynthFontT();
        return new CompileOptions(1, 0, new[] { new CompileFont("", _shapeFontCache) });
    }

    private static ShapedPackage ShapeFixture(string dir, string name, bool fonts = true)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(dir, name + ".fui"));
        CompileOptions opts = fonts ? ShapeOpts() : new CompileOptions(1, 0);
        return FgbCompiler.Shape(bytes, in opts);
    }

    private static int _shapeTickEpoch;

    /// <summary>继续驱动一个已定形世界（时刻从大于建包期的基准起步，Clock 单调不破）。</summary>
    private static void TickShaped(ShapedComponent sc, int times = 1)
    {
        float baseNow = 1f + _shapeTickEpoch * 0.5f;
        _shapeTickEpoch += times + 1;
        for (int i = 0; i < times; i++)
        {
            var t = new FrameTime(1f / 60f, 1f / 60f, baseNow + (i + 1) / 60.0, baseNow + (i + 1) / 60.0);
            sc.Kernel.Tick(in t);
        }
    }

    private static bool GeomIs(NodeTable t, NodeHandle h, float x, float y, float w, float hh)
    {
        ResolvedGeom g = t.GetResolved(h);
        return FloatBits(g.X, x) && FloatBits(g.Y, y) && FloatBits(g.W, w) && FloatBits(g.H, hh);
    }

    private static bool FloatBits(float a, float b) =>
        BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    private static NodeHandle ByEditorId(ShapedComponent sc, string editorId) =>
        sc.Locals.TryHandleOf(editorId, out NodeHandle h) ? h : NodeHandle.None;

    // ── 关系翻译表 golden ────────────────────────────────────────────────────

    private static void ShapeRelationTableGolden()
    {
        // (类型, 期望翻译)。期望值按 fork RelationItem 各分支手推：dst 标量 ← src 标量。
        static string Fmt(FuiRelationType t)
        {
            Span<RelationOp> ops = stackalloc RelationOp[4];
            int n = RelationTable.Translate(t, ops);
            var parts = new string[n];
            for (int i = 0; i < n; i++)
            {
                RelationOp o = ops[i];
                parts[i] = (o.IsPin ? "P" : "F") + o.Axis + "." + o.DstEdge + (o.IsPin ? "" : "<" + o.SrcEdge);
            }
            return string.Join("+", parts);
        }

        bool ok =
            Fmt(FuiRelationType.Left_Left) == "FX.Min<Min"
            && Fmt(FuiRelationType.Left_Center) == "FX.Min<Center"
            && Fmt(FuiRelationType.Left_Right) == "FX.Min<Max"
            && Fmt(FuiRelationType.Center_Center) == "FX.Center<Center"
            && Fmt(FuiRelationType.Right_Left) == "FX.Max<Min"
            && Fmt(FuiRelationType.Right_Center) == "FX.Max<Center"
            && Fmt(FuiRelationType.Right_Right) == "FX.Max<Max"
            && Fmt(FuiRelationType.Top_Top) == "FY.Min<Min"
            && Fmt(FuiRelationType.Top_Middle) == "FY.Min<Center"
            && Fmt(FuiRelationType.Top_Bottom) == "FY.Min<Max"
            && Fmt(FuiRelationType.Middle_Middle) == "FY.Center<Center"
            && Fmt(FuiRelationType.Bottom_Top) == "FY.Max<Min"
            && Fmt(FuiRelationType.Bottom_Middle) == "FY.Max<Center"
            && Fmt(FuiRelationType.Bottom_Bottom) == "FY.Max<Max"
            && Fmt(FuiRelationType.Width) == "FX.Size<Size"
            && Fmt(FuiRelationType.Height) == "FY.Size<Size"
            && Fmt(FuiRelationType.Size) == "FX.Size<Size+FY.Size<Size"
            && Fmt(FuiRelationType.LeftExt_Left) == "FX.Min<Min+PX.Max"
            && Fmt(FuiRelationType.LeftExt_Right) == "FX.Min<Max+PX.Max"
            && Fmt(FuiRelationType.RightExt_Left) == "FX.Max<Min+PX.Min"
            && Fmt(FuiRelationType.RightExt_Right) == "FX.Max<Max+PX.Min"
            && Fmt(FuiRelationType.TopExt_Top) == "FY.Min<Min+PY.Max"
            && Fmt(FuiRelationType.TopExt_Bottom) == "FY.Min<Max+PY.Max"
            && Fmt(FuiRelationType.BottomExt_Top) == "FY.Max<Min+PY.Min"
            && Fmt(FuiRelationType.BottomExt_Bottom) == "FY.Max<Max+PY.Min";
        Check("shape 关系表: 24+Size 全类型 → EdgeFollow 翻译 golden（fork RelationItem 语义手推）", ok);
    }

    // ── pivotAsAnchor：锚定算子的运行时语义与注入规则 ────────────────────────

    /// <summary>
    /// Follow(Size)+PinAnchor 配对的**求值代数**（与编译器的注入判据无关——判据见
    /// <see cref="ShapeAnchorPinInjectionRules"/>）：尺寸变、锚点是定点，对应 fork 里
    /// <c>_owner.width = …</c> ⇒ <c>SetSize</c> 的 pivotAsAnchor 分支只 HandlePositionChanged、
    /// <c>_x</c> 不动的那一支。
    /// </summary>
    private static void ShapeAnchorPinRuntimeSemantics()
    {
        var f = new LayoutFixture();
        f.Table.SetSize(f.Table.Root, 400f, 100f);
        NodeHandle child = f.Box(30f, 20f, 100f, 50f);
        f.Table.SetPivot(child, 0.5f, 0f);

        var b = new ConstraintGraphBuilder(2);
        b.Follow(1, EdgeSel.Size, 0, EdgeSel.Size, LayoutAxis.X);   // width 差值跟随容器
        b.PinAnchor(1, LayoutAxis.X);                               // 锚点方程：pos + 0.5×size = 捕获锚点
        ConstraintGraphResult r = b.Seal();
        if (!r.Ok) { Check("shape 锚定: 配对封图", false); return; }
        f.Layout.Arm(r.Graph!, stackalloc NodeHandle[] { f.Table.Root, child });
        f.Tick();
        // 布防初解：size = 400 + (100−400) = 100；锚 = 30 + 0.5×100 = 80 ⇒ x = 80 − 50 = 30。
        Check("shape 锚定: 布防初解为不动点（authored 即解）", GeomIs(f.Table, child, 30f, 20f, 100f, 50f));

        f.Table.SetSize(f.Table.Root, 500f, 100f);
        f.Tick();
        // 锚点是定点 ⇒ width 200、x = 80 − 0.5×200 = −20（y/h 不动）。
        Check("shape 锚定: resize 保锚点（width +100 ⇒ x 回退 pivot×Δ）",
            GeomIs(f.Table, child, -20f, 20f, 200f, 50f));

        f.Table.SetSize(f.Table.Root, 400f, 100f);
        f.Tick();
        Check("shape 锚定: 回程逐位复原（纯函数求值，无 fork 式 delta 累积漂移）",
            GeomIs(f.Table, child, 30f, 20f, 100f, 50f) && f.GatesGreen);
    }

    private static FuiRelationSide[] Sides(params FuiRelationType[] types)
    {
        var sides = new FuiRelationSide[types.Length];
        for (int i = 0; i < types.Length; i++) sides[i] = new FuiRelationSide(types[i], false);
        return sides;
    }

    /// <summary>
    /// 注入判据（fork 的**两套定点**，RelationItem.cs 391-431 @ oracle 08a2d56）：
    /// 单 Size 算子 × 锚点 pivot × **源是兄弟** ⇒ 注入锚定（fork 走 width setter，`_x` 不动 = 锚点定）；
    /// **源是组件根**（relation `target=""`）⇒ 不注（fork 显式 `tmp=xMin … xMin=tmp` = 原点定）。
    /// 位置已被约束的轴、pivot=0 的轴、非锚点节点一律不注。
    /// </summary>
    private static void ShapeAnchorPinInjectionRules()
    {
        // locals：0 = 组件根，1 = 锚点节点（pivot=(1,0)），2 = 兄弟。
        var anchored = new[] { false, true, false };
        var rig = new PivotRig(new[] { 0f, 1f, 0f }, new[] { 0f, 0f, 0f });

        ConstraintCompiler a = new ConstraintCompiler(3);
        var d1 = new CompileDiagnostics();
        a.AddRelation(1, 2, Sides(FuiRelationType.Width), true, "t", d1);        // 源 = 兄弟
        int injA = a.InjectAnchorPins(anchored, rig.Table, rig.Nodes);
        ConstraintGraphResult ra = a.Seal();
        bool pairOk = false;
        if (ra.Ok)
        {
            ConstraintOp[] ops = ra.Graph!.Ops;
            for (int i = 0; i < ops.Length; i++)
                if (ops[i].PivotCorrect && ops[i].Next != 0) pairOk = true;
        }
        Check("shape 注入: 单 Width 算子 × 锚点 pivot × 源是兄弟 ⇒ 注入 1 条锚定并与之配对",
            injA == 1 && ra.Ok && pairOk);

        // fork 对拍的关键分支：源是组件根（fork 的 _target == _owner.parent）⇒ 定点是原点，不注。
        ConstraintCompiler p = new ConstraintCompiler(3);
        var dp = new CompileDiagnostics();
        p.AddRelation(1, 0, Sides(FuiRelationType.Width), true, "t", dp);
        int injP = p.InjectAnchorPins(anchored, rig.Table, rig.Nodes);

        ConstraintCompiler b = new ConstraintCompiler(3);
        var d2 = new CompileDiagnostics();
        b.AddRelation(1, 2, Sides(FuiRelationType.RightExt_Right), true, "t", d2);   // Ext：位置+尺寸已定
        int injB = b.InjectAnchorPins(anchored, rig.Table, rig.Nodes);

        ConstraintCompiler c = new ConstraintCompiler(3);
        var d3 = new CompileDiagnostics();
        c.AddRelation(1, 2, Sides(FuiRelationType.Left_Left), true, "t", d3);        // 只动位置
        int injC = c.InjectAnchorPins(anchored, rig.Table, rig.Nodes);

        ConstraintCompiler e = new ConstraintCompiler(3);
        var d4 = new CompileDiagnostics();
        e.AddRelation(1, 2, Sides(FuiRelationType.Height), true, "t", d4);           // Y 轴 pivot=0
        int injE = e.InjectAnchorPins(anchored, rig.Table, rig.Nodes);

        ConstraintCompiler g = new ConstraintCompiler(3);
        var d5 = new CompileDiagnostics();
        g.AddRelation(2, 1, Sides(FuiRelationType.Width), true, "t", d5);            // 非锚点节点
        int injG = g.InjectAnchorPins(anchored, rig.Table, rig.Nodes);

        Check("shape 注入: 源是组件根 / Ext 对 / 纯位置 / pivot=0 轴 / 非锚点节点 一律不注",
            injP == 0 && injB == 0 && injC == 0 && injE == 0 && injG == 0);

        // 注入后的端到端：pivot=(1,0) 右锚 + 对兄弟的 Width 差值。fork：`_x`（锚点）不动。
        var f = new LayoutFixture();
        f.Table.SetSize(f.Table.Root, 300f, 80f);
        NodeHandle sib = f.Box(0f, 50f, 50f, 20f);
        NodeHandle child = f.Box(200f, 10f, 50f, 30f);
        f.Table.SetPivot(child, 1f, 0f);
        var cc = new ConstraintCompiler(3);
        var d6 = new CompileDiagnostics();
        cc.AddRelation(2, 1, Sides(FuiRelationType.Width), true, "t", d6);
        var rig2 = new PivotRig(new[] { 0f, 0f, 1f }, new[] { 0f, 0f, 0f });
        int inj2 = cc.InjectAnchorPins(new[] { false, false, true }, rig2.Table, rig2.Nodes);
        ConstraintGraphResult rr = cc.Seal();
        if (!rr.Ok || inj2 != 1) { Check("shape 注入: 端到端封图", false); return; }
        f.Layout.Arm(rr.Graph!, stackalloc NodeHandle[] { f.Table.Root, sib, child });
        f.Tick();
        f.Table.SetSize(sib, 150f, 20f);
        f.Tick();
        // 锚 = 200+50 = 250；width = 50+100 = 150 ⇒ x = 250 − 150 = 100（fork：`_x` 不动、xMin 移）。
        Check("shape 注入: 右锚随兄弟 resize 端到端（fork `_x` 定 ⇒ origin = 锚点 − pivot×width）",
            GeomIs(f.Table, child, 100f, 10f, 150f, 30f) && f.GatesGreen);
    }

    /// <summary>
    /// 注入判据的 pivot 只有一个来源：<see cref="NodeTable"/> 的 pivot 列——与求值现场
    /// （<c>LayoutEngine.PivotOf</c>）同一列，注入系数与求值系数不可能分叉。本夹具把
    /// 「局部 id → 句柄 + pivot 已写好」做成一句话。
    /// </summary>
    private sealed class PivotRig
    {
        internal readonly NodeTable Table = new NodeTable(tree: 11);
        internal readonly NodeHandle[] Nodes;

        internal PivotRig(float[] px, float[] py)
        {
            Nodes = new NodeHandle[px.Length];
            Nodes[0] = Table.Root;
            for (int i = 1; i < px.Length; i++)
            {
                Nodes[i] = Table.CreateNode(NodeType.Component);
                Table.AddChild(Table.Root, Nodes[i]);
            }
            for (int i = 0; i < px.Length; i++)
                if (px[i] != 0f || py[i] != 0f) Table.SetPivot(Nodes[i], px[i], py[i]);
        }
    }

    /// <summary>
    /// fork 对拍：**锚点节点 + 对父的 Width 关系 ⇒ 原点是定点**（fork RelationItem.cs 396-407
    /// 的 `tmp = _owner.xMin; SetSize(…, ignorePivot:true); _owner.xMin = tmp;`——那三行的存在
    /// 就是为了把 SetSize 的保锚点撤销掉）。这是编译器**不注**锚定的那一支，等价于裸 Size 算子
    /// 的求值（<c>npos = pos</c>）。
    /// </summary>
    private static void ShapeAnchorParentWidthKeepsOrigin()
    {
        var f = new LayoutFixture();
        f.Table.SetSize(f.Table.Root, 300f, 80f);
        NodeHandle child = f.Box(200f, 10f, 50f, 30f);
        f.Table.SetPivot(child, 1f, 0f);

        var cc = new ConstraintCompiler(2);
        var diag = new CompileDiagnostics();
        cc.AddRelation(1, 0, Sides(FuiRelationType.Width), true, "t", diag);
        var rig = new PivotRig(new[] { 0f, 1f }, new[] { 0f, 0f });
        int inj = cc.InjectAnchorPins(new[] { false, true }, rig.Table, rig.Nodes);
        ConstraintGraphResult r = cc.Seal();
        if (!r.Ok) { Check("shape 父 Width: 封图", false); return; }
        f.Layout.Arm(r.Graph!, stackalloc NodeHandle[] { f.Table.Root, child });
        f.Tick();
        f.Table.SetSize(f.Table.Root, 400f, 80f);
        f.Tick();
        // fork：xMin 被复原 ⇒ x 不动 200，width = 50+100 = 150（锚点 250→350 是被移动的那一头）。
        Check("shape 父 Width: 锚点节点对父的 Width 关系保**原点**（fork 显式撤销保锚点，故不注锚定）",
            inj == 0 && GeomIs(f.Table, child, 200f, 10f, 150f, 30f) && f.GatesGreen);
    }

    /// <summary>
    /// pivot 列对 Group 也是 authored 真值（不变量 9 的组纯度只管 scale/rotation/contentRef）：
    /// 组上写 pivot 合法、且在恒等基下对 LocalMatrix 零影响——原点换算与锚定系数因此读同一份。
    /// </summary>
    private static void ShapeGroupPivotColumnIsAuthored()
    {
        var table = new NodeTable(tree: 12);
        NodeHandle g = table.CreateNode(NodeType.Group);
        table.AddChild(table.Root, g);
        table.SetSize(g, 200f, 100f);
        table.SetPosition(g, 10f, 20f);
        Affine2D before = table.LocalMatrix(g);
        table.SetPivot(g, 0.5f, 1f);
        Affine2D after = table.LocalMatrix(g);
        Vector2 p = table.GetPivot(g);
        Check("shape 组 pivot: 组上 pivot 可写、列存 authored 真值、恒等基下 LocalMatrix 逐位不变",
            FloatBits(p.x, 0.5f) && FloatBits(p.y, 1f)
            && FloatBits(before.m00, after.m00) && FloatBits(before.m01, after.m01)
            && FloatBits(before.m10, after.m10) && FloatBits(before.m11, after.m11)
            && FloatBits(before.tx, after.tx) && FloatBits(before.ty, after.ty));
    }

    private static void ShapeAnchorPairingValidation()
    {
        // Center+锚定：pivot=0.5 时两行同为 (1,0.5) ⇒ det=0——必须在 Seal 拒绝，不是求值 NaN。
        var b = new ConstraintGraphBuilder(2);
        b.Follow(1, EdgeSel.Center, 0, EdgeSel.Center, LayoutAxis.X);
        b.PinAnchor(1, LayoutAxis.X);
        ConstraintGraphResult bad = b.Seal();

        // Min+锚定：撞既有的「同边双写」门（锚定的 DstEdge 布局位占 Min）——两门谁先响都算拒。
        var m = new ConstraintGraphBuilder(2);
        m.Follow(1, EdgeSel.Min, 0, EdgeSel.Min, LayoutAxis.X);
        m.PinAnchor(1, LayoutAxis.X);
        ConstraintGraphResult minBad = m.Seal();

        var c = new ConstraintGraphBuilder(2);
        c.PinAnchor(1, LayoutAxis.Y);
        ConstraintGraphResult solo = c.Seal();

        Check("shape 锚定: 与位置算子配对被 Seal 拒绝（行列式可退化），单独声明合法",
            !bad.Ok && bad.Error.Contains("pivotCorrect") && !minBad.Ok && solo.Ok);
    }

    /// <summary>percent 经编译器翻译臂到达运行时：比例保持（fork percent 分支代数等价，M1-16 已证）。</summary>
    private static void ShapePercentTranslationSemantics()
    {
        var f = new LayoutFixture();
        f.Table.SetSize(f.Table.Root, 400f, 100f);
        NodeHandle child = f.Box(100f, 0f, 200f, 40f);

        var cc = new ConstraintCompiler(2);
        var diag = new CompileDiagnostics();
        cc.AddRelation(1, 0, new[]
        {
            new FuiRelationSide(FuiRelationType.Left_Left, true),
            new FuiRelationSide(FuiRelationType.Height, true),
        }, true, "t", diag);
        ConstraintGraphResult r = cc.Seal();
        if (!r.Ok) { Check("shape percent: 封图", false); return; }
        f.Layout.Arm(r.Graph!, stackalloc NodeHandle[] { f.Table.Root, child });
        f.Tick();
        f.Table.SetSize(f.Table.Root, 600f, 300f);
        f.Tick();
        // ratio_x = 100/400 = 0.25 ⇒ x = 0.25×600 = 150；ratio_h = 40/100 = 0.4 ⇒ h = 120。
        Check("shape percent: Left_Left% 保比例、Height% 保尺寸比（fork percent 公式）",
            GeomIs(f.Table, child, 150f, 0f, 200f, 120f) && diag.Items.Count == 0 && f.GatesGreen);
    }

    // ── GGroup 归属规划 ─────────────────────────────────────────────────────

    private static void ShapeGroupPlanRules()
    {
        // 孩子 0,1 → 组 2；孩子 3 → 组 5；4 是散点：连续 ✓
        var d1 = new CompileDiagnostics();
        int[] p1 = GroupPlan.Plan(new[] { 2, 2, -1, 5, -1, -1 }, new[] { false, false, true, false, false, true }, "t", d1);
        bool contiguous = p1[0] == 2 && p1[1] == 2 && p1[2] == -1 && p1[3] == 5 && p1[4] == -1
            && d1.Items.Count == 0;

        // 组员 0 与 2 夹着非组员 1 ⇒ FGM201（归属保留）
        var d2 = new CompileDiagnostics();
        int[] p2 = GroupPlan.Plan(new[] { 3, -1, 3, -1 }, new[] { false, false, false, true }, "t", d2);
        bool nonContig = p2[0] == 3 && p2[2] == 3 && d2.Has(FgmCodes.GroupNotContiguous);

        // 嵌套组：0,1 → 内组 2；2 → 外组 4；3 → 外组 4：外组员 span [2,4) 内含内组员 ⇒ 无诊断
        var d3 = new CompileDiagnostics();
        int[] p3 = GroupPlan.Plan(new[] { 2, 2, 4, 4, -1 }, new[] { false, false, true, false, true }, "t", d3);
        bool nested = p3[0] == 2 && p3[2] == 4 && d3.Items.Count == 0;

        // 非法归属：指向非组 / 越界 / 自己 / 组链成环 ⇒ FGM203 + 按无组处理
        var d4 = new CompileDiagnostics();
        int[] p4 = GroupPlan.Plan(new[] { 1, 9, 2, -1 }, new[] { false, false, true, true }, "t", d4);
        var d5 = new CompileDiagnostics();
        int[] p5 = GroupPlan.Plan(new[] { 1, 0, -1 }, new[] { true, true, false }, "t", d5);
        bool invalid = p4[0] == -1 && p4[1] == -1 && p4[2] == -1 && p4[3] == -1
            && d4.CountOf(FgmCodes.GroupMembershipInvalid) == 3
            && (p5[0] == -1 || p5[1] == -1) && d5.Has(FgmCodes.GroupMembershipInvalid);

        Check("shape 组规划: 连续/夹心 FGM201/嵌套组/非法归属 FGM203 四形态", contiguous && nonContig && nested && invalid);
    }

    // ── 建树字段级对照（授权 XML 常量）──────────────────────────────────────

    /// <summary>PullToRefresh/Header.xml：两组真节点化、组员坐标换组空间、localId 映射。</summary>
    private static void ShapeHeaderTreeAndGroups(string dir)
    {
        ShapedPackage s = ShapeFixture(dir, "PullToRefresh");
        if (!s.Success || !s.TryGetComponent("n3qdr", out ShapedComponent? sc))
        { Check("shape Header: 定形成功", false); return; }

        NodeTable t = sc.Table;
        // 根的直接孩子只剩两个组（Header.xml：n2 组收 n0/n1/n3，n6 组收 n4/n5）
        NodeHandle g2 = t.FirstChild(sc.Root);
        NodeHandle g6 = t.NextSibling(g2);
        bool topo = !g2.IsNone && !g6.IsNone && t.NextSibling(g6).IsNone
            && t.TypeOf(g2) == NodeType.Group && t.TypeOf(g6) == NodeType.Group
            && t.LocalIdOf(g2) == 4 && t.LocalIdOf(g6) == 7;       // 显示列表下标 3/6 → localId +1
        Check("shape Header: 根下只剩两组真节点（GGroup 真节点化，显示序 = 组条目位）", topo);

        // <group id="n2_n3qd" xy="52,14" size="296,46"> 与三组员的组空间坐标：
        // n0 (54,20)−(52,14)=(2,6) 默认尺寸 42×34（refresh.png）；n1 (106,23)→(54,9)；n3 (52,14)→(0,0) 40×46
        bool geo = GeomIs(t, g2, 52f, 14f, 296f, 46f)
            && GeomIs(t, ByEditorId(sc, "n0_n3qd"), 2f, 6f, 42f, 34f)
            && GeomIs(t, ByEditorId(sc, "n3_n3qd"), 0f, 0f, 40f, 46f)
            && t.Parent(ByEditorId(sc, "n0_n3qd")).Equals(g2)
            && t.Parent(ByEditorId(sc, "n1_n3qd")).Equals(g2)
            && t.Parent(ByEditorId(sc, "n4_9sfl")).Equals(g6);
        Check("shape Header: 组员坐标换组空间 + 图片默认尺寸 = 条目源尺寸（Header.xml 常量）", geo);

        // localId 映射双向 + 根
        bool ids = sc.Locals.Count == 8
            && sc.Locals.TryLocalOf("n5_9sfl", out ushort l5) && l5 == 6
            && sc.Locals.EditorIdOf(6) == "n5_9sfl"
            && sc.Locals.EditorIdOf(0) == null
            && sc.Locals.HandleOf(0).Equals(sc.Root)
            && t.LocalIdOf(ByEditorId(sc, "n5_9sfl")) == 6;
        Check("shape Header: localId 映射（编辑器 id ⇄ 局部 id ⇄ 句柄，根 = 局部 0）", ids);
    }

    /// <summary>VirtualList/Main.xml：8 孩子建树、类型映射、未写 size 用条目源尺寸。</summary>
    private static void ShapeVirtualListMainTree(string dir)
    {
        ShapedPackage s = ShapeFixture(dir, "VirtualList");
        bool okAll = s.Success && s.Components.Count == 3;
        if (!okAll || !s.TryGetComponent("c8s20", out ShapedComponent? sc))
        { Check("shape VirtualList: 定形成功（3 组件）", false); return; }
        Check("shape VirtualList: 定形成功（3 组件）", true);

        NodeTable t = sc.Table;
        int kids = 0;
        for (NodeHandle c = t.FirstChild(sc.Root); !c.IsNone; c = t.NextSibling(c)) kids++;

        bool ok = kids == 8
            && GeomIs(t, sc.Root, 0f, 0f, 1136f, 640f)
            // <image id="n0" src="c8s21" xy="185,56" size="404,562"/>
            && GeomIs(t, ByEditorId(sc, "n0"), 185f, 56f, 404f, 562f)
            && t.TypeOf(ByEditorId(sc, "n0")) == NodeType.Image
            // <list id="n3" xy="197,130" size="380,473"/>
            && GeomIs(t, ByEditorId(sc, "n3"), 197f, 130f, 380f, 473f)
            && t.TypeOf(ByEditorId(sc, "n3")) == NodeType.List
            // <image id="n2" src="c8s23" xy="315,82"/>——无 size ⇒ 条目源尺寸 133×34（package.xml 4.png）
            && GeomIs(t, ByEditorId(sc, "n2"), 315f, 82f, 133f, 34f)
            // <component id="n8" src="rpolb" xy="693,300" size="183,48"/>
            && t.TypeOf(ByEditorId(sc, "n8")) == NodeType.Component
            && GeomIs(t, ByEditorId(sc, "n8"), 693f, 300f, 183f, 48f);
        Check("shape VirtualList: Main 8 孩子逐字段（类型映射 + 默认尺寸 = 条目源尺寸）", ok);
    }

    /// <summary>TurnPage/Page.xml 的 model：pivot(0.5,1) anchor=true ⇒ 原点 = 锚点 − pivot×尺寸。</summary>
    private static void ShapePivotAnchorOriginConversion(string dir)
    {
        ShapedPackage s = ShapeFixture(dir, "TurnPage");
        if (!s.Success || !s.TryGetComponent("gawe1", out ShapedComponent? sc))
        { Check("shape TurnPage: 定形成功", false); return; }

        NodeTable t = sc.Table;
        NodeHandle model = ByEditorId(sc, "n20_jva6");
        NodeHandle group = ByEditorId(sc, "n26_jva6");
        // <graph name="model" xy="152,339" pivot="0.5,1" anchor="true" size="243,266" group="n26_jva6"/>
        // 组件空间原点 = (152 − 0.5×243, 339 − 1×266) = (30.5, 73)；组 n26 @(111,29) ⇒ 组空间 (−80.5, 44)。
        bool ok = !model.IsNone && !group.IsNone
            && t.Parent(model).Equals(group)
            && GeomIs(t, group, 111f, 29f, 284f, 577f)
            && GeomIs(t, model, -80.5f, 44f, 243f, 266f)
            && FloatBits(t.GetPivot(model).x, 0.5f) && FloatBits(t.GetPivot(model).y, 1f);
        Check("shape 锚点消灭: authored x/y 是锚点 ⇒ 原点换算烘死（Page.xml model 常量），pivot 保留为旋转中心", ok);
    }

    /// <summary>
    /// pivotAsAnchor 的组形态（字节手术：把 Page 的 `model`——唯一带 `pivot`+`anchor` 的孩子——
    /// 的类型字节改成 Group，真实资产里编辑器不产这种组合）：**组节点的 pivot 列同样是 authored
    /// 真值**。fork 的 GGroup 继承 GObject 的 pivotAsAnchor，原点换算（建树期读 .fui 的 pivot）
    /// 与锚定系数（求值期读 pivot 列）必须是同一个数——列上少写一份就分叉。
    /// </summary>
    private static void ShapeAnchoredGroupKeepsPivotColumn(string dir)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(dir, "TurnPage.fui"));
        if (!FuiPackage.TryParse(bytes, out FuiPackage? pkg, out _) || pkg == null
            || !pkg.TryGetItemById("gawe1", out FuiItem? item)
            || !FuiComponent.TryParse(pkg, item, out FuiComponent? comp, out _) || comp == null)
        { Check("shape 组锚点: 手术前解析", false); return; }

        int typeByte = -1;
        foreach (FuiChild c in comp.Children)
        {
            if (c.Id != "n20_jva6") continue;
            ByteBuffer b = c.Open();
            if (!b.Seek(0, 0)) break;
            typeByte = b.bufferOffset + b.position;      // 块 0 首字节 = 对象类型
            break;
        }
        if (typeByte < 0 || bytes[typeByte] != (byte)FuiObjectType.Graph)
        { Check("shape 组锚点: 手术位点定位（model 的类型字节 = Graph）", false); return; }

        var patched = (byte[])bytes.Clone();
        patched[typeByte] = (byte)FuiObjectType.Group;

        CompileOptions opts = ShapeOpts();
        ShapedPackage s = FgbCompiler.Shape(patched, in opts);
        if (!s.Success || !s.TryGetComponent("gawe1", out ShapedComponent? sc))
        { Check("shape 组锚点: 手术后定形", false); return; }

        NodeHandle model = ByEditorId(sc, "n20_jva6");
        Vector2 piv = sc.Table.GetPivot(model);
        bool ok = !model.IsNone
            && sc.Table.TypeOf(model) == NodeType.Group
            && FloatBits(piv.x, 0.5f) && FloatBits(piv.y, 1f)      // ← 组也写 pivot 列
            && sc.Table.ContentRef(model) == 0                      // 不变量 9：组无内容
            && GeomIs(sc.Table, model, -80.5f, 44f, 243f, 266f);    // 原点换算不受类型影响
        Check("shape 组锚点: 组节点的 pivot 列也是 authored 真值（换算与锚定系数同一个数）", ok);
    }

    /// <summary>Footer：图片叶 spec（UV 由 sprite/图集像素归一）、纯矩形 Graph、Loader 有声不编。</summary>
    private static void ShapeFooterContentAndDefaults(string dir)
    {
        ShapedPackage s = ShapeFixture(dir, "PullToRefresh");
        if (!s.TryGetComponent("9sflu", out ShapedComponent? footer)
            || !s.TryGetComponent("n3qdr", out ShapedComponent? header)
            || !s.TryGetComponent("n3qdm", out ShapedComponent? item))
        { Check("shape 内容: 三组件在场", false); return; }

        // Footer n0：sprite n3qds @atlas0 [0,88 42×34]，图集 256×128 ⇒ UV = (0, 0.6875, 0.1640625, 0.953125)
        NodeHandle img = ByEditorId(footer, "n0_n3qd");
        ContentRecord rec = footer.Content.At(footer.Table.ContentRef(img));
        bool uvOk = rec.Kind == ExtractKind.Leaf
            && rec.Leaf.Texture.Value == 1u
            && FloatBits(rec.Leaf.Region.Uv.x, 0f) && FloatBits(rec.Leaf.Region.Uv.y, 88f / 128f)
            && FloatBits(rec.Leaf.Region.Uv.z, 42f / 256f) && FloatBits(rec.Leaf.Region.Uv.w, 122f / 128f)
            && FloatBits(rec.Leaf.Region.SourceWidth, 42f) && FloatBits(rec.Leaf.Region.SourceHeight, 34f)
            && rec.Leaf.BaseColor == 0xFFFFFFFFu;
        Check("shape 内容: 图片叶 UV 按 sprite/图集像素归一（atlas 256×128 手算常量）", uvOk);

        // Header n4：<graph type="rect" lineSize="0" fillColor="#ff3399ff"> ⇒ 纯色叶。
        // #AARRGGBB = (A ff, R 33, G 99, B ff) ⇒ Color32.Pack = r|g<<8|b<<16|a<<24 = 0xFFFF9933。
        NodeHandle graph = ByEditorId(header, "n4_9sfl");
        ContentRecord g = header.Content.At(header.Table.ContentRef(graph));
        bool graphOk = g.Kind == ExtractKind.Leaf && g.Leaf.Texture.IsNone
            && g.Leaf.BaseColor == 0xFFFF9933u;
        Check("shape 内容: 纯矩形 Graph → Solid 叶（fillColor #ff3399ff 手算打包）", graphOk);

        // item 的 Loader：无内容 + FGM303 有声
        NodeHandle loader = ByEditorId(item, "n0");
        bool loaderOk = item.Table.ContentRef(loader) == 0 && s.Diagnostics.Has(FgmCodes.ContentNotInM1);
        Check("shape 内容: Loader 无内容 + FGM303 有声（装载期动态物）", loaderOk);
    }

    /// <summary>文本块 5/6 解析成 TextStyle + 编译期度量 = TextCore 纯函数（同字体同输入逐位同）。</summary>
    private static void ShapeTextParsedAndMeasured(string dir)
    {
        ShapedPackage s = ShapeFixture(dir, "PullToRefresh");
        if (!s.TryGetComponent("n3qdr", out ShapedComponent? sc) || sc.Text == null)
        { Check("shape 文本: Header 在场", false); return; }

        // <text id="n1_n3qd" fontSize="22" color="#999999" align="center" autoSize="none" singleLine
        //  text="Loading"> ⇒ 不度量：resolved == authored 242×26
        NodeHandle n1 = ByEditorId(sc, "n1_n3qd");
        bool plain = sc.Text.TextOf(n1) == "Loading"
            && GeomIs(sc.Table, n1, 54f, 9f, 242f, 26f);
        Check("shape 文本: autoSize=none 不度量（authored 即 resolved）+ 文本串入账", plain);

        // <text id="n5_9sfl" fontSize="22" text="Refresh completed">（autoSize 默认 Both）⇒
        // 编译期度量 = TextCore.Layout 纯函数：期望值直接调同一函数（同字体逐位同——文本·不变量 1/2）
        NodeHandle n5 = ByEditorId(sc, "n5_9sfl");
        (GlyphMetricsTable met, ushort face) = TextFontRig();
        TextLayoutResult r = TLay(met, TStyle(face, 22f), "Refresh completed", float.PositiveInfinity, float.PositiveInfinity);
        ResolvedGeom g = sc.Table.GetResolved(n5);
        bool measured = FloatBits(g.W, r.ContentW) && FloatBits(g.H, r.ContentH);
        Check("shape 文本: autoSize=Both 编译期度量 = TextCore 纯函数逐位同（承诺 8 的度量半边）", measured);
    }

    // ── relation resize 对拍（fork 公式手推值）──────────────────────────────

    /// <summary>Header 组关系：组动、组员不动；跨组差值关系被捕获吸收。</summary>
    private static void ShapeGroupConstraintTracksResize(string dir)
    {
        ShapedPackage s = ShapeFixture(dir, "PullToRefresh");
        if (!s.TryGetComponent("n3qdr", out ShapedComponent? sc)) { Check("shape 组约束: Header 在场", false); return; }
        NodeTable t = sc.Table;

        t.SetSize(sc.Root, 500f, 100f);
        TickShaped(sc);

        // 组 n2 (52,14,296,46) center-center + bottom-bottom → 手推：
        //   centerΔ=0 ⇒ x = 250−148 = 102；bottom offset = 60−71 = −11 ⇒ y = (100−11)−46 = 43。
        NodeHandle g2 = ByEditorId(sc, "n2_n3qd");
        bool groupMoved = GeomIs(t, g2, 102f, 43f, 296f, 46f);
        // 组员组空间坐标不动（树级联免费——GGroup 真节点化的意义所在）
        bool membersStay = GeomIs(t, ByEditorId(sc, "n0_n3qd"), 2f, 6f, 42f, 34f)
            && GeomIs(t, ByEditorId(sc, "n3_n3qd"), 0f, 0f, 40f, 46f);
        // 组员 n4（在组 n6 内）对根的 Width 差值关系：400→500 ⇒ 500（尺寸空间不变，跨组照编）
        ResolvedGeom n4 = t.GetResolved(ByEditorId(sc, "n4_9sfl"));
        bool crossW = FloatBits(n4.W, 500f) && FloatBits(n4.X, 0f);
        // 组员 n5 的 center-center 差值：跨组常量差被捕获吸收 ⇒ 中心跟到 250
        ResolvedGeom n5 = t.GetResolved(ByEditorId(sc, "n5_9sfl"));
        bool crossC = FloatBits(n5.X + n5.W * 0.5f, 250f);

        Check("shape 组约束: 组动员不动（fork 手推 102,43）", groupMoved && membersStay);
        Check("shape 组约束: 跨组差值/尺寸关系被捕获吸收（Width→500、中心→250）", crossW && crossC);
    }

    /// <summary>item 组件三种关系混排：Width / Bottom_Bottom / Right_Right 的 fork 手推值。</summary>
    private static void ShapeItemRelationsTrackResize(string dir)
    {
        ShapedPackage s = ShapeFixture(dir, "PullToRefresh");
        if (!s.TryGetComponent("n3qdm", out ShapedComponent? sc)) { Check("shape item 关系: 在场", false); return; }
        NodeTable t = sc.Table;

        t.SetSize(sc.Root, 800f, 100f);
        TickShaped(sc);

        // n3 (0,86,720,2) Width+Bottom_Bottom：w=720+80=800；bottom offset=88−88=0 ⇒ y=100−2=98
        bool n3 = GeomIs(t, ByEditorId(sc, "n3"), 0f, 98f, 800f, 2f);
        // title (80,7,530×h) Width：w = 530+80 = 610（autoSize=Height 的高度由度量出，不比）
        ResolvedGeom title = t.GetResolved(ByEditorId(sc, "n4"));
        bool titleW = FloatBits(title.W, 610f) && FloatBits(title.X, 80f);
        // time (626,7,83,28) Right_Right：right offset = 709−720 = −11 ⇒ x = (800−11)−83 = 706
        bool time = GeomIs(t, ByEditorId(sc, "n6"), 706f, 7f, 83f, 28f);
        Check("shape item 关系: Width/Bottom_Bottom/Right_Right 三形态 fork 手推值对拍", n3 && titleW && time);
    }

    /// <summary>VirtualList/Button1：四孩子绑父 width+height ⇒ 随根整体缩放。</summary>
    private static void ShapeButtonChildrenBindParent(string dir)
    {
        ShapedPackage s = ShapeFixture(dir, "VirtualList");
        if (!s.TryGetComponent("rpolb", out ShapedComponent? sc)) { Check("shape Button1: 在场", false); return; }
        NodeTable t = sc.Table;

        t.SetSize(sc.Root, 200f, 60f);
        TickShaped(sc);

        bool ok = true;
        int kids = 0;
        for (NodeHandle c = t.FirstChild(sc.Root); !c.IsNone; c = t.NextSibling(c))
        {
            kids++;
            ResolvedGeom g = t.GetResolved(c);
            // Width/Height 差值关系：孩子初始尺寸 == 组件源尺寸 ⇒ offset 0 ⇒ 跟满
            ok &= FloatBits(g.W, 200f) && FloatBits(g.H, 60f);
        }
        Check("shape Button1: 四孩子绑父 Width+Height 随根缩放（Button1.xml）", ok && kids == 4);
    }

    // ── 门与等价性 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 约束环拒绝（L0）负例：字节手术把 item 组件 title/desc 的两条 Width 关系
    /// （target=-1 ⇒ 编码 FF FF 01 0E 00）互指成环 ⇒ FGM101 + 闭合环路径 + 组件除名；
    /// 其余组件照常定形（正例随每个 fixture 用例天然在场）。
    /// </summary>
    private static void ShapeCycleRejectedFgm101(string dir)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(dir, "PullToRefresh.fui"));
        var needle = new byte[] { 0xFF, 0xFF, 0x01, 0x0E, 0x00 };
        var hits = new List<int>();
        for (int i = 0; i + needle.Length <= bytes.Length; i++)
        {
            bool m = true;
            for (int k = 0; k < needle.Length; k++)
                if (bytes[i + k] != needle[k]) { m = false; break; }
            if (m) hits.Add(i);
        }
        // 全包恰三处单边 Width→父：item/title、item/desc、Header/n4（授权 XML 数出来的）
        if (hits.Count != 3)
        { Check("shape 拒环: 手术位点定位（3 处单边 Width→父）", false); return; }

        // 组件按包条目序写出：Footer(无 Width)、item(title=首处、desc=次处)、Header(第三处)。
        // title(孩子 2) 指 desc(孩子 3)，desc 指 title：.fui 大端 i16。
        bytes[hits[0]] = 0x00; bytes[hits[0] + 1] = 0x03;
        bytes[hits[1]] = 0x00; bytes[hits[1] + 1] = 0x02;

        CompileOptions opts = ShapeOpts();
        ShapedPackage s = FgbCompiler.Shape(bytes, in opts);
        bool gate = !s.Success
            && s.Diagnostics.TryFirst(FgmCodes.CycleRejected, out FgmDiagnostic d)
            && d.Severity == FgmSeverity.Error
            && d.Message.Contains("n4") && d.Message.Contains("n5")   // 闭合环路径带编辑器 id
            && !s.TryGetComponent("n3qdm", out _)                     // 成环组件除名
            && s.TryGetComponent("9sflu", out _)                      // 其余照常
            && s.TryGetComponent("n3qdr", out _);
        Check("shape 拒环: FGM101（L0）——环 = 编译错 + 闭合环路径 + 组件除名，余者照编", gate);
    }

    /// <summary>六个样例包全体定形：门全绿 + LateSlotAllocs 恒零（编译期布局写集在布防期定死）。</summary>
    private static void ShapeAllFixturesGatesGreen(string dir)
    {
        string[] names = { "VirtualList", "Cooldown", "ScrollPane", "TextMeshPro", "PullToRefresh", "TurnPage" };
        bool ok = true;
        int comps = 0;
        foreach (string name in names)
        {
            ShapedPackage s = ShapeFixture(dir, name);
            if (!s.Success)
            {
                Console.WriteLine("     " + name + " 定形失败：\n" + s.Diagnostics);
                ok = false;
                continue;
            }
            foreach (ShapedComponent sc in s.Components)
            {
                comps++;
                LayoutStats st = sc.Layout.Stats;
                bool green = st.LateSlotAllocs == 0 && st.IdempotenceFailures == 0
                    && st.DifferentialFailures == 0 && !st.PendingWork && sc.ShapeTicks <= 2;
                if (!green)
                {
                    Console.WriteLine($"     {name}/{sc.Item.Id} late={st.LateSlotAllocs} idem={st.IdempotenceFailures} diff={st.DifferentialFailures}");
                    ok = false;
                }
            }
        }
        Check("shape 门: 六包 " + comps + " 组件全定形——幂等/差分门绿 + LateSlotAllocs 恒零（升门 FGM902）", ok && comps >= 15);
    }

    /// <summary>
    /// 「编译产物 resolved 与手工建等价树运行时解出的逐位同」：TurnPage/Page 按授权 XML 常量
    /// 手工重建（含 pivotAsAnchor 手工换算原点、同字体文本度量），tick 后与编译产物全节点比对。
    /// </summary>
    private static void ShapeCompiledEqualsHandBuiltRuntime(string dir)
    {
        ShapedPackage s = ShapeFixture(dir, "TurnPage");
        if (!s.TryGetComponent("gawe1", out ShapedComponent? sc)) { Check("shape 等价: Page 在场", false); return; }

        // 手工建等价树（Page.xml 常量；model 的原点手工换算 (152,339)−(0.5,1)×(243,266) 再减组原点）
        var table = new NodeTable(tree: 7);
        var inval = new Invalidation(table);
        var kernel = new UiKernel(table, inval);
        var layout = new LayoutEngine(kernel) { IdempotenceGate = true, DifferentialGate = true };
        layout.Attach();
        inval.Register(new AbsorbAllSink());
        var content = new ContentTable();
        var met = new GlyphMetricsTable();
        ushort face = met.RegisterFace(TtfFontFace.Load(SynthFontT(), "SynthT"));
        var text = new TextSystem(kernel, met, new GlyphStore(), content);
        text.Attach(layout);

        table.SetSize(table.Root, 300f, 400f);
        NodeHandle N(ushort type, float x, float y, float w, float h, NodeHandle parent = default)
        {
            NodeHandle n = table.CreateNode(type);
            table.AddChild(parent.IsNone ? table.Root : parent, n);
            table.SetSize(n, w, h);
            table.SetPosition(n, x, y);
            return n;
        }
        NodeHandle n18 = N(NodeType.Image, 0f, 0f, 300f, 400f);
        NodeHandle pic = N(NodeType.Loader, 5f, 6f, 290f, 388f);
        NodeHandle n16 = N(NodeType.Image, 276f, 0f, 24f, 400f);
        NodeHandle n17 = N(NodeType.Image, 0f, 0f, 24f, 400f);
        NodeHandle pn = N(NodeType.Text, 274f, 377f, 22f, 18f);          // autoSize=none：不挂度量
        NodeHandle grp = N(NodeType.Group, 111f, 29f, 284f, 577f);
        NodeHandle model = N(NodeType.Shape, 30.5f - 111f, 73f - 29f, 243f, 266f, grp);
        table.SetPivot(model, 0.5f, 1f);
        NodeHandle n25 = N(NodeType.Text, 0f, 0f, 65f, 27f, grp);
        NodeHandle n28 = N(NodeType.Text, 123f, 120f, 55f, 102f);
        text.AttachText(n25, "Model", TStyle(face, 23f, wrap: true, h: TextHAlign.Center), autoWidth: true, autoHeight: true);
        layout.RegisterMeasured(n25, true, true);
        text.AttachText(n28, "?", TStyle(face, 98f, wrap: true, h: TextHAlign.Center), autoWidth: true, autoHeight: true);
        layout.RegisterMeasured(n28, true, true);

        var ft = FrameTime.First(1f / 60f, 1f / 60f);
        kernel.Tick(in ft);

        // 逐位比对：手建句柄 ↔ 编译产物的 localId（显示列表序）
        var pairs = new (NodeHandle hand, string editorId)[]
        {
            (n18, "n18_jva6"), (pic, "n0_gawe"), (n16, "n16_jva6"), (n17, "n17_jva6"),
            (pn, "n24_jva6"), (model, "n20_jva6"), (n25, "n25_jva6"), (grp, "n26_jva6"), (n28, "n28_jva6"),
        };
        bool ok = layout.Stats.IdempotenceFailures == 0;
        ResolvedGeom rootHand = table.GetResolved(table.Root);
        ResolvedGeom rootComp = sc.Table.GetResolved(sc.Root);
        ok &= FloatBits(rootHand.W, rootComp.W) && FloatBits(rootHand.H, rootComp.H);
        foreach ((NodeHandle hand, string id) in pairs)
        {
            ResolvedGeom a = table.GetResolved(hand);
            ResolvedGeom b = sc.Table.GetResolved(ByEditorId(sc, id));
            bool same = FloatBits(a.X, b.X) && FloatBits(a.Y, b.Y) && FloatBits(a.W, b.W) && FloatBits(a.H, b.H);
            if (!same) Console.WriteLine($"     {id}: hand=({a.X},{a.Y},{a.W},{a.H}) compiled=({b.X},{b.Y},{b.W},{b.H})");
            ok &= same;
        }
        Check("shape 等价: 编译产物 resolved == 手工建等价树运行时解（TurnPage/Page 全节点逐位）", ok);
    }

    private sealed class AbsorbAllSink : IChannelDrain
    {
        public Ch Consumes => Ch.Content | Ch.Transform | Ch.Color | Ch.Visible | Ch.Structure;
        public void Drain(ref FrameContext ctx, Ch channel, ReadOnlySpan<NodeHandle> queue) { }
    }

    /// <summary>
    /// 「编译器 = 无头运行时」的另一半证据：定形世界就是运行时——扰动（根 resize）后回程，
    /// resolved 全节点逐位复原到编译产物（求值是 (authored, 图, offsets) 的纯函数，无路径依赖）。
    /// </summary>
    private static void ShapeRuntimePerturbReturnsBitIdentical(string dir)
    {
        ShapedPackage s = ShapeFixture(dir, "PullToRefresh");
        if (!s.TryGetComponent("n3qdr", out ShapedComponent? sc)) { Check("shape 扰动回程: Header 在场", false); return; }
        NodeTable t = sc.Table;

        int n = sc.Locals.Count;
        var snap = new ResolvedGeom[n];
        for (ushort i = 0; i < n; i++) snap[i] = t.GetResolved(sc.Locals.HandleOf(i));

        t.SetSize(sc.Root, 512f, 96f);
        TickShaped(sc);
        t.SetSize(sc.Root, 400f, 71f);
        TickShaped(sc, 2);

        bool ok = true;
        for (ushort i = 0; i < n; i++)
        {
            ResolvedGeom g = t.GetResolved(sc.Locals.HandleOf(i));
            ok &= FloatBits(g.X, snap[i].X) && FloatBits(g.Y, snap[i].Y)
                && FloatBits(g.W, snap[i].W) && FloatBits(g.H, snap[i].H);
        }
        Check("shape 扰动回程: resize 往返后全节点 resolved 逐位复原（定形世界即运行时，门全程在场）",
            ok && sc.Layout.Stats.IdempotenceFailures == 0 && sc.Layout.Stats.DifferentialFailures == 0);
    }

    /// <summary>确定性：同字节两次 Shape ⇒ 全组件全节点 resolved 逐位同 + 诊断文本逐字符同。</summary>
    private static void ShapeDeterminism(string dir)
    {
        ShapedPackage a = ShapeFixture(dir, "PullToRefresh");
        ShapedPackage b = ShapeFixture(dir, "PullToRefresh");
        bool ok = a.Success && b.Success && a.Components.Count == b.Components.Count
            && a.Diagnostics.ToString() == b.Diagnostics.ToString();
        for (int c = 0; ok && c < a.Components.Count; c++)
        {
            ShapedComponent ca = a.Components[c];
            ShapedComponent cb = b.Components[c];
            ok &= ca.Locals.Count == cb.Locals.Count && ca.ShapeTicks == cb.ShapeTicks;
            for (ushort i = 0; ok && i < ca.Locals.Count; i++)
            {
                ResolvedGeom ga = ca.Table.GetResolved(ca.Locals.HandleOf(i));
                ResolvedGeom gb = cb.Table.GetResolved(cb.Locals.HandleOf(i));
                ok &= FloatBits(ga.X, gb.X) && FloatBits(ga.Y, gb.Y) && FloatBits(ga.W, gb.W) && FloatBits(ga.H, gb.H);
            }
        }
        Check("shape 确定性: 两次 Shape 逐位同（resolved + 诊断文本）——编译产物 golden 的前置", ok);
    }

    private static void ShapeGarbageRejected()
    {
        CompileOptions opts = ShapeOpts();
        ShapedPackage s1 = FgbCompiler.Shape(new byte[] { 1, 2, 3 }, in opts);
        var junk = new byte[512];
        for (int i = 0; i < junk.Length; i++) junk[i] = (byte)(i * 37);
        ShapedPackage s2 = FgbCompiler.Shape(junk, in opts);
        Check("shape 拒收: 垃圾字节 ⇒ FGM001 + Success=false，不抛不越界",
            !s1.Success && s1.Diagnostics.Has(FgmCodes.PackageRejected)
            && !s2.Success && s2.Diagnostics.Has(FgmCodes.PackageRejected)
            && s1.Components.Count == 0);
    }

    /// <summary>无字体注册：文本节点保几何 + FGM301 有声，包整体仍定形（机制 13 的边界形态）。</summary>
    private static void ShapeWithoutFontsStillShapes(string dir)
    {
        ShapedPackage s = ShapeFixture(dir, "PullToRefresh", fonts: false);
        bool ok = s.Success && s.Diagnostics.Has(FgmCodes.FontUnmapped)
            && s.TryGetComponent("n3qdr", out ShapedComponent? sc)
            && sc!.Text == null
            // autoSize 文本无度量源 ⇒ resolved == authored（几何仍立得住）
            && GeomIs(sc.Table, ByEditorId(sc, "n5_9sfl"), 104f, 4f, 192f, 26f);
        Check("shape 无字体: FGM301 有声 + 文本保几何（font-map 缺失是编译诊断不是崩溃）", ok);
    }
}

