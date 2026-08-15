using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using FairyNext.Numerics;

namespace FairyNext.Compiler.Fui;

/// <summary>
/// .fui 包描述符的**纯数据**内存模型（M1-12）。
///
/// 分块顺序照 fork 的 <c>UIPackage.LoadPackage</c>（@ 08a2d56 Assets/Scripts/UI/UIPackage.cs:675-967）：
/// 块 4 字符串表 → 块 5 字符串表补丁 → 块 0 依赖 + branch → 块 1 item 列表 → 块 2 sprite 表 →
/// 块 3 像素命中位图。**没有搬那 300 行**——fork 那段是 Unity 对象图构造（NTexture/AssetBundle/
/// UIObjectFactory/GLoader3D 集合），本类只留数据：条目元信息 + 原始 span 引用。
///
/// 为 M1-20 留的形状（「编译 = 对着无头运行时跑一遍再冻结」，承诺 8）：
///  - 本期解释的只有**定位与寻址所需**的字段（类型 / id / 名字 / 尺寸 / 图集矩形 / 显示列表结构）；
///  - 本期不解释的一律以 <see cref="FuiSpan"/> 原样交出（组件 rawData、各控件类型专属块、
///    gear / relation / transition），M1-20 用同一个 <see cref="ByteBuffer"/> 就地重放。
///    中间对象图只会多出一处会漂移的实现。
///
/// 失败面：<see cref="TryParse"/> 是唯一入口，任意字节序列输入只有「解析成功」与
/// 「返回 false + 诊断」两种结果，永不抛、永不越窗读（平面五机制 12 fuzz 纪律的前置）。
/// </summary>
public sealed class FuiPackage
{
    /// <summary>.fui 魔数 "FGUI"（大端读出的 u32）。</summary>
    public const uint Magic = 0x46475549;

    private readonly Dictionary<string, FuiItem> _itemsById = new Dictionary<string, FuiItem>();
    private readonly Dictionary<string, FuiItem> _itemsByName = new Dictionary<string, FuiItem>();
    private readonly Dictionary<string, FuiSprite> _spritesById = new Dictionary<string, FuiSprite>();
    private readonly List<FuiItem> _items = new List<FuiItem>();
    private readonly List<FuiSprite> _sprites = new List<FuiSprite>();

    private FuiPackage(byte[] bytes)
    {
        Bytes = bytes;
        // 平面五机制 3：包身份的 sourceHash。这里就是「每个装载入口都汇流的那一点」——
        // fork 把它算在 LoadPackage 里正是因为算在调用点会漏掉一部分部署形态。
        SourceHash = FnvHash.Hash64(bytes);
        StringTable = System.Array.Empty<string?>();
        Dependencies = System.Array.Empty<FuiDependency>();
        Branches = System.Array.Empty<string?>();
        Id = string.Empty;
        Name = string.Empty;
    }

    /// <summary>包字节。<see cref="FuiSpan"/> 的 Offset 都相对本数组起点。</summary>
    public byte[] Bytes { get; }

    /// <summary>整包字节的 FNV-1a 64（= FGB 头的 sourceHash 输入）。</summary>
    public ulong SourceHash { get; }

    /// <summary>描述符版本（ver ≥ 2 才有 branch 与 item 的高清/分支尾字段）。</summary>
    public int Version { get; private set; }

    /// <summary>头里的压缩位。本前端不支持压缩包——置位即拒收（诊断说明重新发布）。</summary>
    public bool Compressed { get; private set; }

    /// <summary>包 id（跨包引用的身份；文件名重名/非导出遮蔽的教训 ⇒ 以 id 定身份）。</summary>
    public string Id { get; private set; }

    public string Name { get; private set; }

    /// <summary>共享字符串表。<c>ReadS</c> 按下标取；65534/65533 是 null/空串哨兵，不占表位。</summary>
    public string?[] StringTable { get; private set; }

    /// <summary>依赖包（块 0）。M1-19 的 DEPS 段由此产出。</summary>
    public FuiDependency[] Dependencies { get; private set; }

    /// <summary>包内声明的 branch 名（ver2）。空 = 未发布分支变体。</summary>
    public string?[] Branches { get; private set; }

    public IReadOnlyList<FuiItem> Items => _items;
    public IReadOnlyList<FuiSprite> Sprites => _sprites;

    public bool TryGetItemById(string id, [MaybeNullWhen(false)] out FuiItem item) => _itemsById.TryGetValue(id, out item);
    public bool TryGetItemByName(string name, [MaybeNullWhen(false)] out FuiItem item) => _itemsByName.TryGetValue(name, out item);
    public bool TryGetSprite(string id, [MaybeNullWhen(false)] out FuiSprite sprite) => _spritesById.TryGetValue(id, out sprite);

    /// <summary>把一段 <see cref="FuiSpan"/> 变成可读的缓冲（字符串表与 version 已接好）。</summary>
    public ByteBuffer Slice(FuiSpan span)
    {
        var b = new ByteBuffer(Bytes, span.Offset, span.Length);
        b.stringTable = StringTable;
        b.version = Version;
        return b;
    }

    /// <summary>
    /// 唯一解析入口。返回 false 时 <paramref name="diagnostic"/> 说明哪一门拒的——
    /// 这条诊断串就是 M1-22 的 LoadReport 在 .fui 侧的对应物。
    /// </summary>
    public static bool TryParse(byte[]? bytes, out FuiPackage? package, out string diagnostic)
    {
        package = null;
        if (bytes == null) { diagnostic = ".fui: 字节数组为 null"; return false; }
        if (bytes.Length < 4) { diagnostic = $".fui: 长度 {bytes.Length} < 4，连魔数都装不下"; return false; }

        var p = new FuiPackage(bytes);
        try
        {
            p.Parse();
        }
        catch (FuiFormatException ex) { diagnostic = ".fui: " + ex.Message; return false; }
        catch (System.ArgumentException ex) { diagnostic = ".fui: 参数越界 —— " + ex.Message; return false; }

        package = p;
        diagnostic = string.Empty;
        return true;
    }

    // ---- 解析 --------------------------------------------------------------

    private void Parse()
    {
        var buffer = new ByteBuffer(Bytes);

        if (buffer.ReadUint() != Magic)
            throw new FuiFormatException("魔数不是 \"FGUI\"（旧版包格式或非 .fui 文件）");

        Version = buffer.ReadInt();
        if (Version < 0 || Version > 64)
            throw new FuiFormatException($"描述符版本 {Version} 不在 [0,64]——文件已损坏或不是 .fui");
        buffer.version = Version;
        bool ver2 = Version >= 2;

        Compressed = buffer.ReadBool();
        if (Compressed)
            throw new FuiFormatException("包体带压缩位：本前端只吃未压缩描述符，请以未压缩方式重新发布");

        Id = buffer.ReadString();
        Name = buffer.ReadString();

        buffer.Skip(20);
        int indexTablePos = buffer.position;
        if (indexTablePos < 0 || indexTablePos >= buffer.length)
            throw new FuiFormatException("块表位置越窗（头部被截断）");

        ParseStringTable(buffer, indexTablePos);
        ParseDependenciesAndBranches(buffer, indexTablePos, ver2, out bool branchIncluded);
        ParseItems(buffer, indexTablePos, ver2, branchIncluded);
        ParseSprites(buffer, indexTablePos, ver2);
        ParsePixelHitTest(buffer, indexTablePos);
    }

    /// <summary>块 4 = 字符串池；块 5 = 长串补丁（超 ushort 长度的串走这里，按下标覆盖）。</summary>
    private void ParseStringTable(ByteBuffer buffer, int indexTablePos)
    {
        // fork 忽略这里的返回值。字符串表缺席 ⇒ 后面每个 ReadS 都是错的，属结构性不符：响亮拒收。
        if (!buffer.Seek(indexTablePos, 4))
            throw new FuiFormatException("缺字符串表块（块 4）");

        int cnt = RequireCount(buffer, buffer.ReadInt(), 2, "字符串表");
        var table = new string?[cnt];
        for (int i = 0; i < cnt; i++)
            table[i] = buffer.ReadString();
        StringTable = table;
        buffer.stringTable = table;

        if (buffer.Seek(indexTablePos, 5))
        {
            cnt = RequireCount(buffer, buffer.ReadInt(), 6, "字符串表补丁");
            for (int i = 0; i < cnt; i++)
            {
                int index = buffer.ReadUshort();
                int len = buffer.ReadInt();
                string s = buffer.ReadString(len);
                if (index >= table.Length)
                    throw new FuiFormatException($"字符串表补丁下标 {index} 越表长 {table.Length}");
                table[index] = s;
            }
        }
    }

    /// <summary>块 0 = 依赖列表 + （ver2）branch 名表。</summary>
    private void ParseDependenciesAndBranches(ByteBuffer buffer, int indexTablePos, bool ver2, out bool branchIncluded)
    {
        branchIncluded = false;
        if (!buffer.Seek(indexTablePos, 0))
            throw new FuiFormatException("缺依赖块（块 0）");

        int cnt = RequireCount(buffer, buffer.ReadShort(), 4, "依赖");
        var deps = new FuiDependency[cnt];
        for (int i = 0; i < cnt; i++)
            deps[i] = new FuiDependency(buffer.ReadS(), buffer.ReadS());
        Dependencies = deps;

        if (ver2)
        {
            cnt = RequireCount(buffer, buffer.ReadShort(), 2, "branch");
            if (cnt > 0)
                Branches = buffer.ReadSArray(cnt);
            branchIncluded = cnt > 0;
        }
    }

    /// <summary>块 1 = item 列表。每条带 int 长度前缀（nextPos 相对跳转），未知尾字段自然跳过。</summary>
    private void ParseItems(ByteBuffer buffer, int indexTablePos, bool ver2, bool branchIncluded)
    {
        if (!buffer.Seek(indexTablePos, 1))
            throw new FuiFormatException("缺 item 列表块（块 1）");

        int cnt = RequireCount(buffer, buffer.ReadShort(), 4, "item");
        for (int i = 0; i < cnt; i++)
        {
            int nextPos = buffer.ReadInt();
            nextPos += buffer.position;
            RequirePos(buffer, nextPos, "item 记录长度");

            var pi = new FuiItem(this)
            {
                Type = (FuiItemType)buffer.ReadByte(),
            };
            pi.Id = buffer.ReadS() ?? string.Empty;
            pi.Name = buffer.ReadS();
            pi.Path = buffer.ReadS();
            pi.File = buffer.ReadS();
            pi.Exported = buffer.ReadBool();
            pi.Width = buffer.ReadInt();
            pi.Height = buffer.ReadInt();

            switch (pi.Type)
            {
                case FuiItemType.Image:
                    {
                        pi.ObjectType = FuiObjectType.Image;
                        int scaleOption = buffer.ReadByte();
                        if (scaleOption == 1)
                        {
                            pi.HasScale9Grid = true;
                            pi.Scale9GridX = buffer.ReadInt();
                            pi.Scale9GridY = buffer.ReadInt();
                            pi.Scale9GridWidth = buffer.ReadInt();
                            pi.Scale9GridHeight = buffer.ReadInt();
                            pi.TileGridIndice = buffer.ReadInt();
                        }
                        else if (scaleOption == 2)
                            pi.ScaleByTile = true;

                        pi.Smoothing = buffer.ReadBool();
                        break;
                    }

                case FuiItemType.MovieClip:
                    {
                        pi.Smoothing = buffer.ReadBool();
                        pi.ObjectType = FuiObjectType.MovieClip;
                        pi.RawData = ReadSpan(buffer);
                        break;
                    }

                case FuiItemType.Font:
                    {
                        pi.RawData = ReadSpan(buffer);
                        break;
                    }

                case FuiItemType.Component:
                    {
                        // extension > 0 ⇒ Button/Label/... 的「带扩展语义的组件」；
                        // fork 此处还会调 UIObjectFactory 挂 C# 扩展类型，那是对象图，编译期不需要。
                        int extension = buffer.ReadByte();
                        pi.ObjectType = extension > 0 ? (FuiObjectType)extension : FuiObjectType.Component;
                        pi.RawData = ReadSpan(buffer);
                        break;
                    }

                case FuiItemType.Spine:
                case FuiItemType.DragonBones:
                    {
                        pi.SkeletonAnchorX = buffer.ReadFloat();
                        pi.SkeletonAnchorY = buffer.ReadFloat();
                        break;
                    }

                    // Atlas / Sound / Misc：fork 在此把 file 拼上资源路径前缀（Unity 装载面）。
                    // 编译期不拼——路径前缀是宿主的事，TREF 段只需要 (pkgId, itemId)。
            }

            if (ver2)
            {
                pi.Branch = buffer.ReadS();
                if (pi.Branch != null)
                    pi.Name = pi.Branch + "/" + pi.Name;

                int branchCnt = buffer.ReadByte();
                if (branchCnt > 0)
                {
                    if (branchIncluded)
                        pi.Branches = buffer.ReadSArray(branchCnt);
                    else
                    {
                        // 未发布分支的包：这里存的是「分支变体 id → 本条目」的别名（fork 只读一个）。
                        string? alias = buffer.ReadS();
                        if (alias != null) _itemsById[alias] = pi;
                    }
                }

                int highResCnt = buffer.ReadByte();
                if (highResCnt > 0)
                    pi.HighResolution = buffer.ReadSArray(highResCnt);
            }

            _items.Add(pi);
            _itemsById[pi.Id] = pi;
            if (pi.Name != null)
                _itemsByName[pi.Name] = pi;

            buffer.position = nextPos;
        }
    }

    /// <summary>块 2 = sprite 表（图集内矩形）。M1-19 的 SPRT 段由此产出。</summary>
    private void ParseSprites(ByteBuffer buffer, int indexTablePos, bool ver2)
    {
        if (!buffer.Seek(indexTablePos, 2))
            throw new FuiFormatException("缺 sprite 表块（块 2）");

        int cnt = RequireCount(buffer, buffer.ReadShort(), 4, "sprite");
        for (int i = 0; i < cnt; i++)
        {
            int nextPos = buffer.ReadUshort();
            nextPos += buffer.position;
            RequirePos(buffer, nextPos, "sprite 记录长度");

            string itemId = buffer.ReadS() ?? throw new FuiFormatException($"sprite[{i}] 的 id 为 null");
            string atlasId = buffer.ReadS() ?? throw new FuiFormatException($"sprite[{i}] 的图集 id 为 null");
            if (!_itemsById.TryGetValue(atlasId, out FuiItem atlas))
                throw new FuiFormatException($"sprite[{i}] 指向不存在的图集条目 '{atlasId}'");

            var sprite = new FuiSprite(itemId, atlas)
            {
                RectX = buffer.ReadInt(),
                RectY = buffer.ReadInt(),
                RectWidth = buffer.ReadInt(),
                RectHeight = buffer.ReadInt(),
            };
            sprite.Rotated = buffer.ReadBool();
            if (ver2 && buffer.ReadBool())
            {
                sprite.OffsetX = buffer.ReadInt();
                sprite.OffsetY = buffer.ReadInt();
                sprite.OriginalWidth = buffer.ReadInt();
                sprite.OriginalHeight = buffer.ReadInt();
            }
            else if (sprite.Rotated)
            {
                // 旋转入图集：原始尺寸是矩形的转置（fork 同）。
                sprite.OriginalWidth = sprite.RectHeight;
                sprite.OriginalHeight = sprite.RectWidth;
            }
            else
            {
                sprite.OriginalWidth = sprite.RectWidth;
                sprite.OriginalHeight = sprite.RectHeight;
            }

            _sprites.Add(sprite);
            _spritesById[itemId] = sprite;

            buffer.position = nextPos;
        }
    }

    /// <summary>块 3 = 像素命中位图（可选）。位图字节原样交给 M1-21 的 HITT 段。</summary>
    private void ParsePixelHitTest(ByteBuffer buffer, int indexTablePos)
    {
        if (!buffer.Seek(indexTablePos, 3)) return;

        int cnt = RequireCount(buffer, buffer.ReadShort(), 4, "像素命中");
        for (int i = 0; i < cnt; i++)
        {
            int nextPos = buffer.ReadInt();
            nextPos += buffer.position;
            RequirePos(buffer, nextPos, "像素命中记录长度");

            string? itemId = buffer.ReadS();
            if (itemId != null && _itemsById.TryGetValue(itemId, out FuiItem pi) && pi.Type == FuiItemType.Image)
                pi.PixelHitTestData = new FuiSpan(buffer.bufferOffset + buffer.position, nextPos - buffer.position);

            buffer.position = nextPos;
        }
    }

    // ---- 硬化小工具 --------------------------------------------------------

    /// <summary>
    /// 计数字段的合理性门。<paramref name="minBytesEach"/> = 每条记录最少占几字节：
    /// 一个被改成 20 亿的计数不该先分配 20 亿个槽再失败——那是 OOM 而不是「拒收 + 诊断」。
    /// </summary>
    internal static int RequireCount(ByteBuffer buffer, int count, int minBytesEach, string what)
    {
        if (count < 0)
            throw new FuiFormatException($"{what} 计数为负：{count}");
        long need = (long)count * minBytesEach;
        int avail = buffer.length - buffer.position;
        if (need > avail)
            throw new FuiFormatException($"{what} 计数 {count} 越窗：需 ≥{need} 字节，剩 {avail}");
        return count;
    }

    internal static void RequirePos(ByteBuffer buffer, int pos, string what)
    {
        if (pos < 0 || pos > buffer.length)
            throw new FuiFormatException($"{what} 指向窗口外：pos={pos}，窗口长度 {buffer.length}");
    }

    /// <summary>读 int 长度前缀的子块，只记 span 不复制（span 用绝对下标）。</summary>
    private static FuiSpan ReadSpan(ByteBuffer buffer)
    {
        int count = buffer.ReadInt();
        if (count < 0 || count > buffer.length - buffer.position)
            throw new FuiFormatException($"子块长度 {count} 越窗（剩 {buffer.length - buffer.position}）");
        var span = new FuiSpan(buffer.bufferOffset + buffer.position, count);
        buffer.Skip(count);
        return span;
    }
}

/// <summary>依赖包引用（块 0 的一条）。M1-19 据此产 DEPS 段并重算 combinedRefHash。</summary>
public readonly struct FuiDependency
{
    public readonly string? Id;
    public readonly string? Name;
    public FuiDependency(string? id, string? name) { Id = id; Name = name; }
    public override string ToString() => $"{Name}({Id})";
}

/// <summary>包内一条资源。类型专属数据要么已解释（图片九宫/tile），要么留 span（组件/动画/字体）。</summary>
public sealed class FuiItem
{
    internal FuiItem(FuiPackage owner)
    {
        Owner = owner;
        Id = string.Empty;
    }

    public FuiPackage Owner { get; }
    public FuiItemType Type { get; internal set; }
    /// <summary>显示对象类型。组件条目带 extension 时即 Button/Label/... 的编号。</summary>
    public FuiObjectType ObjectType { get; internal set; }
    public string Id { get; internal set; }
    /// <summary>条目名（带 branch 前缀时形如 "分支名/原名"，与 fork 一致）。</summary>
    public string? Name { get; internal set; }
    public string? Path { get; internal set; }
    /// <summary>宿主文件名（图集/声音/骨骼）。**不带资源路径前缀**——前缀是宿主装载面的事。</summary>
    public string? File { get; internal set; }
    public bool Exported { get; internal set; }
    public int Width { get; internal set; }
    public int Height { get; internal set; }

    // ---- Image ----
    public bool HasScale9Grid { get; internal set; }
    public int Scale9GridX { get; internal set; }
    public int Scale9GridY { get; internal set; }
    public int Scale9GridWidth { get; internal set; }
    public int Scale9GridHeight { get; internal set; }
    /// <summary>九宫内哪些格子改用平铺（位掩码，fork 同名字段）。</summary>
    public int TileGridIndice { get; internal set; }
    public bool ScaleByTile { get; internal set; }
    public bool Smoothing { get; internal set; }

    // ---- MovieClip / Font / Component ----
    /// <summary>类型专属原始数据。组件 = 显示列表块流（见 <see cref="FuiComponent"/>）。</summary>
    public FuiSpan RawData { get; internal set; }

    // ---- Spine / DragonBones ----
    public float SkeletonAnchorX { get; internal set; }
    public float SkeletonAnchorY { get; internal set; }

    // ---- ver2 尾字段 ----
    public string? Branch { get; internal set; }
    public string?[]? Branches { get; internal set; }
    public string?[]? HighResolution { get; internal set; }

    /// <summary>块 3 的像素命中位图原始字节（空 = 该图无位图）。</summary>
    public FuiSpan PixelHitTestData { get; internal set; }

    public override string ToString() => $"{Type} {Id} '{Name}' {Width}x{Height}";
}

/// <summary>图集内的一块（块 2 的一条）。坐标全是整数像素——.fui 就是这么存的，别过一道浮点。</summary>
public sealed class FuiSprite
{
    internal FuiSprite(string id, FuiItem atlas) { Id = id; Atlas = atlas; }

    public string Id { get; }
    public FuiItem Atlas { get; }
    public int RectX { get; internal set; }
    public int RectY { get; internal set; }
    public int RectWidth { get; internal set; }
    public int RectHeight { get; internal set; }
    public bool Rotated { get; internal set; }
    public int OffsetX { get; internal set; }
    public int OffsetY { get; internal set; }
    public int OriginalWidth { get; internal set; }
    public int OriginalHeight { get; internal set; }

    public override string ToString() =>
        $"{Id}@{Atlas.Id} [{RectX},{RectY} {RectWidth}x{RectHeight}]{(Rotated ? " rot" : "")}";
}
