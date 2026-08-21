namespace FairyNext.Core.Layout;

/// <summary>
/// 流式布局描述（架构平面四 A 机制⑩ 第四层：行列/流式；不做完整 flexbox——编辑器永不产出 flex，
/// grow/shrink 两遍协商还破坏单遍拓扑求值）。M1-20 起由 FGB blob 段（LinearLayoutDesc）携带；
/// 本包由测试/宿主手建注册。
/// </summary>
public struct LinearLayoutDesc
{
    /// <summary>主轴垂直（false = 水平主轴，子节点从左往右排）。</summary>
    public bool Vertical;

    /// <summary>换行：主轴排满容器主轴尺寸即折行（判据 = 行内已有内容且再放会越界，对齐 fork 流式布局）。</summary>
    public bool Wrap;

    /// <summary>主轴相邻间距。</summary>
    public float Gap;

    /// <summary>行（交叉轴）间距。</summary>
    public float LineGap;

    /// <summary>
    /// parentUsesSize 位（架构机制⑩）：容器是否读子尺寸自撑。
    /// false = 固定尺寸容器：**子几何脏不向父上溯**（layout 通道的上溯剪断条件），容器尺寸不动；
    /// true = 交叉轴恒自撑为内容高度；主轴仅在**非 wrap** 时自撑（wrap 的折行宽依赖主轴尺寸，
    /// 主轴同时自撑会构成「宽定内容、内容定宽」的真环，静态排除）。
    /// </summary>
    public bool ParentUsesSize;

    /// <summary>本描述的自撑轴掩码（<see cref="LayoutOwn"/> 位；不自撑返回 0）。</summary>
    public byte AutoSizeMask()
    {
        if (!ParentUsesSize) return 0;
        byte cross = Vertical ? LayoutOwn.SizeX : LayoutOwn.SizeY;
        byte main = Vertical ? LayoutOwn.SizeY : LayoutOwn.SizeX;
        return Wrap ? cross : (byte)(cross | main);
    }
}
