using FairyNext.Contracts;
using FairyNext.Numerics;

namespace FairyNext.Core.Rendering;

// ============================================================================
// 填充（技能 CD 类进度显示）的参数记录与 ABI 编码。
//
// v1.3 裁决（架构「平面三」机制 9 末句）：**径向填充不落孤岛**——fill 参数进实例的 aux/extra
// 由 shader 求值。理由是频次：一屏几十个技能图标各转各的 CD 是常态，把高频类型交给栅栏兜，
// 等于让最常见的界面把「唯一主路径」的承诺踩穿。
//
// 本文件的分工（三件事，各一个定义点）：
//  1. <see cref="FillMethod"/> / 三套 origin —— **编号与 fork 的 FieldTypes.cs 逐值相同**，
//     因为它们是 .fui 里的字节（M1-12 已把这些字段读成整数交给编译期）。重编号 = 读错资产。
//  2. <see cref="RadialFillParams"/> → <c>aux</c> 的位打包（表在 <see cref="Abi.RadialFillAuxBits"/>，
//     位移常量一律取生成物，本文件不手抄）。aux 是**参数记录**：无损、可回读、进规范化哈希。
//  3. 同一组参数 → <c>extra</c> 的**求值形态**（中心 + 起角 + 有符号扫角）。shader 因此不必按
//     method 分五路：给它一个圆心、一个起始角、一个带符号的扫过角，剩下的是一次角度比较。
//
// 两份表示只有 <see cref="Write"/> 一个写口（不变量式的纪律：参数与求值形态不可能各自漂移）。
//
// ── 线性填充为什么不走 shader ────────────────────────────────────────────────
// Horizontal/Vertical 填充在几何上就是**把 quad 裁短**——rect 与 UV 一起收缩，结果仍是一个
// 普通实例，零 flag、零 aux、参考光栅照常能画。只有 Radial90/180/360 需要角度判定。
// 于是「填充」这件事在本设计里分成两半：能用几何表达的用几何，剩下的才付 shader 的钱。
// fork 对两者一视同仁地重建网格（FillMesh 的 4/8/12 顶点分支），那是网格路径下的必然。
// ============================================================================

/// <summary>
/// 填充方法。**编号 = fork <c>FairyGUI.FillMethod</c> = .fui 字节值**，append-only。
/// </summary>
public enum FillMethod : byte
{
    /// <summary>不填充（整图）。</summary>
    None = 0,
    /// <summary>水平裁短（CPU 几何，不走 shader）。</summary>
    Horizontal = 1,
    /// <summary>垂直裁短（CPU 几何，不走 shader）。</summary>
    Vertical = 2,
    /// <summary>四分之一圆（pivot = 某个角）。</summary>
    Radial90 = 3,
    /// <summary>半圆（pivot = 某条边的中点）。</summary>
    Radial180 = 4,
    /// <summary>整圆（pivot = 中心；技能 CD 的典型形态）。</summary>
    Radial360 = 5,
}

/// <summary>水平填充起点（<see cref="FillMethod.Horizontal"/>）。编号同 fork。</summary>
public enum OriginHorizontal : byte
{
    /// <summary>自左向右。</summary>
    Left = 0,
    /// <summary>自右向左。</summary>
    Right = 1,
}

/// <summary>垂直填充起点（<see cref="FillMethod.Vertical"/>）。编号同 fork。</summary>
public enum OriginVertical : byte
{
    /// <summary>自上而下。</summary>
    Top = 0,
    /// <summary>自下而上。</summary>
    Bottom = 1,
}

/// <summary>90° 填充的 pivot 角。编号同 fork。</summary>
public enum Origin90 : byte
{
    /// <summary>左上角。</summary>
    TopLeft = 0,
    /// <summary>右上角。</summary>
    TopRight = 1,
    /// <summary>左下角。</summary>
    BottomLeft = 2,
    /// <summary>右下角。</summary>
    BottomRight = 3,
}

/// <summary>180° 填充的 pivot 边。编号同 fork。</summary>
public enum Origin180 : byte
{
    /// <summary>上边中点。</summary>
    Top = 0,
    /// <summary>下边中点。</summary>
    Bottom = 1,
    /// <summary>左边中点。</summary>
    Left = 2,
    /// <summary>右边中点。</summary>
    Right = 3,
}

/// <summary>360° 填充的起始方向（pivot 恒为中心）。编号同 fork。</summary>
public enum Origin360 : byte
{
    /// <summary>12 点方向起（技能 CD 的默认）。</summary>
    Top = 0,
    /// <summary>6 点方向起。</summary>
    Bottom = 1,
    /// <summary>9 点方向起。</summary>
    Left = 2,
    /// <summary>3 点方向起。</summary>
    Right = 3,
}

/// <summary>
/// 一次填充的全部参数（作者意图，未量化）。<see cref="Method"/> 为 <see cref="FillMethod.None"/>
/// 时其余字段无意义。
/// </summary>
public struct RadialFillParams : IEquatable<RadialFillParams>
{
    /// <summary>填充方法。</summary>
    public FillMethod Method;
    /// <summary>起点（按 <see cref="Method"/> 解释为 OriginHorizontal/Vertical/90/180/360）。</summary>
    public byte Origin;
    /// <summary>顺时针（屏幕视觉；本仓库 y 向下，故正角 = 顺时针）。</summary>
    public bool Clockwise;
    /// <summary>完成比 [0,1]（超范围一律夹紧；NaN 视作 0——不画好过画一个随机角）。</summary>
    public float Amount;

    /// <summary>不填充。</summary>
    public static RadialFillParams None => default;

    /// <summary>按四个分量建。</summary>
    public static RadialFillParams Of(FillMethod method, byte origin, float amount, bool clockwise = true) =>
        new RadialFillParams { Method = method, Origin = origin, Amount = amount, Clockwise = clockwise };

    /// <summary>是否需要 shader 求值（= 三个径向方法之一，且完成比落在开区间 (0,1)）。</summary>
    public readonly bool NeedsShader =>
        (Method == FillMethod.Radial90 || Method == FillMethod.Radial180 || Method == FillMethod.Radial360)
        && RadialFill.Clamp01(Amount) > 0f && RadialFill.Clamp01(Amount) < 1f;

    /// <inheritdoc/>
    public readonly bool Equals(RadialFillParams other) =>
        Method == other.Method && Origin == other.Origin && Clockwise == other.Clockwise
        && BitEquals.Eq(Amount, other.Amount);

    /// <inheritdoc/>
    public readonly override bool Equals(object? obj) => obj is RadialFillParams p && Equals(p);
    /// <inheritdoc/>
    public readonly override int GetHashCode() => HashCode.Combine(Method, Origin, Clockwise, Amount);
    /// <inheritdoc/>
    public readonly override string ToString() =>
        Method == FillMethod.None ? "fill none"
            : $"fill {Method} origin={Origin} amount={Amount} {(Clockwise ? "cw" : "ccw")}";
}

/// <summary>
/// 填充参数 ↔ ABI 的唯一编解码点（位移常量取自 <see cref="AbiLayout"/> 生成物）。
/// </summary>
public static class RadialFill
{
    /// <summary>完成比的定点分母（u16 满量程）。</summary>
    public const int AmountScale = 0xFFFF;

    /// <summary>夹到 [0,1]；NaN 落到 0（<c>!(v &gt; 0)</c> 一次比较同时吃掉负数与 NaN）。</summary>
    public static float Clamp01(float v) => !(v > 0f) ? 0f : (v < 1f ? v : 1f);

    /// <summary>完成比 → u16 定点（四舍五入）。</summary>
    public static uint QuantizeAmount(float amount) => (uint)(Clamp01(amount) * AmountScale + 0.5f);

    /// <summary>u16 定点 → 完成比。</summary>
    public static float DequantizeAmount(uint fixedAmount) =>
        (fixedAmount > AmountScale ? AmountScale : fixedAmount) / (float)AmountScale;

    /// <summary>
    /// 参数 → <c>aux</c> 位字（保留段恒零）。<see cref="FillMethod.None"/> 返回 0——
    /// 「没有填充」在 aux 里就是全零，与未初始化的实例天然同形。
    /// </summary>
    public static uint PackAux(in RadialFillParams p)
    {
        if (p.Method == FillMethod.None) return 0u;
        uint aux = ((uint)p.Method & AbiLayout.RadialFillMethodMask) << AbiLayout.RadialFillMethodShift;
        aux |= (p.Origin & AbiLayout.RadialFillOriginMask) << AbiLayout.RadialFillOriginShift;
        if (p.Clockwise) aux |= 1u << AbiLayout.RadialFillClockwiseShift;
        aux |= (QuantizeAmount(p.Amount) & AbiLayout.RadialFillAmountMask) << AbiLayout.RadialFillAmountShift;
        return aux;
    }

    /// <summary>
    /// <c>aux</c> 位字 → 参数（<see cref="PackAux"/> 的逆；amount 走定点往返，其余分量无损）。
    /// </summary>
    public static RadialFillParams UnpackAux(uint aux) => new RadialFillParams
    {
        Method = (FillMethod)AbiLayout.RadialFillMethod(aux),
        Origin = (byte)AbiLayout.RadialFillOrigin(aux),
        Clockwise = AbiLayout.RadialFillClockwise(aux) != 0,
        Amount = DequantizeAmount(AbiLayout.RadialFillAmount(aux)),
    };

    /// <summary>
    /// 一个径向方法覆盖的角度（turns：1 = 整圈）。线性方法与 None 返回 0。
    /// </summary>
    public static float CoverageTurns(FillMethod method) => method switch
    {
        FillMethod.Radial90 => 0.25f,
        FillMethod.Radial180 => 0.5f,
        FillMethod.Radial360 => 1f,
        _ => 0f,
    };

    /// <summary>
    /// pivot（旋转中心）在 quad 内的归一化坐标。
    /// 90° 是角、180° 是边中点、360° 是中心——三者是同一个「圆心在哪」的问题的三个答案。
    /// </summary>
    public static Vector2 Center(FillMethod method, byte origin) => method switch
    {
        FillMethod.Radial90 => (Origin90)origin switch
        {
            Origin90.TopLeft => new Vector2(0f, 0f),
            Origin90.TopRight => new Vector2(1f, 0f),
            Origin90.BottomLeft => new Vector2(0f, 1f),
            _ => new Vector2(1f, 1f),
        },
        FillMethod.Radial180 => (Origin180)origin switch
        {
            Origin180.Top => new Vector2(0.5f, 0f),
            Origin180.Bottom => new Vector2(0.5f, 1f),
            Origin180.Left => new Vector2(0f, 0.5f),
            _ => new Vector2(1f, 0.5f),
        },
        FillMethod.Radial360 => new Vector2(0.5f, 0.5f),
        _ => new Vector2(0.5f, 0.5f),
    };

    /// <summary>
    /// 扫过区间的**起始边**角度（turns，0 = +x 向右，正方向 = 顺时针，因为 y 向下）。
    ///
    /// 规则一句话：<b>从 pivot 出发、沿顺时针方向遇到的第一条覆盖边</b>。
    ///  · 90°：pivot 是角，两条边指向矩形内部——左上角是 右(0) 与 下(.25)，顺时针先遇 右，故 0；
    ///    依此右上=.25、右下=.5、左下=.75（= 角按 TL→TR→BR→BL 顺时针编号 × 0.25）。
    ///  · 180°：pivot 是边中点，两条边沿该边反向延伸——上边是 右(0) 与 左(.5)，顺时针先遇 右，故 0；
    ///    右边 .25、下边 .5、左边 .75。与 fork <c>FillMesh.FillRadial180</c> 的半区拆分同结论。
    ///  · 360°：pivot 是中心，起始边就是 origin 指的那个方向——上 = .75（12 点）、右 = 0、下 = .25、左 = .5。
    /// </summary>
    public static float StartTurns(FillMethod method, byte origin) => method switch
    {
        FillMethod.Radial90 => (Origin90)origin switch
        {
            Origin90.TopLeft => 0f,
            Origin90.TopRight => 0.25f,
            Origin90.BottomRight => 0.5f,
            _ => 0.75f,                       // BottomLeft
        },
        FillMethod.Radial180 => (Origin180)origin switch
        {
            Origin180.Top => 0f,
            Origin180.Right => 0.25f,
            Origin180.Bottom => 0.5f,
            _ => 0.75f,                       // Left
        },
        FillMethod.Radial360 => (Origin360)origin switch
        {
            Origin360.Right => 0f,
            Origin360.Bottom => 0.25f,
            Origin360.Left => 0.5f,
            _ => 0.75f,                       // Top（12 点起，技能 CD 的默认）
        },
        _ => 0f,
    };

    /// <summary>
    /// 参数 → <c>extra</c> 求值形态：<c>(centerX, centerY, startTurns, signedSweepTurns)</c>，
    /// 中心是 quad 内归一化坐标、角度单位是 turns。
    ///
    /// 逆时针不是「反着算一遍」而是**从区间的另一端起填**：起始边挪到 <c>start + coverage</c>，
    /// 扫角取负。于是无论方向，被填区间恒在 <c>[s0, s0+coverage]</c> 内，
    /// shader 只需要「从 start 沿 sign(sweep) 走 |sweep| 以内即覆盖」一条判据。
    /// </summary>
    public static Vector4 PackExtra(in RadialFillParams p)
    {
        float coverage = CoverageTurns(p.Method);
        if (coverage <= 0f) return Vector4.zero;

        Vector2 c = Center(p.Method, p.Origin);
        float start = StartTurns(p.Method, p.Origin);
        // amount 走与 aux 相同的定点往返：两份表示对同一个 quad 说的必须是同一个数。
        float amount = DequantizeAmount(QuantizeAmount(p.Amount));
        float sweep = coverage * amount;
        if (!p.Clockwise)
        {
            start += coverage;
            sweep = -sweep;
        }
        return new Vector4(c.x, c.y, start, sweep);
    }

    /// <summary>
    /// 把一次径向填充写进实例（<b>唯一写口</b>：flags 位 + aux 参数记录 + extra 求值形态一次落定）。
    /// 非径向或完成比不在 (0,1) 开区间的参数在此**不写任何东西**——满格是整图、空是不发射，
    /// 两者都不该带着一个 shader 分支上路。
    /// </summary>
    /// <returns>true = 真的写了 shader 求值参数。</returns>
    public static bool Write(ref QuadInstance q, in RadialFillParams p)
    {
        if (!p.NeedsShader) return false;
        q.Flags |= 1u << AbiLayout.FlagsRadialFillShift;
        q.Aux = PackAux(in p);
        q.Extra = PackExtra(in p);
        return true;
    }
}
