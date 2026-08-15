namespace FairyNext.Compiler.Fui;

/// <summary>
/// .fui 结构性不符。**只由编译器前端抛，且只在 TryParse 的括号内存在**——
/// 对外的 API 面是 <c>bool TryParse(..., out 诊断)</c>：.fui 是外部输入，
/// 「装不下的包」是数据事实，不是程序错误（平面五机制 12 fuzz 纪律）。
/// </summary>
public sealed class FuiFormatException : Exception
{
    public FuiFormatException(string message) : base(message) { }
}

/// <summary>
/// 包字节数组上的一段（绝对下标）。**本期不复制原始数据**：显示列表里那些本期不解释的部分
/// （gear / relation / transition / 各控件类型专属块）以 span 形式交给 M1-20 消费——
/// 编译器要的是「跑一遍运行时」，不是「先造一份中间对象图再造第二份」。
/// </summary>
public readonly struct FuiSpan : IEquatable<FuiSpan>
{
    /// <summary>相对包字节数组起点的绝对下标。</summary>
    public readonly int Offset;
    public readonly int Length;

    public FuiSpan(int offset, int length) { Offset = offset; Length = length; }

    public static FuiSpan Empty => new FuiSpan(0, 0);
    public bool IsEmpty => Length == 0;

    public bool Equals(FuiSpan other) => Offset == other.Offset && Length == other.Length;
    public override bool Equals(object? obj) => obj is FuiSpan s && Equals(s);
    public override int GetHashCode() => (Offset * 397) ^ Length;
    public override string ToString() => $"[{Offset}, {Offset + Length})";
}

/// <summary>
/// 包内资源条目类型（fork <c>PackageItemType</c> 逐值对齐，@ 08a2d56 Assets/Scripts/UI/FieldTypes.cs:3）。
/// 值是 .fui 里的字节，**永不重编号**。
/// </summary>
public enum FuiItemType : byte
{
    Image = 0,
    MovieClip = 1,
    Sound = 2,
    Component = 3,
    Atlas = 4,
    Font = 5,
    Swf = 6,
    Misc = 7,
    Unknown = 8,
    Spine = 9,
    DragonBones = 10,
}

/// <summary>
/// 显示对象类型（fork <c>ObjectType</c> 逐值对齐，FieldTypes.cs:18）。
/// 组件条目的 <c>extension</c> 字节也落在这个编号空间（Button/Label/... 是「带扩展语义的组件」）。
/// </summary>
public enum FuiObjectType : byte
{
    Image = 0,
    MovieClip = 1,
    Swf = 2,
    Graph = 3,
    Loader = 4,
    Group = 5,
    Text = 6,
    RichText = 7,
    InputText = 8,
    Component = 9,
    List = 10,
    Label = 11,
    Button = 12,
    ComboBox = 13,
    ProgressBar = 14,
    Slider = 15,
    ScrollBar = 16,
    Tree = 17,
    Loader3D = 18,
}

/// <summary>组件溢出处理（fork <c>OverflowType</c>）。Scroll 的参数在组件块 7。</summary>
public enum FuiOverflowType : byte
{
    Visible = 0,
    Hidden = 1,
    Scroll = 2,
}

/// <summary>
/// 关系类型（fork <c>RelationType</c> 逐值对齐）。M1-20 把每条关系编译成
/// EdgeFollow ConstraintOp（1~2 条）+ FanOut CSR，拓扑序在编译期定死。
/// </summary>
public enum FuiRelationType : byte
{
    Left_Left = 0,
    Left_Center = 1,
    Left_Right = 2,
    Center_Center = 3,
    Right_Left = 4,
    Right_Center = 5,
    Right_Right = 6,

    Top_Top = 7,
    Top_Middle = 8,
    Top_Bottom = 9,
    Middle_Middle = 10,
    Bottom_Top = 11,
    Bottom_Middle = 12,
    Bottom_Bottom = 13,

    Width = 14,
    Height = 15,

    LeftExt_Left = 16,
    LeftExt_Right = 17,
    RightExt_Left = 18,
    RightExt_Right = 19,
    TopExt_Top = 20,
    TopExt_Bottom = 21,
    BottomExt_Top = 22,
    BottomExt_Bottom = 23,

    Size = 24,
}
