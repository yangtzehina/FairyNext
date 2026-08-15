using System.Reflection;
using FairyNext.AbiGen;
using FairyNext.Compiler.Fui;

namespace FairyNext.Tests;

/// <summary>
/// M1-12 .fui 前端读取器（ByteBuffer 移植 + 包/组件/显示列表解析）。
///
/// **对照物不是本读取器自己**：断言里的组件数 / 名字 / 尺寸 / 孩子数 / 关系边 / 滚动 flags
/// 抄自 FairyGUI 编辑器工程的授权 XML（`~/ECS/FairyGUI-unity/UIProject/assets/…`，oracle @ 08a2d56）
/// ——.fui 的上游源文件。授权 XML 与发布产物是两套独立表示，两边对得上才说明读对了。
/// 每条常量后面的注释写了它抄的是哪个文件的哪个属性。样例包与已知差异见
/// `tests/fixtures/fui/README.md`。
///
/// 四类用例：① 包描述符字段级对照；② 组件显示列表字段级对照；③ 两级块表 Seek 与字符串表
/// 哨兵的边界行为；④ 恶意输入——截断/翻位/越界偏移一律「返回失败 + 诊断」，永不抛、永不越窗读
/// （平面五机制 12 的前哨；常设 fuzz 门在 M1-22 上线，本包先把入口面收敛成 TryParse）。
/// </summary>
public static partial class Program
{
    private const string FuiFixtureDir = "tests/fixtures/fui";

    private static void FuiReaderSuite()
    {
        string? root = RepoRoot.Find(AppContext.BaseDirectory);
        if (root == null) { Check(".fui: 定位仓库根", false); return; }
        string dir = RepoRoot.ToAbsolute(root, FuiFixtureDir);

        FuiPackage? virtualList = LoadFixture(dir, "VirtualList");
        FuiPackage? cooldown = LoadFixture(dir, "Cooldown");
        FuiPackage? scrollPane = LoadFixture(dir, "ScrollPane");
        FuiPackage? textMeshPro = LoadFixture(dir, "TextMeshPro");
        if (virtualList == null || cooldown == null || scrollPane == null || textMeshPro == null) return;

        // ---- ① 包描述符 ----
        PackageHeaderFields(virtualList);
        PackageItemFields(virtualList);
        PackageSpriteTable(virtualList);
        PackageTrimmedSprites(cooldown);
        PackageDependenciesAndFont(cooldown);
        PackageVersion7(textMeshPro);

        // ---- ② 组件显示列表 ----
        ComponentMainDisplayList(virtualList);
        ComponentControllersAndGears(virtualList);
        ComponentRelationsToParent(virtualList);
        ComponentScrollBlock(scrollPane);
        ComponentRelationSideTypes(scrollPane);
        ComponentChildBasicProps(cooldown);
        ComponentTextKinds(textMeshPro);
        ComponentSpansStayInsideTheirRecord(virtualList);

        // ---- ③ 块表与字符串表 ----
        SeekBoundaries();
        StringTableSentinels();
        ByteBufferBigEndian();
        ByteBufferWindow();
        ByteBufferSubBufferOffset();
        ByteBufferNoStaticState();

        // ---- ④ 恶意输入 ----
        MalformedHeaders(dir);
        MalformedTruncation(dir);
        MalformedBitFlips(dir);
        MalformedComponentPayload(dir);

        // 可选：把整个 oracle 样例目录扫一遍（本机有 fork checkout 时才跑）。
        // 不设成常设用例是因为 CI 不该因为本机没有 oracle 而多一条永远缺席的门。
        OracleSweep();
    }

    private static FuiPackage? LoadFixture(string dir, string name)
    {
        string path = Path.Combine(dir, name + ".fui");
        if (!File.Exists(path)) { Check($".fui: 样例包存在（{name}）", false); return null; }
        if (!FuiPackage.TryParse(File.ReadAllBytes(path), out FuiPackage? pkg, out string diag) || pkg == null)
        {
            Check($".fui: 样例包可解析（{name}）", false);
            Console.WriteLine($"     {diag}");
            return null;
        }
        return pkg;
    }

    // ---- ① 包描述符字段级对照 ---------------------------------------------

    /// <summary>对照 UIProject/assets/VirtualList/package.xml：id、7 个组件/图片资源 + 发布期图集。</summary>
    private static void PackageHeaderFields(FuiPackage p)
    {
        int components = 0, images = 0, atlases = 0;
        foreach (FuiItem it in p.Items)
        {
            if (it.Type == FuiItemType.Component) components++;
            else if (it.Type == FuiItemType.Image) images++;
            else if (it.Type == FuiItemType.Atlas) atlases++;
        }

        Check(".fui 包头: VirtualList id/name/version（package.xml packageDescription@id=qkteqwfp）",
            p.Id == "qkteqwfp" && p.Name == "VirtualList" && p.Version == 2 && !p.Compressed);
        Check(".fui 条目计数: 3 组件 + 9 图片 + 1 发布期图集（package.xml <resources> 12 条，含 3 component/9 image）",
            components == 3 && images == 9 && atlases == 1 && p.Items.Count == 13);
        Check(".fui 字符串表: 62 条且首条非空（表长是解析出的，非空是「表真的被读进来了」的证据）",
            p.StringTable.Length == 62 && !string.IsNullOrEmpty(p.StringTable[0]));
    }

    /// <summary>对照 package.xml 的逐条资源属性：类型 / 名字 / 尺寸 / exported / 九宫格。</summary>
    private static void PackageItemFields(FuiPackage p)
    {
        bool main = p.TryGetItemById("c8s20", out FuiItem? mainItem)
            // package.xml: <component id="c8s20" name="Main.xml" exported="true"/>；尺寸抄自 Main.xml <component size="1136,640">
            && mainItem.Type == FuiItemType.Component && mainItem.Name == "Main"
            && mainItem.Exported && mainItem.Width == 1136 && mainItem.Height == 640
            && mainItem.ObjectType == FuiObjectType.Component && !mainItem.RawData.IsEmpty;
        Check(".fui 条目: Main = 导出组件 1136x640（package.xml + Main.xml）", main);

        bool button = p.TryGetItemById("c8s24", out FuiItem? mailItem)
            // mailItem.xml: <component size="380,125" extention="Button">
            && mailItem.Name == "mailItem" && mailItem.Width == 380 && mailItem.Height == 125
            && mailItem.ObjectType == FuiObjectType.Button && !mailItem.Exported;
        Check(".fui 条目: mailItem 的 extension=Button 落进 ObjectType（mailItem.xml extention）", button);

        bool grid = p.TryGetItemById("c8s21", out FuiItem? img)
            // package.xml: <image id="c8s21" name="1.png" scale="9grid" scale9grid="10,15,28,32"/>
            && img.Type == FuiItemType.Image && img.Name == "1" && img.HasScale9Grid
            && img.Scale9GridX == 10 && img.Scale9GridY == 15
            && img.Scale9GridWidth == 28 && img.Scale9GridHeight == 32 && !img.ScaleByTile;
        Check(".fui 条目: 九宫格 10,15,28,32 逐值对上 package.xml scale9grid", grid);

        bool plain = p.TryGetItemById("c8s23", out FuiItem? plainImg)
            && !plainImg.HasScale9Grid && plainImg.Width == 133 && plainImg.Height == 34;
        Check(".fui 条目: 非九宫图不带 grid（scale 缺席 ⇒ HasScale9Grid=false，不是零矩形）", plain);

        Check(".fui 条目: 按名字寻址与按 id 寻址指向同一条",
            p.TryGetItemByName("Main", out FuiItem? byName) && byName == mainItem);
    }

    /// <summary>sprite 表：图集矩形 / 旋转入图集 / 原始尺寸的转置规则。</summary>
    private static void PackageSpriteTable(FuiPackage p)
    {
        Check(".fui sprite 表: 9 张图各一条（package.xml 9 个 <image>）", p.Sprites.Count == 9);

        bool rot = p.TryGetSprite("c8s23", out FuiSprite? s)
            && s.Rotated && s.RectWidth == 34 && s.RectHeight == 133
            // 旋转入图集 ⇒ 原始尺寸是矩形的转置；133x34 就是 package.xml 里 4.png 的条目尺寸
            && s.OriginalWidth == 133 && s.OriginalHeight == 34
            && s.Atlas.Type == FuiItemType.Atlas && s.Atlas.Id == "atlas0";
        Check(".fui sprite: 旋转条目的 originalSize = 矩形转置（4.png 133x34 ⇄ 图集 34x133）", rot);

        bool up = p.TryGetSprite("c8s21", out FuiSprite? s2)
            && !s2.Rotated && s2.RectX == 219 && s2.RectY == 122
            && s2.RectWidth == 50 && s2.RectHeight == 62
            && s2.OriginalWidth == 50 && s2.OriginalHeight == 62;
        Check(".fui sprite: 未旋转条目 originalSize == rect（1.png 50x62）", up);
    }

    /// <summary>ver2 的 sprite 裁白分支：offset + originalSize 显式存储。</summary>
    private static void PackageTrimmedSprites(FuiPackage p)
    {
        // Cooldown 的数字图 1(4)_png 被裁掉了左右空白：图集里只占 22x42，原图 41x45，左上偏移 9,2。
        // 原始尺寸抄自 package.xml 的条目（p3yax = 1(4)_png.png），偏移只有裁白分支才会非零。
        bool trimmed = p.TryGetSprite("p3yax", out FuiSprite? s)
            && s.RectWidth == 22 && s.RectHeight == 42
            && s.OriginalWidth == 41 && s.OriginalHeight == 45
            && s.OffsetX == 9 && s.OffsetY == 2;
        Check(".fui sprite: 裁白条目走 ver2 offset/originalSize 分支（1(4)_png 41x45 → 22x42 @9,2）", trimmed);

        bool untrimmed = p.TryGetSprite("uo8117", out FuiSprite? m)
            && m.OffsetX == 0 && m.OffsetY == 0 && m.OriginalWidth == 48 && m.OriginalHeight == 48;
        Check(".fui sprite: 未裁白条目 offset 为零（mask0.png 48x48）", untrimmed);
    }

    private static void PackageDependenciesAndFont(FuiPackage p)
    {
        Check(".fui 依赖表: Cooldown 一条自指依赖（块 0；M1-19 的 DEPS 段输入）",
            p.Dependencies.Length == 1 && p.Dependencies[0].Id == "y768eypf"
            && p.Dependencies[0].Name == "Cooldown");

        // package.xml: <font id="p3yav" name="cdtime.fnt" exported="true"/>——位图字体的字模数据
        // 以子缓冲存在条目里；本期只认它是一段 span（字形源归 M1-17）。
        bool font = p.TryGetItemById("p3yav", out FuiItem? f)
            && f.Type == FuiItemType.Font && f.Name == "cdtime" && f.Exported
            && !f.RawData.IsEmpty && f.RawData.Offset + f.RawData.Length <= p.Bytes.Length;
        Check(".fui 条目: 字体 cdtime 的 rawData 是包内合法 span（不复制字节）", font);

        // 未被任何 sprite 引用的资源（package.xml 的 ltiqn / 15.png）在发布时被丢弃：
        // 授权 XML 与发布产物的差异是**发布器的既定行为**，读取器照实反映即可。
        Check(".fui 条目: 未引用资源不在发布产物内（package.xml 的 ltiqn 被发布器丢弃）",
            !p.TryGetItemById("ltiqn", out _));
    }

    private static void PackageVersion7(FuiPackage p)
    {
        Check(".fui 版本: TextMeshPro 描述符 version=7（≥5 分支：组件尾部两条音效串）",
            p.Version == 7 && p.Id == "33vzljm4");

        // 高版本包的组件尾部多读两个串。读错位的典型表现是尾部字段串味，这里用「解析成功 +
        // 尾部字段取值合理」兜住：mask 缺席应为 -1（不是随机短整数）。
        bool ok = p.TryGetItemById("v0400", out FuiItem? main)
            && FuiComponent.TryParse(p, main, out FuiComponent? c, out _) && c != null
            && c.MaskId == -1 && c.HitTestId == null && c.Children.Length == 5;
        Check(".fui 版本: version=7 组件尾部不越位（mask=-1、5 个孩子）", ok);
    }

    // ---- ② 组件显示列表字段级对照 -----------------------------------------

    private static FuiComponent? Comp(FuiPackage p, string itemId, string caseName)
    {
        if (!p.TryGetItemById(itemId, out FuiItem? item)) { Check(caseName, false); return null; }
        if (!FuiComponent.TryParse(p, item, out FuiComponent? c, out string diag) || c == null)
        {
            Check(caseName, false);
            Console.WriteLine($"     {diag}");
            return null;
        }
        return c;
    }

    /// <summary>对照 VirtualList/Main.xml 的 &lt;displayList&gt;：8 个孩子，逐个类型/名字/xy/size。</summary>
    private static void ComponentMainDisplayList(FuiPackage p)
    {
        const string name = ".fui 组件: VirtualList/Main 显示列表 8 孩子逐字段（Main.xml displayList）";
        FuiComponent? c = Comp(p, "c8s20", name);
        if (c == null) return;

        bool ok = c.SourceWidth == 1136 && c.SourceHeight == 640    // <component size="1136,640">
            && c.Children.Length == 8
            // <image id="n0" src="c8s21" xy="185,56" size="404,562"/>
            && Child(c, 0, FuiObjectType.Image, "n0", "c8s21", 185, 56, 404, 562)
            // <image id="n1" src="c8s22" xy="197,70" size="384,63"/>
            && Child(c, 1, FuiObjectType.Image, "n1", "c8s22", 197, 70, 384, 63)
            // <list id="n3" name="mailList" xy="197,130" size="380,473"/>——list 无 src
            && Child(c, 3, FuiObjectType.List, "mailList", null, 197, 130, 380, 473)
            // <text id="n4" xy="171,15" size="425,28"/>
            && Child(c, 4, FuiObjectType.Text, "n4", null, 171, 15, 425, 28)
            // <component id="n8" src="rpolb" xy="693,300" size="183,48"/>
            && Child(c, 7, FuiObjectType.Component, "n8", "rpolb", 693, 300, 183, 48);
        Check(name, ok);

        // <image id="n2" src="c8s23" xy="315,82"/>——XML 无 size 属性 ⇒ 用条目自身尺寸，
        // 显示列表里这一位是「缺席」而不是 0x0。这两者混同会让 M1-20 把默认尺寸写成零。
        Check(".fui 组件: 孩子未写 size 时字段缺席而非 0x0（Main.xml n2 无 size 属性）",
            c.Children[2].Id == "n2" && !c.Children[2].HasSize
            && c.Children[2].X == 315 && c.Children[2].Y == 82);
    }

    private static bool Child(FuiComponent c, int i, FuiObjectType type, string name, string? src,
                              int x, int y, int w, int h)
    {
        FuiChild ch = c.Children[i];
        return ch.Type == type && ch.Name == name && ch.Src == src
            && ch.X == x && ch.Y == y && ch.HasSize && ch.Width == w && ch.Height == h;
    }

    /// <summary>对照 VirtualList/mailItem.xml：3 个控制器的名字与页表 + 4 条 gearDisplay 的落位。</summary>
    private static void ComponentControllersAndGears(FuiPackage p)
    {
        const string name = ".fui 组件: mailItem 三控制器名字与页表（mailItem.xml <controller>）";
        FuiComponent? c = Comp(p, "c8s24", name);
        if (c == null) return;

        bool ctrl = c.Controllers.Length == 3
            // <controller name="IsRead" pages="0,未读,1,已读"/>
            && c.Controllers[0].Name == "IsRead" && c.Controllers[0].PageCount == 2
            && c.Controllers[0].PageNames[0] == "未读" && c.Controllers[0].PageNames[1] == "已读"
            && c.Controllers[0].PageIds[0] == "0" && c.Controllers[0].PageIds[1] == "1"
            // <controller name="button" pages="0,up,1,down,2,over,3,selectedOver"/>
            && c.Controllers[1].Name == "button" && c.Controllers[1].PageCount == 4
            && c.Controllers[1].PageNames[3] == "selectedOver"
            // <controller name="c1" pages="0,未领,1,已领"/>
            && c.Controllers[2].Name == "c1" && c.Controllers[2].PageCount == 2;
        Check(name, ctrl);

        // mailItem.xml 里带 <gearDisplay> 的正好是 n13/n15/n16/n17 四个孩子，其余零条。
        // gear 编号 0 = Display（fork GObject.GetGear 的下标空间）。
        var withGear = new List<string?>();
        foreach (FuiChild ch in c.Children)
            if (ch.Gears.Length > 0) withGear.Add(ch.Id);
        bool gears = withGear.Count == 4
            && withGear[0] == "n13" && withGear[1] == "n15" && withGear[2] == "n16" && withGear[3] == "n17";
        foreach (FuiChild ch in c.Children)
            gears &= ch.Gears.Length == 0 || (ch.Gears.Length == 1 && ch.Gears[0].Kind == 0 && !ch.Gears[0].Data.IsEmpty);
        Check(".fui 组件: gearDisplay 落在 n13/n15/n16/n17 且 kind=0（mailItem.xml <gearDisplay>）", gears);

        Check(".fui 组件: mailItem 一条 transition 't0'（mailItem.xml <transition name=\"t0\">）",
            c.Transitions.Length == 1 && c.Transitions[0].Name == "t0" && !c.Transitions[0].Data.IsEmpty);
        Check(".fui 组件: extension=Button 的组件带块 6（mailItem.xml <Button mode=\"Radio\"/>）",
            c.HasExtensionData && c.Item.ObjectType == FuiObjectType.Button);
    }

    /// <summary>对照 VirtualList/Button1.xml：4 个孩子各一条「绑父」关系，边 = width + height。</summary>
    private static void ComponentRelationsToParent(FuiPackage p)
    {
        const string name = ".fui 组件: Button1 四孩子各绑父 width/height（Button1.xml <relation target=\"\">）";
        FuiComponent? c = Comp(p, "rpolb", name);
        if (c == null) return;

        bool ok = c.Children.Length == 4 && c.Relations.Length == 0;   // 关系挂在孩子上，组件自身没有
        foreach (FuiChild ch in c.Children)
        {
            ok &= ch.Relations.Length == 1
                && ch.Relations[0].TargetIndex == -1          // target="" ⇒ 父，编码为 -1
                && ch.Relations[0].Sides.Length == 2
                && ch.Relations[0].Sides[0].Type == FuiRelationType.Width
                && ch.Relations[0].Sides[1].Type == FuiRelationType.Height
                && !ch.Relations[0].Sides[0].UsePercent;
        }
        Check(name, ok);

        // <graph ... touchable="false"> 三条 + <text name="title"> 一条：touchable 默认 true，
        // 写 false 的才是 false。默认值搞反在运行期表现为「整块按钮吃掉点击」。
        Check(".fui 组件: touchable=false 只落在三个 graph 上（Button1.xml touchable 属性）",
            c.Children.Length == 4 && !c.Children[0].Touchable && !c.Children[1].Touchable
            && !c.Children[2].Touchable && c.Children[3].Touchable && c.Children[3].Name == "title");
    }

    /// <summary>对照 ScrollPane/item.xml 的组件级滚动属性（.fui 块 7）。</summary>
    private static void ComponentScrollBlock(FuiPackage p)
    {
        const string name = ".fui 组件: item 滚动块（item.xml overflow/scroll/scrollBarFlags/scrollBar）";
        FuiComponent? c = Comp(p, "qujq2", name);
        if (c == null) return;

        // <component size="720,88" overflow="scroll" scroll="horizontal" scrollBarFlags="648" scrollBar="hidden">
        bool ok = c.SourceWidth == 720 && c.SourceHeight == 88
            && c.Overflow == FuiOverflowType.Scroll
            && c.Scroll != null
            && c.Scroll.ScrollType == 0            // horizontal
            && c.Scroll.ScrollBarDisplay == 3      // hidden
            && c.Scroll.Flags == 648               // scrollBarFlags 原值逐位进 .fui，不重编码
            // 位含义 = fork ScrollPane.Setup 的公式；648 = 8 | 128 | 512
            && c.Scroll.PageMode                   // bit 8
            && c.Scroll.MaskDisabled               // bit 512
            && !c.Scroll.SnapToItem                // bit 2 未置
            && !c.Scroll.DisplayInDemand           // bit 4 未置
            && !c.Scroll.DisplayOnLeft && !c.Scroll.InertiaDisabled
            && !c.Scroll.Floating && !c.Scroll.DontClipMargin;
        Check(name, ok);

        // 块 7 读完必须把位置还回块 0 的读取点：fork 在这里存了 savedPos，漏掉就会把滚动
        // 配置的字节当成 clipSoftness 继续读。
        Check(".fui 组件: 读完块 7 回到块 0 继续（clipSoftness 未被滚动块串味）",
            !c.HasClipSoftness && c.MaskId == -1);

        // 对照 Main.xml：同包另一组件不带滚动 ⇒ Scroll 为 null，不是「全零的滚动配置」。
        FuiComponent? main = Comp(p, "n38w0", ".fui 组件: ScrollPane/Main 可解析");
        Check(".fui 组件: 无 overflow=scroll 的组件 Scroll == null（Main.xml 无 overflow 属性）",
            main != null && main.Overflow == FuiOverflowType.Visible && main.Scroll == null);
    }

    /// <summary>对照 ScrollPane/item.xml 每个孩子的 sidePair：关系边类型逐值。</summary>
    private static void ComponentRelationSideTypes(FuiPackage p)
    {
        const string name = ".fui 组件: item 六条关系的边类型逐值（item.xml sidePair）";
        FuiComponent? c = Comp(p, "qujq2", name);
        if (c == null) return;

        bool ok = c.Children.Length == 7
            // <image id="n3" ...><relation target="" sidePair="width-width,bottom-bottom"/>
            && Sides(c, 1, FuiRelationType.Width, FuiRelationType.Bottom_Bottom)
            // <component id="n9" ...><relation target="" sidePair="left-right"/>
            && Sides(c, 2, FuiRelationType.Left_Right)
            && Sides(c, 3, FuiRelationType.Left_Right)
            // <text id="n4"/><text id="n5"> sidePair="width-width"
            && Sides(c, 4, FuiRelationType.Width)
            && Sides(c, 5, FuiRelationType.Width)
            // <text id="n6"> sidePair="right-right"
            && Sides(c, 6, FuiRelationType.Right_Right)
            // <loader id="n0"> 无 relation
            && c.Children[0].Relations.Length == 0;
        Check(name, ok);
    }

    private static bool Sides(FuiComponent c, int childIndex, params FuiRelationType[] expected)
    {
        FuiChild ch = c.Children[childIndex];
        if (ch.Relations.Length != 1 || ch.Relations[0].TargetIndex != -1) return false;
        FuiRelationSide[] sides = ch.Relations[0].Sides;
        if (sides.Length != expected.Length) return false;
        for (int i = 0; i < expected.Length; i++)
            if (sides[i].Type != expected[i]) return false;
        return true;
    }

    /// <summary>对照 Cooldown/Button1.xml：alpha / visible / Loader 孩子 / 无 src 的原生对象。</summary>
    private static void ComponentChildBasicProps(FuiPackage p)
    {
        const string name = ".fui 组件: Cooldown/Button1 孩子基本属性（Button1.xml 逐属性）";
        FuiComponent? c = Comp(p, "ltiql", name);
        if (c == null) return;

        bool ok = c.SourceWidth == 60 && c.SourceHeight == 60 && c.Children.Length == 4
            // <image id="n5" src="ltiqo" xy="0,0" size="60,60" visible="false"/>
            && c.Children[0].Id == "n5" && !c.Children[0].Visible && c.Children[0].Src == "ltiqo"
            // <loader id="n6" name="icon" xy="0,0" size="60,60"/>——loader 无 src
            && c.Children[1].Type == FuiObjectType.Loader && c.Children[1].Name == "icon"
            && c.Children[1].Src == null && c.Children[1].Visible
            // <image id="n10" name="mask" src="uo8117" xy="3,3" size="54,54" alpha="0.6"/>
            && c.Children[3].Name == "mask" && c.Children[3].X == 3 && c.Children[3].Y == 3
            && c.Children[3].Width == 54 && c.Children[3].Height == 54
            && Math.Abs(c.Children[3].Alpha - 0.6f) < 1e-6f;
        Check(name, ok);

        // 未写的属性走默认值：alpha=1、rotation=0、scale 缺席、blendMode=0、无滤镜、无组。
        FuiChild first = c.Children[0];
        Check(".fui 组件: 孩子默认值（alpha=1 / rot=0 / 无 scale / 无滤镜 / group=-1）",
            first.Alpha == 1f && first.Rotation == 0f && !first.HasScale && !first.HasSkew
            && !first.HasColorFilter && first.BlendMode == 0 && first.GroupId == -1
            && first.Tooltips == null);

        // 跨包引用：同包 src 的 PkgId 必须是 null（不是空串）——M1-20 据此分流 TREF 与本包解析。
        Check(".fui 组件: 同包资源引用的 PkgId 为 null（跨包才写包 id）",
            c.Children[0].PkgId == null && c.Children[3].PkgId == null);
    }

    /// <summary>对照 TextMeshPro/Main.xml：richtext / input=true 各自的 ObjectType。</summary>
    private static void ComponentTextKinds(FuiPackage p)
    {
        const string name = ".fui 组件: RichText / InputText 的类型编号（Main.xml richtext / input=\"true\"）";
        FuiComponent? c = Comp(p, "v0400", name);
        if (c == null) return;

        bool ok = c.Children.Length == 5
            && c.Children[0].Type == FuiObjectType.RichText && c.Children[0].Id == "n0_v040"
            && c.Children[1].Type == FuiObjectType.Text
            && c.Children[2].Type == FuiObjectType.Graph
            && c.Children[3].Type == FuiObjectType.InputText && c.Children[3].Id == "n3_mpsw"
            && c.Children[4].Type == FuiObjectType.Text;
        Check(name, ok);
    }

    /// <summary>
    /// 结构不变量：孩子记录的窗口必须落在组件 rawData 内，组件 rawData 必须落在包内。
    /// 本包把每个孩子裁进自己的窗口正是为了让「孩子的块偏移够不到兄弟的字节」——
    /// 这条断言是那个决定的机器形式。
    /// </summary>
    private static void ComponentSpansStayInsideTheirRecord(FuiPackage p)
    {
        bool ok = true;
        int checkedChildren = 0;
        foreach (FuiItem item in p.Items)
        {
            if (item.Type != FuiItemType.Component) continue;
            if (!FuiComponent.TryParse(p, item, out FuiComponent? c, out _) || c == null) { ok = false; continue; }

            int lo = item.RawData.Offset, hi = lo + item.RawData.Length;
            ok &= lo >= 0 && hi <= p.Bytes.Length;
            foreach (FuiChild ch in c.Children)
            {
                ok &= ch.Data.Offset >= lo && ch.Data.Offset + ch.Data.Length <= hi;
                foreach (FuiGear g in ch.Gears)
                    ok &= g.Data.Offset >= ch.Data.Offset
                       && g.Data.Offset + g.Data.Length <= ch.Data.Offset + ch.Data.Length;
                checkedChildren++;
            }
            foreach (FuiTransition t in c.Transitions)
                ok &= t.Data.Offset >= lo && t.Data.Offset + t.Data.Length <= hi;
        }
        Check($".fui span 嵌套: 孩子/gear/transition 的 span 全部落在各自记录内（{checkedChildren} 个孩子）",
            ok && checkedChildren > 0);
    }

    // ---- ③ 两级块表与字符串表 ---------------------------------------------

    /// <summary>
    /// 两级块表的三种「无此块」出口：偏移为 0（块缺席）、下标 ≥ 段数、块表位置本身越窗。
    /// 三种都必须返回 false **且不移动 position**——调用方靠这个「读不到就走默认值」实现前向兼容。
    /// </summary>
    private static void SeekBoundaries()
    {
        // 手搭一张块表：段数 3、short 偏移、块 0 → +10、块 1 → 0（缺席）、块 2 → +12。
        // 布局：[0]=segCount [1]=useShort [2..7]=三个 short 偏移 [8..]=块体
        byte[] data = new byte[24];
        data[0] = 3;
        data[1] = 1;
        Put16(data, 2, 10);
        Put16(data, 4, 0);
        Put16(data, 6, 12);
        data[10] = 0xAB;   // 块 0 体
        data[12] = 0xCD;   // 块 2 体

        var b = new ByteBuffer(data);
        b.position = 5;
        bool hit = b.Seek(0, 0) && b.position == 10 && b.ReadByte() == 0xAB;
        Check(".fui Seek: 命中块跳到 indexTablePos + 偏移", hit);

        b.position = 5;
        bool absent = !b.Seek(0, 1) && b.position == 5;
        Check(".fui Seek: 偏移为 0 = 块缺席 → false 且 position 不动（前向兼容的出口）", absent);

        b.position = 5;
        bool overIndex = !b.Seek(0, 3) && !b.Seek(0, 99) && b.position == 5;
        Check(".fui Seek: 块下标 ≥ 段数 → false 且 position 不动", overIndex);

        b.position = 5;
        bool badTable = !b.Seek(data.Length, 0) && !b.Seek(data.Length + 1000, 0)
            && !b.Seek(-1, 0) && !b.Seek(0, -1) && b.position == 5;
        Check(".fui Seek: 块表位置/下标越窗 → false 且不越窗读", badTable);

        // 偏移指到窗口外：fork 会把 position 挪出窗口，之后每次读都读到别人的字节。
        byte[] evil = (byte[])data.Clone();
        Put16(evil, 2, 5000);
        var eb = new ByteBuffer(evil);
        eb.position = 7;
        Check(".fui Seek: 跳转目标越窗 → false 且 position 不动（不越窗读）",
            !eb.Seek(0, 0) && eb.position == 7);

        // 子窗口：块表位置相对窗口起点，而不是底层数组起点。
        byte[] host = new byte[8 + data.Length];
        Array.Copy(data, 0, host, 8, data.Length);
        var sub = new ByteBuffer(host, 8, data.Length);
        Check(".fui Seek: 非零 offset 的窗口内块表按窗口相对位置解析",
            sub.Seek(0, 0) && sub.position == 10 && sub.ReadByte() == 0xAB);
    }

    private static void Put16(byte[] d, int at, int v)   // 大端，与 .fui 一致
    {
        d[at] = (byte)((v >> 8) & 0xFF);
        d[at + 1] = (byte)(v & 0xFF);
    }

    /// <summary>ReadS 的两个哨兵与共享表语义。65534/65533 合并即丢「字段缺席 vs 空内容」的区分。</summary>
    private static void StringTableSentinels()
    {
        byte[] data = new byte[8];
        Put16(data, 0, 1);        // 表下标 1
        Put16(data, 2, 65534);    // null
        Put16(data, 4, 65533);    // 空串
        Put16(data, 6, 2);        // 越表长

        var b = new ByteBuffer(data) { stringTable = new string?[] { "zero", "one" } };
        bool ok = b.ReadS() == "one" && b.ReadS() == null && b.ReadS() == string.Empty;
        Check(".fui ReadS: 正常下标取共享表 / 65534 = null / 65533 = 空串（三者互不合并）", ok);

        bool threw = false;
        try { b.ReadS(); } catch (FuiFormatException) { threw = true; }
        Check(".fui ReadS: 下标越表长 = 结构性不符（抛 FuiFormatException，不返回 null 蒙混）", threw);

        var noTable = new ByteBuffer(new byte[] { 0, 0 });
        bool threw2 = false;
        try { noTable.ReadS(); } catch (FuiFormatException) { threw2 = true; }
        Check(".fui ReadS: 无字符串表时取串也走同一门（不 NullReference）", threw2);
    }

    /// <summary>移植件的读数正确性：.fui 全程大端，float 走字节交换路径。</summary>
    private static void ByteBufferBigEndian()
    {
        byte[] d = { 0x12, 0x34, 0x12, 0x34, 0x56, 0x78, 0x3F, 0x80, 0x00, 0x00, 10, 20, 30, 40 };
        var b = new ByteBuffer(d);
        bool ok = b.ReadShort() == 0x1234
            && b.ReadInt() == 0x12345678
            && b.ReadFloat() == 1.0f
            && b.ReadColor().Pack() == ((uint)40 << 24 | (uint)30 << 16 | (uint)20 << 8 | 10);
        Check("ByteBuffer: 大端 short/int/float 与 RGBA8 直读", ok);

        var le = new ByteBuffer(new byte[] { 0x34, 0x12 }) { littleEndian = true };
        Check("ByteBuffer: littleEndian 开关生效（同字节反过来读）", le.ReadShort() == 0x1234);

        Check("ByteBuffer: ReadPath 明确不支持（路径数据随 M2 tween 引擎回归）",
            Throws<NotSupportedException>(() => new ByteBuffer(new byte[8]).ReadPath()));
    }

    /// <summary>窗口边界：越窗读必须抛，Skip/position 越窗本身合法（fork 的调用形态依赖它）。</summary>
    private static void ByteBufferWindow()
    {
        byte[] backing = new byte[32];
        for (int i = 0; i < backing.Length; i++) backing[i] = (byte)(i + 1);
        var b = new ByteBuffer(backing, 8, 4);   // 窗口 = 字节 8..11

        bool ok = b.length == 4 && b.bufferOffset == 8 && b.ReadInt() == 0x090A0B0C;
        Check("ByteBuffer: 窗口只覆盖 [offset, offset+length)", ok);

        // 关键一条：窗口后面还有 20 个字节的底层数组，fork 会照读不误。
        b.position = 2;
        Check("ByteBuffer: 越窗读抛 FuiFormatException（不静默读窗口外的字节）",
            Throws<FuiFormatException>(() => b.ReadInt()));

        b.position = 0;
        b.Skip(1000);
        Check("ByteBuffer: Skip/position 可越窗（合法），读才失败",
            b.position == 1000 && !b.bytesAvailable && Throws<FuiFormatException>(() => b.ReadByte()));

        Check("ByteBuffer: 构造窗口越底层数组即拒（offset/length 二者都查）",
            Throws<ArgumentOutOfRangeException>(() => new ByteBuffer(backing, 30, 10))
            && Throws<ArgumentOutOfRangeException>(() => new ByteBuffer(backing, 40)));
    }

    /// <summary>
    /// fork 的 ReadBuffer 用 <c>new ByteBuffer(_data, _pointer, count)</c> 切子缓冲，漏了 _offset。
    /// 宿主窗口 offset == 0 时看不出来（fork 的 UIPackage 恰好总是 0）；这里就用非零 offset 的
    /// 宿主窗口把它钉住。
    /// </summary>
    private static void ByteBufferSubBufferOffset()
    {
        byte[] backing = new byte[32];
        // 窗口从 8 开始：[8..11] = int 长度 3，[12..14] = 子缓冲内容 0xAA 0xBB 0xCC
        backing[8] = 0; backing[9] = 0; backing[10] = 0; backing[11] = 3;
        backing[12] = 0xAA; backing[13] = 0xBB; backing[14] = 0xCC;
        // 若漏加 _offset，子缓冲会从字节 4 开始读到这三个零：
        backing[4] = 0; backing[5] = 0; backing[6] = 0;

        var host = new ByteBuffer(backing, 8, 12);
        ByteBuffer sub = host.ReadBuffer();
        Check("ByteBuffer: ReadBuffer 子缓冲带上宿主 offset（fork 该处漏加 _offset）",
            sub.bufferOffset == 12 && sub.length == 3
            && sub.ReadByte() == 0xAA && sub.ReadByte() == 0xBB && sub.ReadByte() == 0xCC);

        Check("ByteBuffer: ReadBuffer 的长度前缀越窗即拒（不切出越窗的子窗口）",
            Throws<FuiFormatException>(() =>
            {
                byte[] bad = { 0, 0, 0x7F, 0xFF, 1, 2, 3, 4 };
                new ByteBuffer(bad).ReadBuffer();
            }));
    }

    /// <summary>
    /// fork 的 <c>static byte[] temp</c>（第 33 行）是并行编包下的数据竞争，而大端 .fui 在小端机上
    /// **每个 float 都要走这块暂存**。改成实例字段后，本类不该再有任何静态可变状态——
    /// 用反射钉死比写一条会偶发的多线程用例可靠。
    /// </summary>
    private static void ByteBufferNoStaticState()
    {
        FieldInfo[] statics = typeof(ByteBuffer).GetFields(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        var offenders = new List<string>();
        foreach (FieldInfo f in statics)
            if (!f.IsLiteral && !f.IsInitOnly) offenders.Add(f.Name);
        Check("ByteBuffer: 零静态可变状态（fork 的 static temp[8] 已改实例字段）", offenders.Count == 0);
        if (offenders.Count > 0) Console.WriteLine("     " + string.Join(", ", offenders));
    }

    // ---- ④ 恶意输入 -------------------------------------------------------

    private static void MalformedHeaders(string dir)
    {
        byte[] good = File.ReadAllBytes(Path.Combine(dir, "VirtualList.fui"));

        bool magic = !FuiPackage.TryParse(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, out _, out string d1) && d1.Length > 0;
        bool tiny = !FuiPackage.TryParse(new byte[] { 0x46, 0x47 }, out _, out string d2) && d2.Length > 0;
        bool nul = !FuiPackage.TryParse(null, out _, out string d3) && d3.Length > 0;
        bool empty = !FuiPackage.TryParse(Array.Empty<byte>(), out _, out _);
        Check(".fui 恶意: 魔数不符 / 长度不足 / null / 空数组 → 拒收带诊断",
            magic && tiny && nul && empty);

        byte[] badVersion = (byte[])good.Clone();
        badVersion[4] = 0x7F; badVersion[5] = 0xFF; badVersion[6] = 0xFF; badVersion[7] = 0xFF;
        bool ver = !FuiPackage.TryParse(badVersion, out _, out _);

        byte[] compressed = (byte[])good.Clone();
        compressed[8] = 1;   // 压缩位
        bool comp = !FuiPackage.TryParse(compressed, out _, out string dc) && dc.Contains("压缩");
        Check(".fui 恶意: 荒谬版本号 / 压缩位 → 拒收（不是「先解析再崩」）", ver && comp);
    }

    /// <summary>
    /// 逐长度截断：从 1 字节到全长，每个前缀都必须得到「成功」或「false + 诊断」，永不抛。
    /// 截断是最常见的部署事故形态（传输中断、AssetBundle 半包）。
    /// </summary>
    private static void MalformedTruncation(string dir)
    {
        int cases = 0, escaped = 0;
        foreach (string name in new[] { "VirtualList", "Cooldown", "ScrollPane", "TextMeshPro" })
        {
            byte[] full = File.ReadAllBytes(Path.Combine(dir, name + ".fui"));
            for (int len = 1; len < full.Length; len += 7)
            {
                byte[] cut = new byte[len];
                Array.Copy(full, cut, len);
                cases++;
                try
                {
                    if (FuiPackage.TryParse(cut, out FuiPackage? p, out _) && p != null)
                        foreach (FuiItem it in p.Items)
                            if (it.Type == FuiItemType.Component)
                                FuiComponent.TryParse(p, it, out _, out _);
                }
                catch (Exception) { escaped++; }
            }
        }
        Check($".fui 恶意: 逐长度截断 {cases} 例全部收敛为拒收/降级（零异常逃逸）", escaped == 0 && cases > 400);
    }

    /// <summary>单字节翻位扫描：每一位的破坏都不该变成异常，只能变成拒收或一个内容有出入的包。</summary>
    private static void MalformedBitFlips(string dir)
    {
        byte[] full = File.ReadAllBytes(Path.Combine(dir, "Cooldown.fui"));
        int cases = 0, escaped = 0, rejected = 0;
        for (int i = 0; i < full.Length; i += 3)
        {
            foreach (byte mask in new byte[] { 0x01, 0x80, 0xFF })
            {
                byte[] bad = (byte[])full.Clone();
                bad[i] ^= mask;
                cases++;
                try
                {
                    if (!FuiPackage.TryParse(bad, out FuiPackage? p, out _) || p == null) { rejected++; continue; }
                    foreach (FuiItem it in p.Items)
                    {
                        if (it.Type == FuiItemType.Component)
                            FuiComponent.TryParse(p, it, out _, out _);
                    }
                }
                catch (Exception) { escaped++; }
            }
        }
        Check($".fui 恶意: 单字节翻位 {cases} 例零异常逃逸（其中 {rejected} 例被拒收）",
            escaped == 0 && cases > 3000 && rejected > 0);
    }

    /// <summary>
    /// 组件 rawData 单独截断：包仍然可用，只有那一个组件解析失败——
    /// 「拒载」与「降级」之间没有第三种静默状态，这条在 .fui 侧的对应物就是它。
    /// </summary>
    private static void MalformedComponentPayload(string dir)
    {
        byte[] full = File.ReadAllBytes(Path.Combine(dir, "VirtualList.fui"));
        if (!FuiPackage.TryParse(full, out FuiPackage? p, out _) || p == null
            || !p.TryGetItemById("c8s20", out FuiItem? main))
        {
            Check(".fui 恶意: 组件负载损坏只影响该组件", false);
            return;
        }

        // 把 Main 的显示列表块起点写成一片 0xFF：块表偏移全越窗。
        byte[] bad = (byte[])full.Clone();
        for (int i = main.RawData.Offset; i < main.RawData.Offset + Math.Min(64, main.RawData.Length); i++)
            bad[i] = 0xFF;

        bool pkgOk = FuiPackage.TryParse(bad, out FuiPackage? p2, out _) && p2 != null;
        bool mainFails = false, othersOk = true;
        if (pkgOk && p2 != null)
        {
            foreach (FuiItem it in p2.Items)
            {
                if (it.Type != FuiItemType.Component) continue;
                bool parsed = FuiComponent.TryParse(p2, it, out FuiComponent? c, out string diag);
                if (it.Id == "c8s20") mainFails = !parsed || c == null || c.Children.Length != 8;
                else othersOk &= parsed && c != null && diag.Length == 0;
            }
        }
        Check(".fui 恶意: 组件负载损坏只影响该组件（包与其余组件照常可用）",
            pkgOk && mainFails && othersOk);

        // 非组件条目走组件解析 = 调用方错误，也必须是 false + 诊断而不是异常。
        bool wrongKind = p.TryGetItemById("c8s21", out FuiItem? img)
            && !FuiComponent.TryParse(p, img, out _, out string d) && d.Contains("Image");
        Check(".fui 恶意: 拿图片条目当组件解析 → false + 诊断", wrongKind);
    }

    /// <summary>
    /// 可选回归扫描：<c>FAIRYNEXT_FUI_SWEEP=&lt;目录&gt;</c> 时把目录下所有 *_fui.bytes / *.fui
    /// 全解析一遍（本机 oracle checkout 下 = 30 个包 / 216 个组件）。未设环境变量不产生用例。
    /// </summary>
    private static void OracleSweep()
    {
        string? sweep = Environment.GetEnvironmentVariable("FAIRYNEXT_FUI_SWEEP");
        if (string.IsNullOrEmpty(sweep) || !Directory.Exists(sweep)) return;

        var files = new List<string>();
        files.AddRange(Directory.GetFiles(sweep, "*_fui.bytes"));
        files.AddRange(Directory.GetFiles(sweep, "*.fui"));
        files.Sort(StringComparer.Ordinal);

        int packages = 0, components = 0;
        var failures = new List<string>();
        foreach (string f in files)
        {
            if (!FuiPackage.TryParse(File.ReadAllBytes(f), out FuiPackage? p, out string diag) || p == null)
            {
                failures.Add($"{Path.GetFileName(f)}: {diag}");
                continue;
            }
            packages++;
            foreach (FuiItem it in p.Items)
            {
                if (it.Type != FuiItemType.Component) continue;
                if (FuiComponent.TryParse(p, it, out _, out string cd)) components++;
                else failures.Add($"{Path.GetFileName(f)}/{it.Name}: {cd}");
            }
        }
        Check($".fui 扫描: {sweep} 下 {packages} 个包 / {components} 个组件全部解析成功", failures.Count == 0);
        foreach (string s in failures) Console.WriteLine("     " + s);
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
        catch (Exception) { return false; }
    }
}
