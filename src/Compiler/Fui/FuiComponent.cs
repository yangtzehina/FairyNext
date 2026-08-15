using System.Collections.Generic;

namespace FairyNext.Compiler.Fui;

/// <summary>
/// 组件显示列表的**数据模型**（M1-12）。
///
/// 对应 fork 的 <c>GComponent.ConstructFromResourceCore</c>（@ 08a2d56 GComponent.cs:1412-1657）
/// 里读字节的那一半。**不实现三遍 Setup**（Setup_BeforeAdd → relations → Setup_AfterAdd）——
/// 那三遍是运行时对象构造，其顺序契约靠人肉维持；编译期不需要它：M1-20 直接由本模型产
/// NODE/CNST/BIND 段，顺序在编译期定死（平面五机制 5）。
///
/// 三遍收成一遍的机制：fork 的第二、三遍是**重走同一个孩子列表**（同样的 ushort 长度前缀、
/// 同样的 curPos），只为在孩子对象已存在后再读它的块 3 / 块 1、2。本模型把每个孩子记录切成
/// 自己的窗口后一次读完块 0/1/2/3，字节完全一致，且**孩子的块偏移再也够不到兄弟的字节**
/// （fork 那边块表偏移越界会读进相邻记录）。
///
/// 组件级块表：0 = 基本属性，1 = 控制器，2 = 孩子列表，3 = 本组件的关系，4 = 尾部
/// （customData/opaque/mask/命中区/进出场音效），5 = transition，6 = 扩展（Button/Label/...），
/// 7 = 滚动配置。孩子级块表：0 = 基本属性，1 = tooltips/组，2 = gear，3 = 关系，
/// 4 = 嵌套组件页同步，5+ = 各控件类型专属。
/// </summary>
public sealed class FuiComponent
{
    private FuiComponent(FuiPackage package, FuiItem item)
    {
        Package = package;
        Item = item;
        Controllers = System.Array.Empty<FuiController>();
        Children = System.Array.Empty<FuiChild>();
        Relations = System.Array.Empty<FuiRelation>();
        Transitions = System.Array.Empty<FuiTransition>();
        MaskId = -1;
    }

    public FuiPackage Package { get; }
    public FuiItem Item { get; }

    // ---- 块 0：基本属性 ----
    public int SourceWidth { get; private set; }
    public int SourceHeight { get; private set; }
    public bool HasMinMax { get; private set; }
    public int MinWidth { get; private set; }
    public int MaxWidth { get; private set; }
    public int MinHeight { get; private set; }
    public int MaxHeight { get; private set; }
    public bool HasPivot { get; private set; }
    public float PivotX { get; private set; }
    public float PivotY { get; private set; }
    /// <summary>pivotAsAnchor 在 M1-20 编译期被消灭（换算原点 + 注入约束），运行期不存在此概念。</summary>
    public bool PivotAsAnchor { get; private set; }
    public bool HasMargin { get; private set; }
    public int MarginTop { get; private set; }
    public int MarginBottom { get; private set; }
    public int MarginLeft { get; private set; }
    public int MarginRight { get; private set; }
    public FuiOverflowType Overflow { get; private set; }
    public bool HasClipSoftness { get; private set; }
    public int ClipSoftnessX { get; private set; }
    public int ClipSoftnessY { get; private set; }

    /// <summary>块 7 的滚动配置（Overflow == Scroll 时才有）。</summary>
    public FuiScrollConfig? Scroll { get; private set; }

    // ---- 块 1/2/3/5 ----
    public FuiController[] Controllers { get; private set; }
    public FuiChild[] Children { get; private set; }
    /// <summary>本组件自身的关系（target 下标解析到**自己的孩子**；-1 = 父）。</summary>
    public FuiRelation[] Relations { get; private set; }
    public FuiTransition[] Transitions { get; private set; }

    // ---- 块 4：尾部 ----
    public string? CustomData { get; private set; }
    public bool Opaque { get; private set; }
    /// <summary>遮罩孩子下标；-1 = 无遮罩。</summary>
    public int MaskId { get; private set; }
    public bool ReversedMask { get; private set; }
    /// <summary>像素命中所用的图片条目 id（null = 不用像素命中）。</summary>
    public string? HitTestId { get; private set; }
    public int HitTestOffsetX { get; private set; }
    /// <summary>HitTestId 为 null 时这一字段兼作「形状命中的孩子下标」（fork 同一对 int 的双关）。</summary>
    public int HitTestOffsetY { get; private set; }
    public string? SoundOnAdded { get; private set; }
    public string? SoundOnRemoved { get; private set; }

    /// <summary>块 6 是否存在（Button/Label/ProgressBar/... 的扩展数据；本期不解释）。</summary>
    public bool HasExtensionData { get; private set; }

    /// <summary>
    /// 重新打开本组件的原始块流。M1-20 用它就地重放本期不解释的块
    /// （块 6 扩展、孩子的 5+ 类型专属块）——不另造中间对象图。
    /// </summary>
    public ByteBuffer OpenRawData() => Package.Slice(Item.RawData);

    /// <summary>
    /// 解析一个组件条目。<paramref name="item"/> 必须是本包内 <see cref="FuiItemType.Component"/> 条目。
    /// 任意字节序列输入只有「成功」与「false + 诊断」两种结果。
    /// </summary>
    public static bool TryParse(FuiPackage package, FuiItem item, out FuiComponent? component, out string diagnostic)
    {
        component = null;
        if (package == null) { diagnostic = "组件解析：包为 null"; return false; }
        if (item == null) { diagnostic = "组件解析：条目为 null"; return false; }
        if (item.Type != FuiItemType.Component)
        {
            diagnostic = $"组件解析：条目 '{item.Id}' 的类型是 {item.Type}，不是 Component";
            return false;
        }
        if (item.RawData.IsEmpty) { diagnostic = $"组件解析：条目 '{item.Id}' 没有显示列表数据"; return false; }

        var c = new FuiComponent(package, item);
        try
        {
            c.Parse();
        }
        catch (FuiFormatException ex) { diagnostic = $"组件 '{item.Id}': " + ex.Message; return false; }
        catch (System.ArgumentException ex) { diagnostic = $"组件 '{item.Id}': 参数越界 —— " + ex.Message; return false; }

        component = c;
        diagnostic = string.Empty;
        return true;
    }

    private void Parse()
    {
        ByteBuffer buffer = OpenRawData();

        if (!buffer.Seek(0, 0))
            throw new FuiFormatException("缺基本属性块（块 0）");

        SourceWidth = buffer.ReadInt();
        SourceHeight = buffer.ReadInt();

        if (buffer.ReadBool())
        {
            HasMinMax = true;
            MinWidth = buffer.ReadInt();
            MaxWidth = buffer.ReadInt();
            MinHeight = buffer.ReadInt();
            MaxHeight = buffer.ReadInt();
        }

        if (buffer.ReadBool())
        {
            HasPivot = true;
            PivotX = buffer.ReadFloat();
            PivotY = buffer.ReadFloat();
            PivotAsAnchor = buffer.ReadBool();
        }

        if (buffer.ReadBool())
        {
            HasMargin = true;
            MarginTop = buffer.ReadInt();
            MarginBottom = buffer.ReadInt();
            MarginLeft = buffer.ReadInt();
            MarginRight = buffer.ReadInt();
        }

        Overflow = (FuiOverflowType)buffer.ReadByte();
        if (Overflow == FuiOverflowType.Scroll)
        {
            int savedPos = buffer.position;
            if (buffer.Seek(0, 7))
                Scroll = FuiScrollConfig.Read(buffer);
            buffer.position = savedPos;
        }

        if (buffer.ReadBool())
        {
            HasClipSoftness = true;
            ClipSoftnessX = buffer.ReadInt();
            ClipSoftnessY = buffer.ReadInt();
        }

        ParseControllers(buffer);
        ParseChildren(buffer);

        // 本组件自身的关系：目标下标解析到自己的孩子（fork 的 parentToChild = true）。
        if (buffer.Seek(0, 3))
            Relations = ReadRelations(buffer);

        ParseTail(buffer);
        ParseTransitions(buffer);

        HasExtensionData = buffer.Seek(0, 6);
    }

    private void ParseControllers(ByteBuffer buffer)
    {
        if (!buffer.Seek(0, 1)) return;

        int cnt = FuiPackage.RequireCount(buffer, buffer.ReadShort(), 2, "控制器");
        var list = new FuiController[cnt];
        for (int i = 0; i < cnt; i++)
        {
            int nextPos = buffer.ReadUshort();
            nextPos += buffer.position;
            FuiPackage.RequirePos(buffer, nextPos, "控制器记录长度");

            var span = new FuiSpan(buffer.bufferOffset + buffer.position, nextPos - buffer.position);
            list[i] = FuiController.Read(Package.Slice(span), span);

            buffer.position = nextPos;
        }
        Controllers = list;
    }

    private void ParseChildren(ByteBuffer buffer)
    {
        if (!buffer.Seek(0, 2)) return;

        int cnt = FuiPackage.RequireCount(buffer, buffer.ReadShort(), 2, "孩子");
        var list = new FuiChild[cnt];
        for (int i = 0; i < cnt; i++)
        {
            // fork 第一遍读 ReadShort、第二三遍读 ReadUshort（同一批字节）。取 ushort：
            // dataLen > 32767 时 short 会变负，position 反向跳。
            int dataLen = buffer.ReadUshort();
            int curPos = buffer.position;
            FuiPackage.RequirePos(buffer, curPos + dataLen, "孩子记录长度");

            var span = new FuiSpan(buffer.bufferOffset + curPos, dataLen);
            list[i] = FuiChild.Read(this, Package.Slice(span), span);

            buffer.position = curPos + dataLen;
        }
        Children = list;
    }

    private void ParseTail(ByteBuffer buffer)
    {
        if (!buffer.Seek(0, 4)) return;

        // fork 在此 Skip(2)（丢弃 customData 的串表下标）。这里读下来但**容忍**非法下标：
        // 丢了十几年的字段没人保证过它的值，读崩一个包不值得。
        int cdIndex = buffer.ReadUshort();
        CustomData = ResolveOptionalString(cdIndex);

        Opaque = buffer.ReadBool();
        MaskId = buffer.ReadShort();
        if (MaskId != -1)
            ReversedMask = buffer.ReadBool();

        HitTestId = buffer.ReadS();
        HitTestOffsetX = buffer.ReadInt();
        HitTestOffsetY = buffer.ReadInt();

        if (Package.Version >= 5)
        {
            SoundOnAdded = buffer.ReadS();
            SoundOnRemoved = buffer.ReadS();
        }
    }

    private string? ResolveOptionalString(int index)
    {
        if (index == 65534) return null;
        if (index == 65533) return string.Empty;
        string?[] table = Package.StringTable;
        return index < table.Length ? table[index] : null;
    }

    private void ParseTransitions(ByteBuffer buffer)
    {
        if (!buffer.Seek(0, 5)) return;

        int cnt = FuiPackage.RequireCount(buffer, buffer.ReadShort(), 2, "transition");
        var list = new FuiTransition[cnt];
        for (int i = 0; i < cnt; i++)
        {
            int nextPos = buffer.ReadUshort();
            nextPos += buffer.position;
            FuiPackage.RequirePos(buffer, nextPos, "transition 记录长度");

            var span = new FuiSpan(buffer.bufferOffset + buffer.position, nextPos - buffer.position);
            ByteBuffer sub = Package.Slice(span);
            // 只读到 name 为止：其余（options/autoPlay/items 与每个 item 的块表）归 ANIM 段，
            // 由 M1-20 就地重放——本期把它们再拆一遍只会多出一处会漂移的实现。
            string? name = sub.ReadS();
            list[i] = new FuiTransition(name, span);

            buffer.position = nextPos;
        }
        Transitions = list;
    }

    /// <summary>关系块：{ u8 条数; (i16 目标下标, u8 边数, (u8 类型, u8 百分比)[边数])[条数] }。</summary>
    internal static FuiRelation[] ReadRelations(ByteBuffer buffer)
    {
        int cnt = FuiPackage.RequireCount(buffer, buffer.ReadByte(), 3, "关系");
        if (cnt == 0) return System.Array.Empty<FuiRelation>();

        var items = new FuiRelation[cnt];
        for (int i = 0; i < cnt; i++)
        {
            short targetIndex = buffer.ReadShort();
            int cnt2 = FuiPackage.RequireCount(buffer, buffer.ReadByte(), 2, "关系边");
            var sides = new FuiRelationSide[cnt2];
            for (int j = 0; j < cnt2; j++)
                sides[j] = new FuiRelationSide((FuiRelationType)buffer.ReadByte(), buffer.ReadBool());
            items[i] = new FuiRelation(targetIndex, sides);
        }
        return items;
    }
}

/// <summary>
/// 显示列表里的一个孩子。**只解释寻址与几何**——gear / relation / 类型专属块留 span。
/// </summary>
public sealed class FuiChild
{
    private FuiChild(FuiComponent owner, FuiSpan data)
    {
        Owner = owner;
        Data = data;
        ScaleX = 1f;
        ScaleY = 1f;
        Alpha = 1f;
        Visible = true;
        Touchable = true;
        GroupId = -1;
        Gears = System.Array.Empty<FuiGear>();
        Relations = System.Array.Empty<FuiRelation>();
    }

    public FuiComponent Owner { get; }
    /// <summary>本孩子整条记录在包字节里的位置（M1-20 用它读类型专属块 5+）。</summary>
    public FuiSpan Data { get; }

    /// <summary>重新打开本孩子的块流（<c>Seek(0, n)</c> 即块 n；窗口被裁到本记录内）。</summary>
    public ByteBuffer Open() => Owner.Package.Slice(Data);

    // ---- 块 0 ----
    public FuiObjectType Type { get; private set; }
    /// <summary>资源条目 id；null = 无资源的原生对象（graph/text/group/...）。</summary>
    public string? Src { get; private set; }
    /// <summary>跨包引用时的包 id；null = 同包。</summary>
    public string? PkgId { get; private set; }
    /// <summary>编辑器 id（"n5"），M1-20 映射为 localId u16。</summary>
    public string? Id { get; private set; }
    public string? Name { get; private set; }
    public int X { get; private set; }
    public int Y { get; private set; }
    public bool HasSize { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool HasMinMax { get; private set; }
    public int MinWidth { get; private set; }
    public int MaxWidth { get; private set; }
    public int MinHeight { get; private set; }
    public int MaxHeight { get; private set; }
    public bool HasScale { get; private set; }
    public float ScaleX { get; private set; }
    public float ScaleY { get; private set; }
    public bool HasSkew { get; private set; }
    public float SkewX { get; private set; }
    public float SkewY { get; private set; }
    public bool HasPivot { get; private set; }
    public float PivotX { get; private set; }
    public float PivotY { get; private set; }
    public bool PivotAsAnchor { get; private set; }
    public float Alpha { get; private set; }
    public float Rotation { get; private set; }
    public bool Visible { get; private set; }
    public bool Touchable { get; private set; }
    public bool Grayed { get; private set; }
    public byte BlendMode { get; private set; }
    public bool HasColorFilter { get; private set; }
    public float FilterBrightness { get; private set; }
    public float FilterContrast { get; private set; }
    public float FilterSaturation { get; private set; }
    public float FilterHue { get; private set; }
    public string? CustomData { get; private set; }

    // ---- 块 1 ----
    public string? Tooltips { get; private set; }
    /// <summary>所属 GGroup 的孩子下标；-1 = 不属于任何组。M1-20 把组员重挂成真节点子树。</summary>
    public int GroupId { get; private set; }

    // ---- 块 2 / 3 ----
    public FuiGear[] Gears { get; private set; }
    public FuiRelation[] Relations { get; private set; }

    internal static FuiChild Read(FuiComponent owner, ByteBuffer b, FuiSpan span)
    {
        var c = new FuiChild(owner, span);

        if (!b.Seek(0, 0))
            throw new FuiFormatException("孩子记录缺基本属性块（块 0）");

        c.Type = (FuiObjectType)b.ReadByte();
        c.Src = b.ReadS();
        c.PkgId = b.ReadS();

        // fork 在这里重新 Seek(beginPos, 0) 再 Skip(5) 跳过上面三个字段（1 + 2 + 2）。
        // 同一条流上顺序读到这儿，位置已经就是 5，不必回跳。
        c.Id = b.ReadS();
        c.Name = b.ReadS();
        c.X = b.ReadInt();
        c.Y = b.ReadInt();

        if (b.ReadBool())
        {
            c.HasSize = true;
            c.Width = b.ReadInt();
            c.Height = b.ReadInt();
        }

        if (b.ReadBool())
        {
            c.HasMinMax = true;
            c.MinWidth = b.ReadInt();
            c.MaxWidth = b.ReadInt();
            c.MinHeight = b.ReadInt();
            c.MaxHeight = b.ReadInt();
        }

        if (b.ReadBool())
        {
            c.HasScale = true;
            c.ScaleX = b.ReadFloat();
            c.ScaleY = b.ReadFloat();
        }

        if (b.ReadBool())
        {
            c.HasSkew = true;
            c.SkewX = b.ReadFloat();
            c.SkewY = b.ReadFloat();
        }

        if (b.ReadBool())
        {
            c.HasPivot = true;
            c.PivotX = b.ReadFloat();
            c.PivotY = b.ReadFloat();
            c.PivotAsAnchor = b.ReadBool();
        }

        c.Alpha = b.ReadFloat();
        c.Rotation = b.ReadFloat();
        c.Visible = b.ReadBool();
        c.Touchable = b.ReadBool();
        c.Grayed = b.ReadBool();
        c.BlendMode = b.ReadByte();

        int filter = b.ReadByte();
        if (filter == 1)
        {
            c.HasColorFilter = true;
            c.FilterBrightness = b.ReadFloat();
            c.FilterContrast = b.ReadFloat();
            c.FilterSaturation = b.ReadFloat();
            c.FilterHue = b.ReadFloat();
        }

        c.CustomData = b.ReadS();

        if (b.Seek(0, 1))
        {
            c.Tooltips = b.ReadS();
            c.GroupId = b.ReadShort();
        }

        if (b.Seek(0, 2))
            c.Gears = ReadGears(b);

        if (b.Seek(0, 3))
            c.Relations = FuiComponent.ReadRelations(b);

        return c;
    }

    /// <summary>gear 块：{ u16 条数; (u16 记录长度, u8 gear 编号, 记录体)[条数] }。</summary>
    private static FuiGear[] ReadGears(ByteBuffer b)
    {
        int cnt = FuiPackage.RequireCount(b, b.ReadShort(), 3, "gear");
        if (cnt == 0) return System.Array.Empty<FuiGear>();

        var list = new List<FuiGear>(cnt);
        for (int i = 0; i < cnt; i++)
        {
            int nextPos = b.ReadUshort();
            nextPos += b.position;
            FuiPackage.RequirePos(b, nextPos, "gear 记录长度");

            byte kind = b.ReadByte();
            list.Add(new FuiGear(kind, new FuiSpan(b.bufferOffset + b.position, nextPos - b.position)));

            b.position = nextPos;
        }
        return list.ToArray();
    }

    public override string ToString() => $"{Type} '{Name}'({Id}) @{X},{Y}" + (HasSize ? $" {Width}x{Height}" : "");
}

/// <summary>控制器（块 1 的一条）。页表是 gear/BIND 编译的定义域。</summary>
public sealed class FuiController
{
    private FuiController(FuiSpan data)
    {
        Data = data;
        PageIds = System.Array.Empty<string?>();
        PageNames = System.Array.Empty<string?>();
    }

    public FuiSpan Data { get; }
    public string? Name { get; private set; }
    public bool AutoRadioGroupDepth { get; private set; }
    public string?[] PageIds { get; private set; }
    public string?[] PageNames { get; private set; }
    /// <summary>0 = 固定页，1 = 指定下标，2 = 按 branch，3 = 按变量（ver ≥ 2）。</summary>
    public int HomePageType { get; private set; }
    public int HomePageIndex { get; private set; }
    /// <summary>HomePageType == 3 时的变量名。</summary>
    public string? HomePageVar { get; private set; }

    public int PageCount => PageNames.Length;

    internal static FuiController Read(ByteBuffer b, FuiSpan span)
    {
        var c = new FuiController(span);

        if (!b.Seek(0, 0))
            throw new FuiFormatException("控制器记录缺块 0");
        c.Name = b.ReadS();
        c.AutoRadioGroupDepth = b.ReadBool();

        if (b.Seek(0, 1))
        {
            int cnt = FuiPackage.RequireCount(b, b.ReadShort(), 4, "控制器页");
            var ids = new string?[cnt];
            var names = new string?[cnt];
            for (int i = 0; i < cnt; i++)
            {
                ids[i] = b.ReadS();
                names[i] = b.ReadS();
            }
            c.PageIds = ids;
            c.PageNames = names;

            if (b.version >= 2)
            {
                c.HomePageType = b.ReadByte();
                switch (c.HomePageType)
                {
                    case 1: c.HomePageIndex = b.ReadShort(); break;
                    // 2 = 按当前 branch 取页，3 = 按变量取页：两者都是**运行期解析**，
                    // 编译期只记下依据。M1-20 的 branch 变体是编译期身份（平面五机制 14），
                    // type 2 因此在编译期就能定死；type 3 归状态平面。
                    case 3: c.HomePageVar = b.ReadS(); break;
                }
            }
        }

        return c;
    }

    public override string ToString() => $"controller '{Name}' pages={PageCount}";
}

/// <summary>一条 gear（十格之一）。编号 = fork 的 GearBase 下标；记录体归 M1-20 的 BIND 编译。</summary>
public readonly struct FuiGear
{
    /// <summary>0 Display / 1 XY / 2 Size / 3 Look / 4 Color / 5 Animation / 6 Text / 7 Icon / 8 FontSize / 9 Display2。</summary>
    public readonly byte Kind;
    public readonly FuiSpan Data;
    public FuiGear(byte kind, FuiSpan data) { Kind = kind; Data = data; }
    public override string ToString() => $"gear{Kind} {Data}";
}

/// <summary>一条关系（一个目标 + 若干条边）。</summary>
public readonly struct FuiRelation
{
    /// <summary>目标孩子下标；-1 = 父（组件自身）。</summary>
    public readonly short TargetIndex;
    public readonly FuiRelationSide[] Sides;
    public FuiRelation(short targetIndex, FuiRelationSide[] sides) { TargetIndex = targetIndex; Sides = sides; }
    public override string ToString() => $"rel→{TargetIndex} ×{Sides.Length}";
}

/// <summary>关系的一条边。<c>UsePercent</c> 在 M1-20 编译成 ratio 常量。</summary>
public readonly struct FuiRelationSide
{
    public readonly FuiRelationType Type;
    public readonly bool UsePercent;
    public FuiRelationSide(FuiRelationType type, bool usePercent) { Type = type; UsePercent = usePercent; }
    public override string ToString() => UsePercent ? Type + "%" : Type.ToString();
}

/// <summary>一条 transition（块 5 的一条）。记录体归 M1-20 的 ANIM 段。</summary>
public readonly struct FuiTransition
{
    public readonly string? Name;
    public readonly FuiSpan Data;
    public FuiTransition(string? name, FuiSpan data) { Name = name; Data = data; }
    public override string ToString() => $"transition '{Name}' {Data}";
}

/// <summary>
/// 块 7 的滚动配置。**纯数据**：fork 的 <c>ScrollPane.Setup</c> 读完这些字段后就开始造滚动条对象，
/// 那部分归 M2-06。flags 的位含义原样保留（fork ScrollPane.cs 同一段）。
/// </summary>
public sealed class FuiScrollConfig
{
    private FuiScrollConfig() { }

    /// <summary>0 Horizontal / 1 Vertical / 2 Both。</summary>
    public byte ScrollType { get; private set; }
    /// <summary>0 Default / 1 Visible / 2 Auto / 3 Hidden。</summary>
    public byte ScrollBarDisplay { get; private set; }
    public int Flags { get; private set; }
    public bool HasScrollBarMargin { get; private set; }
    public int ScrollBarMarginTop { get; private set; }
    public int ScrollBarMarginBottom { get; private set; }
    public int ScrollBarMarginLeft { get; private set; }
    public int ScrollBarMarginRight { get; private set; }
    public string? VtScrollBarRes { get; private set; }
    public string? HzScrollBarRes { get; private set; }
    public string? HeaderRes { get; private set; }
    public string? FooterRes { get; private set; }

    public bool DisplayOnLeft => (Flags & 1) != 0;
    public bool SnapToItem => (Flags & 2) != 0;
    public bool DisplayInDemand => (Flags & 4) != 0;
    public bool PageMode => (Flags & 8) != 0;
    public bool InertiaDisabled => (Flags & 256) != 0;
    public bool MaskDisabled => (Flags & 512) != 0;
    public bool Floating => (Flags & 1024) != 0;
    public bool DontClipMargin => (Flags & 2048) != 0;

    internal static FuiScrollConfig Read(ByteBuffer b)
    {
        var s = new FuiScrollConfig
        {
            ScrollType = b.ReadByte(),
            ScrollBarDisplay = b.ReadByte(),
            Flags = b.ReadInt(),
        };

        if (b.ReadBool())
        {
            s.HasScrollBarMargin = true;
            s.ScrollBarMarginTop = b.ReadInt();
            s.ScrollBarMarginBottom = b.ReadInt();
            s.ScrollBarMarginLeft = b.ReadInt();
            s.ScrollBarMarginRight = b.ReadInt();
        }

        s.VtScrollBarRes = b.ReadS();
        s.HzScrollBarRes = b.ReadS();
        s.HeaderRes = b.ReadS();
        s.FooterRes = b.ReadS();
        return s;
    }
}
