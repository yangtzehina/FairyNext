using FairyNext.Numerics;

namespace FairyNext.Core;

/// <summary>
/// resolved 槽（16B/槽，稀疏 slab）——布局在 P5 的**独占产出**。
/// 布局只产出位置与尺寸；scale/rotation/skew/pivot 永远只有 authored 一份（机制③）。
/// </summary>
public struct ResolvedGeom
{
    /// <summary>父空间 x（节点原点 = 左上角）。</summary>
    public float X;
    /// <summary>父空间 y（y 向下）。</summary>
    public float Y;
    /// <summary>宽。</summary>
    public float W;
    /// <summary>高。</summary>
    public float H;
}

public sealed partial class NodeTable
{
    // resolved 槽池：下标 0 = 「无槽」哨兵（resolvedRef==0 ⇒ 读 authored 同一存储，零拷贝）
    private ResolvedGeom[] _resolvedPool = new ResolvedGeom[1];
    private int _resolvedCount = 1;

    // ────────────────────────────────────────────────────────────────────────
    // authored 读（不变量 13：写后同相位立即可见）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>读 authored 位置（**不是** resolved：GetPosition 读逻辑真值，机制③）。</summary>
    public Vector2 GetPosition(NodeHandle h) =>
        TryResolve(h, out uint i) ? new Vector2(_posX[i], _posY[i]) : Vector2.zero;

    /// <summary>读 authored 尺寸。</summary>
    public Vector2 GetSize(NodeHandle h) =>
        TryResolve(h, out uint i) ? new Vector2(_width[i], _height[i]) : Vector2.zero;

    /// <summary>读缩放（永远只有 authored 一份）。</summary>
    public Vector2 GetScale(NodeHandle h) =>
        TryResolve(h, out uint i) ? new Vector2(_scaleX[i], _scaleY[i]) : Vector2.one;

    /// <summary>读旋转（弧度）。</summary>
    public float GetRotation(NodeHandle h) => TryResolve(h, out uint i) ? _rotation[i] : 0f;

    /// <summary>读剪切角（弧度，水平剪切）。</summary>
    public float GetSkew(NodeHandle h) => TryResolve(h, out uint i) ? _skew[i] : 0f;

    /// <summary>读 pivot（归一化，[0,1] 为节点框内；只作旋转/缩放中心，不改原点语义）。</summary>
    public Vector2 GetPivot(NodeHandle h) =>
        TryResolve(h, out uint i) ? new Vector2(_pivotX[i], _pivotY[i]) : Vector2.zero;

    /// <summary>读局部 α（0..1；存储为 u8）。</summary>
    public float GetAlpha(NodeHandle h) =>
        TryResolve(h, out uint i) ? Visual.Alpha(_localVisual[i]) / 255f : 0f;

    /// <summary>读局部可见位（机制⑬：不可见不摘树）。</summary>
    public bool IsVisible(NodeHandle h) =>
        TryResolve(h, out uint i) && (_localVisual[i] & Visual.Visible) != 0;

    /// <summary>读局部置灰位。</summary>
    public bool IsGrayed(NodeHandle h) =>
        TryResolve(h, out uint i) && (_localVisual[i] & Visual.Grayed) != 0;

    /// <summary>读局部可命中位。</summary>
    public bool IsTouchable(NodeHandle h) =>
        TryResolve(h, out uint i) && (_localVisual[i] & Visual.Touchable) != 0;

    /// <summary>读 localVisual 原字（渲染/事件平面按 <see cref="Visual"/> 位域解读）。</summary>
    public uint LocalVisual(NodeHandle h) => TryResolve(h, out uint i) ? _localVisual[i] : 0u;

    /// <summary>节点类型。</summary>
    public ushort TypeOf(NodeHandle h) => TryResolve(h, out uint i) ? _typeId[i] : NodeType.None;

    /// <summary>模板内稳定局部索引（机制⑩）。</summary>
    public ushort LocalIdOf(NodeHandle h) => TryResolve(h, out uint i) ? _localId[i] : (ushort)0;

    /// <summary>所属实例块 id（0 = 树根域）。</summary>
    public uint OwnerInst(NodeHandle h) => TryResolve(h, out uint i) ? _ownerInst[i] : 0u;

    /// <summary>类型化内容池索引（Image→quad 描述、Text→排版实例、Component→实例块、Island→挂载表）。</summary>
    public uint ContentRef(NodeHandle h) => TryResolve(h, out uint i) ? _contentRef[i] : 0u;

    /// <summary>状态侧表引用（0 = 快路径：setter 直写 authored，全程不触碰侧表，机制⑧）。</summary>
    public uint StateRef(NodeHandle h) => TryResolve(h, out uint i) ? _stateRef[i] : 0u;

    /// <summary>resolved 槽引用（0 = 与 authored 共享存储）。</summary>
    public uint ResolvedRef(NodeHandle h) => TryResolve(h, out uint i) ? _resolvedRef[i] : 0u;

    // ────────────────────────────────────────────────────────────────────────
    // authored 写（等值切断 + 相位门 + 来源分流，机制④⑨ / 裁决 13）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>写 authored 位置（Ch.Transform）。值不变 ⇒ 不写、不 Mark。</summary>
    public void SetPosition(NodeHandle h, float x, float y, WriteSource src = WriteSource.User)
    {
        if (!ResolveForWrite(h, out uint i)) return;
        bool changed = false;
        if (!BitEquals.Eq(_posX[i], x)) { _posX[i] = x; changed = true; }
        if (!BitEquals.Eq(_posY[i], y)) { _posY[i] = y; changed = true; }
        if (changed) Mark(i, Ch.Transform, src);
    }

    /// <summary>写 authored 尺寸（Ch.Layout——width/height 是布局输入，不是变换）。</summary>
    public void SetSize(NodeHandle h, float width, float height, WriteSource src = WriteSource.User)
    {
        if (!ResolveForWrite(h, out uint i)) return;
        bool changed = false;
        if (!BitEquals.Eq(_width[i], width)) { _width[i] = width; changed = true; }
        if (!BitEquals.Eq(_height[i], height)) { _height[i] = height; changed = true; }
        if (changed) Mark(i, Ch.Layout, src);
    }

    /// <summary>写缩放（Ch.Transform）。Group 恒 identity 缩放（不变量 9）。</summary>
    public void SetScale(NodeHandle h, float scaleX, float scaleY, WriteSource src = WriteSource.User)
    {
        if (!ResolveForWrite(h, out uint i)) return;
        UiAssert.That(_typeId[i] != NodeType.Group || (scaleX == 1f && scaleY == 1f),
            "Group 纯度：typeId==Group ⇒ scaleX==scaleY==1（不变量 9）");
        bool changed = false;
        if (!BitEquals.Eq(_scaleX[i], scaleX)) { _scaleX[i] = scaleX; changed = true; }
        if (!BitEquals.Eq(_scaleY[i], scaleY)) { _scaleY[i] = scaleY; changed = true; }
        if (changed) Mark(i, Ch.Transform, src);
    }

    /// <summary>写旋转（弧度，绕 pivot；Ch.Transform）。Group 恒 0（不变量 9）。</summary>
    public void SetRotation(NodeHandle h, float radians, WriteSource src = WriteSource.User)
    {
        if (!ResolveForWrite(h, out uint i)) return;
        UiAssert.That(_typeId[i] != NodeType.Group || radians == 0f,
            "Group 纯度：typeId==Group ⇒ rotation==0（不变量 9）");
        if (BitEquals.Eq(_rotation[i], radians)) return;
        _rotation[i] = radians;
        Mark(i, Ch.Transform, src);
    }

    /// <summary>写剪切角（弧度，水平剪切；Ch.Transform）。</summary>
    public void SetSkew(NodeHandle h, float radians, WriteSource src = WriteSource.User)
    {
        if (!ResolveForWrite(h, out uint i)) return;
        if (BitEquals.Eq(_skew[i], radians)) return;
        _skew[i] = radians;
        Mark(i, Ch.Transform, src);
    }

    /// <summary>
    /// 写 pivot（归一化）。<paramref name="keepPosition"/>=true 时按机制⑪ 显式换算 authored 位置，
    /// 使节点的渲染结果不跳（局部矩阵保持不变）：pos' = pos + (A − I)·(p1−p0)·size。
    /// pivotAsAnchor 不在运行时存在——编译期已消灭（M1-20）。
    /// </summary>
    public void SetPivot(NodeHandle h, float pivotX, float pivotY, bool keepPosition = false,
        WriteSource src = WriteSource.User)
    {
        if (!ResolveForWrite(h, out uint i)) return;
        if (BitEquals.Eq(_pivotX[i], pivotX) && BitEquals.Eq(_pivotY[i], pivotY)) return;

        if (keepPosition)
        {
            ReadResolvedCore(i, out _, out _, out float w, out float hh);
            Basis2x2(i, out float a00, out float a01, out float a10, out float a11);
            float dx = (pivotX - _pivotX[i]) * w;
            float dy = (pivotY - _pivotY[i]) * hh;
            float shiftX = a00 * dx + a01 * dy - dx;
            float shiftY = a10 * dx + a11 * dy - dy;
            _posX[i] += shiftX;
            _posY[i] += shiftY;
        }
        _pivotX[i] = pivotX;
        _pivotY[i] = pivotY;
        Mark(i, Ch.Transform, src);
    }

    /// <summary>
    /// 写局部 α（0..1，存储 u8）。等值切断在 **u8 值**上进行：0.999 与 1.0 都是 255 ⇒ 不脏。
    /// NaN 与负值一律截为 0（<c>!(a &gt; 0)</c> 一次比较同时吃掉两者）。
    /// α 影响整棵子树的 worldVisual ⇒ 同时给下行通道位。
    /// </summary>
    public void SetAlpha(NodeHandle h, float alpha, WriteSource src = WriteSource.User)
    {
        if (!ResolveForWrite(h, out uint i)) return;
        byte a = ToU8(alpha);
        uint v = _localVisual[i];
        if (Visual.Alpha(v) == a) return;
        _localVisual[i] = Visual.WithAlpha(v, a);
        Mark(i, Ch.Color | Ch.DownColor, src);
    }

    /// <summary>写局部可见位（机制⑬：只改位，不摘树）。</summary>
    public void SetVisible(NodeHandle h, bool visible, WriteSource src = WriteSource.User)
        => SetVisualBit(h, Visual.Visible, visible, Ch.Visible | Ch.DownVisible, src);

    /// <summary>写局部置灰位（worldVisual 按 OR 级联）。</summary>
    public void SetGrayed(NodeHandle h, bool grayed, WriteSource src = WriteSource.User)
        => SetVisualBit(h, Visual.Grayed, grayed, Ch.Color | Ch.DownColor, src);

    /// <summary>写局部可命中位。</summary>
    public void SetTouchable(NodeHandle h, bool touchable, WriteSource src = WriteSource.User)
        => SetVisualBit(h, Visual.Touchable, touchable, Ch.Color, src);

    /// <summary>写像素对齐位（不级联）。</summary>
    public void SetPixelSnap(NodeHandle h, bool snap, WriteSource src = WriteSource.User)
        => SetVisualBit(h, Visual.PixelSnap, snap, Ch.Transform, src);

    /// <summary>写内容引用（资产/渲染平面用）。Group 恒 0（不变量 9）。</summary>
    public void SetContentRef(NodeHandle h, uint contentRef, WriteSource src = WriteSource.User)
    {
        if (!ResolveForWrite(h, out uint i)) return;
        UiAssert.That(_typeId[i] != NodeType.Group || contentRef == 0,
            "Group 纯度：typeId==Group ⇒ contentRef==0（不变量 9）");
        if (_contentRef[i] == contentRef) return;
        _contentRef[i] = contentRef;
        Mark(i, Ch.Content, src);
    }

    /// <summary>写状态侧表引用（分配与解释权归状态平面，机制⑧）。</summary>
    internal void SetStateRef(NodeHandle h, uint stateRef)
    {
        if (!TryResolve(h, out uint i)) { UiAssert.That(false, "SetStateRef 于失效句柄"); return; }
        _stateRef[i] = stateRef;
    }

    /// <summary>
    /// 状态平面的通用内部写口（接缝：<c>WriteAuthored(n, prop, v, src)</c> 内含等值切断）。
    /// propId 取自 <see cref="Contracts.Abi.PropIds"/>。
    /// **M1-09 的 setter codegen 接管后本 switch 由生成代码替换**——在此之前它是唯一的 PropId 分派点。
    /// </summary>
    /// <returns>值真的变了返回 true（= 已 Mark）。</returns>
    internal bool WriteAuthored(NodeHandle h, byte propId, float value, WriteSource src)
    {
        if (!ResolveForWrite(h, out uint i)) return false;
        switch (propId)
        {
            case 1: return WriteFloat(i, ref _width[i], value, Ch.Layout, src);       // Width
            case 2: return WriteFloat(i, ref _height[i], value, Ch.Layout, src);      // Height
            case 64:                                                                   // Alpha
            {
                byte a = ToU8(value);
                if (Visual.Alpha(_localVisual[i]) == a) return false;
                _localVisual[i] = Visual.WithAlpha(_localVisual[i], a);
                Mark(i, Ch.Color | Ch.DownColor, src);
                return true;
            }
            case 65: return WriteVisualBit(i, Visual.Visible, value != 0f, Ch.Visible | Ch.DownVisible, src);
            case 66: return WriteVisualBit(i, Visual.Grayed, value != 0f, Ch.Color | Ch.DownColor, src);
            case 128: return WriteFloat(i, ref _posX[i], value, Ch.Transform, src);   // X
            case 129: return WriteFloat(i, ref _posY[i], value, Ch.Transform, src);   // Y
            case 130: return WriteFloat(i, ref _scaleX[i], value, Ch.Transform, src); // ScaleX
            case 131: return WriteFloat(i, ref _scaleY[i], value, Ch.Transform, src); // ScaleY
            case 132: return WriteFloat(i, ref _rotation[i], value, Ch.Transform, src); // Rotation
            default:
                UiAssert.That(false, "WriteAuthored 收到未归属的 PropId=" + propId
                    + "（通道归属封闭：没有归属的属性不应存在 setter）");
                return false;
        }
    }

    private bool WriteFloat(uint i, ref float slot, float v, Ch ch, WriteSource src)
    {
        if (BitEquals.Eq(slot, v)) return false;
        slot = v;
        Mark(i, ch, src);
        return true;
    }

    private bool WriteVisualBit(uint i, uint bit, bool on, Ch ch, WriteSource src)
    {
        uint v = _localVisual[i];
        if (((v & bit) != 0) == on) return false;
        _localVisual[i] = on ? v | bit : v & ~bit;
        Mark(i, ch, src);
        return true;
    }

    private void SetVisualBit(NodeHandle h, uint bit, bool on, Ch ch, WriteSource src)
    {
        if (!ResolveForWrite(h, out uint i)) return;
        WriteVisualBit(i, bit, on, ch, src);
    }

    private static byte ToU8(float a) =>
        !(a > 0f) ? (byte)0 : a >= 1f ? (byte)255 : (byte)(a * 255f + 0.5f);

    private bool ResolveForWrite(NodeHandle h, out uint i)
    {
        if (!TryResolve(h, out i))
        {
            UiAssert.That(false, "authored 写于已失效句柄（gen 不符或已 DEAD）");
            return false;
        }
        // 不变量 3：P7/P8 期间 debug 断言；release 下写值照做、Mark 由失效平面延到下帧队列
        // （降级分支必须在断言之外，见 UiAssert 文件头）。
        UiAssert.That(Phase < FramePhase.P7_RenderDrain,
            "相位写门：P7/P8 禁改 authored（不变量 3）");
        return true;
    }

    // ────────────────────────────────────────────────────────────────────────
    // resolved（布局唯一写者，机制③ / 不变量 2、7）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 分配 resolved 槽。槽集合**编译期定死**（模板的布局写集），实例化时批量分配，
    /// 运行期 resolvedRef 不再变（不变量 7）——重复分配即孤儿槽，断言。
    /// </summary>
    internal uint AllocResolvedSlot(NodeHandle h)
    {
        if (!TryResolve(h, out uint i)) { UiAssert.That(false, "AllocResolvedSlot 于失效句柄"); return 0; }
        UiAssert.That(_resolvedRef[i] == 0, "resolved 槽重复分配（不变量 7：实例化后 resolvedRef 不再变）");
        if (_resolvedRef[i] != 0) return _resolvedRef[i];

        if (_resolvedCount == _resolvedPool.Length)
            Array.Resize(ref _resolvedPool, _resolvedPool.Length * 2);
        uint slot = (uint)_resolvedCount++;
        _resolvedPool[slot] = new ResolvedGeom { X = _posX[i], Y = _posY[i], W = _width[i], H = _height[i] };
        _resolvedRef[i] = slot;
        return slot;
    }

    /// <summary>
    /// 布局在 P5 的独占写口（不变量 2）：来源必须是 Layout、相位必须是 P5、
    /// 且节点必须持有 resolved 槽——无槽节点的 resolved 就是 authored，写它等于篡改逻辑真值。
    /// </summary>
    public void SetResolved(NodeHandle h, float x, float y, float w, float hh, WriteSource src)
    {
        if (!TryResolve(h, out uint i)) { UiAssert.That(false, "SetResolved 于失效句柄"); return; }
        UiAssert.That(src == WriteSource.Layout, "resolved 唯一写者门：src 必须为 Layout（不变量 2）");
        UiAssert.That(Phase == FramePhase.P5_Layout, "resolved 唯一写者门：仅 P5 可写（不变量 2）");
        uint r = _resolvedRef[i];
        UiAssert.That(r != 0, "resolved 写到无槽节点（不变量 7：写目标必须在编译期布局写集内）");
        if (r == 0) return;

        ref ResolvedGeom g = ref _resolvedPool[r];
        bool moved = !BitEquals.Eq(g.X, x) || !BitEquals.Eq(g.Y, y);
        bool sized = !BitEquals.Eq(g.W, w) || !BitEquals.Eq(g.H, hh);
        if (!moved && !sized) return;               // 等值切断同样管布局出口（机制④）
        g.X = x; g.Y = y; g.W = w; g.H = hh;
        Mark(i, moved && sized ? Ch.Transform | Ch.Content : moved ? Ch.Transform : Ch.Content, src);
    }

    /// <summary>
    /// 读 resolved 几何——渲染与命中读的唯一几何。
    /// resolvedRef==0 时直接读 authored 列（同一存储、零拷贝，机制③）。
    /// </summary>
    public ResolvedGeom GetResolved(NodeHandle h)
    {
        if (!TryResolve(h, out uint i)) return default;
        ReadResolvedCore(i, out float x, out float y, out float w, out float hh);
        return new ResolvedGeom { X = x, Y = y, W = w, H = hh };
    }

    private void ReadResolvedCore(uint i, out float x, out float y, out float w, out float h)
    {
        uint r = _resolvedRef[i];
        if (r == 0)
        {
            x = _posX[i]; y = _posY[i]; w = _width[i]; h = _height[i];
        }
        else
        {
            ref ResolvedGeom g = ref _resolvedPool[r];
            x = g.X; y = g.Y; w = g.W; h = g.H;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 单点几何（机制⑪ / 不变量 15）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **全仓库唯一的局部变换构造点**：
    /// <c>local = T(pos)·T(p·size)·R(rot)·K(skew)·S(scale)·T(−p·size)</c>。
    ///
    /// 明文语义：
    ///  - position 恒 = 节点原点（左上角）在父空间坐标；pivot 只作旋转/缩放中心，不改原点；
    ///  - 几何来源是 **resolved**（resolvedRef==0 时即 authored 同一存储，机制③）；
    ///  - 全链路 y 向下、角度为弧度：正角 = 从 +x 转向 +y，屏幕上视觉为顺时针；
    ///    y 翻转在整条链路上只存在于后端根的 <c>localScale=(s,−s,1)</c> 一处，核心永不翻转；
    ///  - 单值 skew = 水平剪切角：<c>K = [[1, tan(k)], [0, 1]]</c>
    ///    （fork 的双值 skewX/skewY 在编译期归一为单值；双值需求出现前不加列）。
    ///
    /// 任何地方需要局部矩阵一律调本方法——第二份公式在本仓库是缺陷，不是风格问题
    /// （旧架构 y 翻转 + pivotOffset + HandlePositionChanged 三处叠加换算是错位类 bug 的温床）。
    /// </summary>
    public Affine2D LocalMatrix(NodeHandle h) =>
        TryResolve(h, out uint i) ? LocalMatrixCore(i) : Affine2D.Identity;

    private Affine2D LocalMatrixCore(uint i)
    {
        ReadResolvedCore(i, out float x, out float y, out float w, out float h);
        Basis2x2(i, out float a00, out float a01, out float a10, out float a11);
        float px = _pivotX[i] * w;
        float py = _pivotY[i] * h;
        return new Affine2D(
            a00, a01, a10, a11,
            x + px - (a00 * px + a01 * py),
            y + py - (a10 * px + a11 * py));
    }

    /// <summary>R·K·S 的 2×2 部分（唯一公式的线性块；SetPivot 的 keepPosition 换算复用同一块）。</summary>
    private void Basis2x2(uint i, out float a00, out float a01, out float a10, out float a11)
    {
        float rot = _rotation[i];
        float c = MathF.Cos(rot);
        float s = MathF.Sin(rot);
        float sk = _skew[i];
        float k = sk == 0f ? 0f : MathF.Tan(sk);
        float sx = _scaleX[i];
        float sy = _scaleY[i];
        a00 = c * sx;
        a01 = sy * (c * k - s);
        a10 = s * sx;
        a11 = sy * (s * k + c);
    }
}
