using FairyNext.Compiler.Fui;
using FairyNext.Core;
using FairyNext.Core.Layout;
using FairyNext.Numerics;

namespace FairyNext.Compiler.Shape;

/// <summary>
/// 一条关系边翻译出的单个约束算子描述（<see cref="RelationTable.Translate"/> 的输出行）。
/// IsPin=false ⇒ Follow(dst.DstEdge ← src.SrcEdge)，百分比位由关系边的 UsePercent 决定；
/// IsPin=true ⇒ Pin(dst.DstEdge)（Ext 类关系的「对边钉住」半边，恒非百分比）。
/// </summary>
internal readonly struct RelationOp
{
    public readonly LayoutAxis Axis;
    public readonly EdgeSel DstEdge;
    public readonly EdgeSel SrcEdge;
    public readonly bool IsPin;

    public RelationOp(LayoutAxis axis, EdgeSel dstEdge, EdgeSel srcEdge, bool isPin)
    {
        Axis = axis; DstEdge = dstEdge; SrcEdge = srcEdge; IsPin = isPin;
    }

    public override string ToString() =>
        IsPin ? "Pin(" + Axis + "." + DstEdge + ")" : "Follow(" + Axis + "." + DstEdge + " ← src." + SrcEdge + ")";
}

/// <summary>
/// 24+1 种 <see cref="FuiRelationType"/> → EdgeFollow 算子的翻译表（架构平面五烘焙表
/// 「relation（24 种）」行的实现）。**语义对照 fork Relations/RelationItem、不抄实现**：
/// fork 的每个 RelationType 在事件驱动下改写 owner 的 xMin/yMin/width/height，
/// 归约后恰是「dst 的一个轴标量跟随 src 的一个轴标量」；Ext 族是「一边跟随 + 对边钉住」
/// （fork 用「保持当前 xMin+width」的现值实现同一语义，多写者下会漂移，这里 Pin 存捕获常量）。
/// </summary>
internal static class RelationTable
{
    /// <summary>翻译一条关系边（最多 4 个算子：Size = Width+Height 两条 Follow）。返回算子数。</summary>
    public static int Translate(FuiRelationType type, Span<RelationOp> ops)
    {
        switch (type)
        {
            // ── X 位置族（fork RelationItem.ApplyOnXYChanged 的 x 分支）──────────
            case FuiRelationType.Left_Left: return One(ops, LayoutAxis.X, EdgeSel.Min, EdgeSel.Min);
            case FuiRelationType.Left_Center: return One(ops, LayoutAxis.X, EdgeSel.Min, EdgeSel.Center);
            case FuiRelationType.Left_Right: return One(ops, LayoutAxis.X, EdgeSel.Min, EdgeSel.Max);
            case FuiRelationType.Center_Center: return One(ops, LayoutAxis.X, EdgeSel.Center, EdgeSel.Center);
            case FuiRelationType.Right_Left: return One(ops, LayoutAxis.X, EdgeSel.Max, EdgeSel.Min);
            case FuiRelationType.Right_Center: return One(ops, LayoutAxis.X, EdgeSel.Max, EdgeSel.Center);
            case FuiRelationType.Right_Right: return One(ops, LayoutAxis.X, EdgeSel.Max, EdgeSel.Max);

            // ── Y 位置族（Top=Min / Middle=Center / Bottom=Max）────────────────
            case FuiRelationType.Top_Top: return One(ops, LayoutAxis.Y, EdgeSel.Min, EdgeSel.Min);
            case FuiRelationType.Top_Middle: return One(ops, LayoutAxis.Y, EdgeSel.Min, EdgeSel.Center);
            case FuiRelationType.Top_Bottom: return One(ops, LayoutAxis.Y, EdgeSel.Min, EdgeSel.Max);
            case FuiRelationType.Middle_Middle: return One(ops, LayoutAxis.Y, EdgeSel.Center, EdgeSel.Center);
            case FuiRelationType.Bottom_Top: return One(ops, LayoutAxis.Y, EdgeSel.Max, EdgeSel.Min);
            case FuiRelationType.Bottom_Middle: return One(ops, LayoutAxis.Y, EdgeSel.Max, EdgeSel.Center);
            case FuiRelationType.Bottom_Bottom: return One(ops, LayoutAxis.Y, EdgeSel.Max, EdgeSel.Max);

            // ── 尺寸族 ─────────────────────────────────────────────────────────
            case FuiRelationType.Width: return One(ops, LayoutAxis.X, EdgeSel.Size, EdgeSel.Size);
            case FuiRelationType.Height: return One(ops, LayoutAxis.Y, EdgeSel.Size, EdgeSel.Size);
            case FuiRelationType.Size:
                ops[0] = new RelationOp(LayoutAxis.X, EdgeSel.Size, EdgeSel.Size, false);
                ops[1] = new RelationOp(LayoutAxis.Y, EdgeSel.Size, EdgeSel.Size, false);
                return 2;

            // ── Ext 族：一边跟随 + 对边钉住（尺寸导出）─────────────────────────
            case FuiRelationType.LeftExt_Left: return Ext(ops, LayoutAxis.X, EdgeSel.Min, EdgeSel.Min, EdgeSel.Max);
            case FuiRelationType.LeftExt_Right: return Ext(ops, LayoutAxis.X, EdgeSel.Min, EdgeSel.Max, EdgeSel.Max);
            case FuiRelationType.RightExt_Left: return Ext(ops, LayoutAxis.X, EdgeSel.Max, EdgeSel.Min, EdgeSel.Min);
            case FuiRelationType.RightExt_Right: return Ext(ops, LayoutAxis.X, EdgeSel.Max, EdgeSel.Max, EdgeSel.Min);
            case FuiRelationType.TopExt_Top: return Ext(ops, LayoutAxis.Y, EdgeSel.Min, EdgeSel.Min, EdgeSel.Max);
            case FuiRelationType.TopExt_Bottom: return Ext(ops, LayoutAxis.Y, EdgeSel.Min, EdgeSel.Max, EdgeSel.Max);
            case FuiRelationType.BottomExt_Top: return Ext(ops, LayoutAxis.Y, EdgeSel.Max, EdgeSel.Min, EdgeSel.Min);
            case FuiRelationType.BottomExt_Bottom: return Ext(ops, LayoutAxis.Y, EdgeSel.Max, EdgeSel.Max, EdgeSel.Min);

            default: return 0;                     // 未知类型：调用方计诊断
        }
    }

    private static int One(Span<RelationOp> ops, LayoutAxis axis, EdgeSel dst, EdgeSel src)
    {
        ops[0] = new RelationOp(axis, dst, src, false);
        return 1;
    }

    private static int Ext(Span<RelationOp> ops, LayoutAxis axis, EdgeSel dst, EdgeSel src, EdgeSel pin)
    {
        ops[0] = new RelationOp(axis, dst, src, false);
        ops[1] = new RelationOp(axis, pin, pin, true);
        return 2;
    }
}

/// <summary>
/// 组件约束编译器（M1-20a）：显示列表的 relation 表 → <see cref="ConstraintGraphBuilder"/> 声明 →
/// <see cref="Seal"/>。**拒环门复用 ConstraintGraphBuilder.Seal**（M1-16 留缝原文）——本类不含
/// 第二套拒环/拓扑排序，只做翻译、census 与 pivotAsAnchor 锚定注入的决策。
/// </summary>
internal sealed class ConstraintCompiler
{
    private readonly ConstraintGraphBuilder _builder;
    private readonly int _localCount;
    private readonly byte[] _axisOps;          // (local<<1|axis) → 已声明算子数（含 Pin；饱和到 255）
    private readonly bool[] _axisSoloSize;     // (local<<1|axis) → 恰一个算子且它写 Size
    private readonly ushort[] _axisSoloSrc;    // 上者成立时该算子的源局部 id（0 = 组件根 = fork 的 parent）

    public ConstraintCompiler(int localCount)
    {
        _localCount = localCount;
        _builder = new ConstraintGraphBuilder(localCount < 2 ? 2 : localCount);
        _axisOps = new byte[localCount * 2];
        _axisSoloSize = new bool[localCount * 2];
        _axisSoloSrc = new ushort[localCount * 2];
    }

    /// <summary>已声明算子数。</summary>
    public int OpCount => _builder.OpCount;

    /// <summary>
    /// 翻译一条关系（一个目标 + 若干条边）。<paramref name="sameSpace"/> = dst 与 src 的父坐标系
    /// 相同（真节点化后组员的父是组）：百分比**位置**边跨坐标系时按 M1 面跳过 + FGM202——
    /// FollowPercent 的锚是 src 起点的绝对值，跨系直写是错切；FollowDelta 的常量差在捕获时
    /// 被吸收、百分比**尺寸**空间不变，两者照编。
    /// </summary>
    public void AddRelation(ushort dstLocal, ushort srcLocal, FuiRelationSide[] sides,
        bool sameSpace, string site, CompileDiagnostics diag)
    {
        Span<RelationOp> ops = stackalloc RelationOp[4];
        for (int s = 0; s < sides.Length; s++)
        {
            FuiRelationSide side = sides[s];
            int n = RelationTable.Translate(side.Type, ops);
            if (n == 0)
            {
                diag.Add(FgmCodes.RelationTargetInvalid, FgmSeverity.Warning, site,
                    "未知关系类型 " + (byte)side.Type + "，该边跳过");
                continue;
            }
            for (int k = 0; k < n; k++)
            {
                RelationOp op = ops[k];
                if (op.IsPin)
                {
                    _builder.Pin(dstLocal, op.DstEdge, op.Axis);
                    Census(dstLocal, op.Axis, sizeOp: false, srcLocal);
                    continue;
                }
                if (side.UsePercent && op.DstEdge != EdgeSel.Size && !sameSpace)
                {
                    diag.Add(FgmCodes.GroupCrossPercentSkipped, FgmSeverity.Warning, site,
                        "百分比位置关系 " + side.Type + " 跨组坐标系，M1 面不编（锚为 src 起点绝对值，跨系直写是错切）");
                    continue;
                }
                _builder.Follow(dstLocal, op.DstEdge, srcLocal, op.SrcEdge, op.Axis, side.UsePercent);
                Census(dstLocal, op.Axis, sizeOp: op.DstEdge == EdgeSel.Size, srcLocal);
            }
        }
    }

    private void Census(ushort dstLocal, LayoutAxis axis, bool sizeOp, ushort srcLocal)
    {
        int key = (dstLocal << 1) | (int)axis;
        if (_axisOps[key] != byte.MaxValue) _axisOps[key]++;
        _axisSoloSize[key] = _axisOps[key] == 1 && sizeOp;
        if (_axisSoloSize[key]) _axisSoloSrc[key] = srcLocal;
    }

    /// <summary>
    /// pivotAsAnchor 的锚定注入（编译期消灭的后半：原点换算在建树侧，这里烘 resize 时的定点）。
    ///
    /// **fork 对同一形态有两套定点，判据必须分开**（RelationItem.cs 391-410 的 Width /
    /// 411-431 的 Height，@ oracle 08a2d56）：
    ///  - 源是**组件根**（fork 的 <c>_target == _owner.parent</c>，relation <c>target=""</c> ⇒ 局部 0）：
    ///    fork 走 <c>tmp = xMin; SetSize(w, h, ignorePivot:true); xMin = tmp;</c>——**原点**被显式复原、
    ///    锚点反而移动。裸 Size 算子的求值就是这条（<c>npos = pos</c>），**不注**即对齐。
    ///  - 源是**兄弟**：fork 走 <c>_owner.width = …</c> ⇒ <c>SetSize(v, _rawHeight)</c> ⇒
    ///    pivotAsAnchor 分支只 <c>HandlePositionChanged()</c>、<c>_x</c>（锚点）不动 ⇒ **锚点**是定点。
    ///    这一条要 <see cref="ConstraintGraphBuilder.PinAnchor"/> 与尺寸算子配成联立对才成立。
    ///
    /// 于是判据 = 该轴恰**一个**算子 ∧ 它写 Size ∧ **它的源不是局部 0** ∧ 该轴 pivot 非零。
    /// 位置已被约束（1 个位置算子或 2 算子拉伸）的轴不注：位置有显式写者时 fork 的事件序产物
    /// 本就不定形（同轴晚到者互踩），这里取「显式约束优先」的不动点语义（对照见 architecture.md
    /// 实现期补充）。pivot 从 <paramref name="table"/> 读——与求值现场
    /// （<c>LayoutEngine.PivotOf</c>）**同一列**，注入系数与求值系数不可能分叉。
    /// </summary>
    /// <returns>注入的锚定算子数。</returns>
    public int InjectAnchorPins(bool[] anchored, NodeTable table, NodeHandle[] byLocal)
    {
        int injected = 0;
        for (ushort local = 1; local < _localCount; local++)
        {
            if (!anchored[local]) continue;
            Vector2 pivot = table.GetPivot(byLocal[local]);
            for (int axis = 0; axis < 2; axis++)
            {
                float piv = axis == 0 ? pivot.x : pivot.y;
                if (piv == 0f) continue;                       // 锚点 == 原点：方程是恒等式
                int key = (local << 1) | axis;
                if (_axisOps[key] != 1 || !_axisSoloSize[key]) continue;
                if (_axisSoloSrc[key] == 0) continue;          // 源 = 组件根：fork 定点是原点，不注
                _builder.PinAnchor(local, (LayoutAxis)axis);
                _axisOps[key]++;
                _axisSoloSize[key] = false;
                injected++;
            }
        }
        return injected;
    }

    /// <summary>封图（复用 <see cref="ConstraintGraphBuilder.Seal"/> 的校验/拓扑排序/拒环）。</summary>
    public ConstraintGraphResult Seal() => _builder.Seal();
}
