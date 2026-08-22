using System.Collections.Generic;
using FairyNext.Compiler.Fui;
using FairyNext.Core;
using FairyNext.Core.Layout;
using FairyNext.Core.Rendering;
using FairyNext.Core.Text;
using FairyNext.Numerics;

namespace FairyNext.Compiler.Shape;

/// <summary>跨组件共享的定形上下文（字体注册 + 图集纹理 id）。</summary>
internal sealed class ShapeContext
{
    /// <summary>共享度量账（无字体注册为 null；face id 跨组件稳定——样式等值比较依赖它）。</summary>
    public GlyphMetricsTable? Metrics;

    /// <summary>字体名 → face id（首条 = 回退，见 <see cref="CompileFont"/>）。</summary>
    public readonly Dictionary<string, ushort> FacesByName = new Dictionary<string, ushort>(StringComparer.Ordinal);

    /// <summary>回退 face（<see cref="Metrics"/> 非 null 时有效）。</summary>
    public ushort FallbackFace;

    /// <summary>图集条目 id → 段键纹理 id（本包内分配，M1-20b 冻结 TREF 时同源）。</summary>
    public readonly Dictionary<string, TexId> AtlasTex = new Dictionary<string, TexId>(StringComparer.Ordinal);

    /// <summary>
    /// 同一批登记的**分配序**列表。字典的枚举序在 .NET 里不是契约（无删除时碰巧是插入序，
    /// 但那是实现细节），而 TREF 段的记录序进 blob 字节 ⇒ 进 golden ——顺序必须由我们自己拿住。
    /// </summary>
    public readonly List<KeyValuePair<string, TexId>> AtlasOrder = new List<KeyValuePair<string, TexId>>();

    public bool HasFonts => Metrics != null;
}

/// <summary>
/// 建树器（M1-20a）：一个 <see cref="FuiComponent"/> → 一个无头运行时世界（承诺 8：编译 = 对着
/// 真运行时跑一遍再冻结，这里就是那个「真运行时」——树/相位机/布局引擎/文本系统全是运行期同款，
/// 属性一律走正常 setter，**编译器也不绕写门**）。
///
/// 五步，顺序是契约：
///  ① 建世界（表/失效协议/内核/布局引擎 + 五通道吸收排水器 + 可选文本系统）；
///  ② 建节点 + GGroup 真节点化（<see cref="GroupPlan"/> 归属 → 组员重挂为组的真孩子，
///     坐标换到组空间）+ pivotAsAnchor 原点换算（authored x/y 是锚点 ⇒ 原点 = 锚点 − pivot×尺寸，
///     fork GObject.xMin 的 getter 语义烘死进 authored）；
///  ③ 挂内容（图片叶/纯矩形 Graph/文本；未进 M1 面的形态按有声原则出 FGM 诊断不编）；
///  ④ relation → 约束编译（<see cref="ConstraintCompiler"/>，拒环复用 Seal）+ 锚定注入 + Arm——
///     **resolved 槽在此全部预解出来**（Arm/RegisterMeasured 布防即分配），P5 里不再有迟到分配；
///  ⑤ 编译期 P5 + 度量：真跑 <see cref="UiKernel.Tick"/>（幂等门 + 差分神谕全程开启），收敛后
///     执法三道门——未收敛 FGM901、门红或 <see cref="LayoutStats.LateSlotAllocs"/> 非零 FGM902。
/// </summary>
internal static class TreeBuilder
{
    private sealed class RenderSink : IChannelDrain
    {
        public Ch Consumes => Ch.Content | Ch.Transform | Ch.Color | Ch.Visible | Ch.Structure;
        public void Drain(ref FrameContext ctx, Ch channel, ReadOnlySpan<NodeHandle> queue) { }
    }

    /// <summary>定形一个组件。失败（约束环等 Error 级）返回 null，原因已入 <paramref name="diag"/>。</summary>
    public static ShapedComponent? Build(FuiPackage pkg, FuiItem item, FuiComponent comp,
        ShapeContext ctx, ushort treeId, CompileDiagnostics diag)
    {
        string site = item.Name ?? item.Id;

        // ── ① 世界 ──────────────────────────────────────────────────────────
        var table = new NodeTable(treeId);
        var inval = new Invalidation(table);
        var kernel = new UiKernel(table, inval);
        var layout = new LayoutEngine(kernel) { IdempotenceGate = true, DifferentialGate = true };
        layout.Attach();
        inval.Register(new RenderSink());
        var content = new ContentTable();
        TextSystem? text = null;
        if (ctx.HasFonts)
        {
            text = new TextSystem(kernel, ctx.Metrics!, new GlyphStore(), content);
            text.Attach(layout);
        }

        // 组件根是**世界树根的孩子**，不是世界树根本身（M1-22 修正）。理由有两条，第二条是硬的：
        //  ① 形态对齐运行期——一个组件实例在真宿主里永远是某棵树里的一棵子树，
        //     编译世界让它当树根就是让编译面和运行面在结构上不同源；
        //  ② 模板的节点区间因此**不从槽 1 起**（根在槽 2），于是 NODE 拓扑列的相对化
        //     （`FgbFreezer.Relativize`）在真语料上不再是数值恒等——M1-20b 记在案的那条
        //     「摘掉相对化调用不可观测」的存活变异，从此有门管得住。
        NodeHandle compRoot = table.CreateNode(NodeType.Component, 0);
        table.AddChild(table.Root, compRoot);
        table.SetSize(compRoot, comp.SourceWidth, comp.SourceHeight);

        // ── ② 节点与结构 ────────────────────────────────────────────────────
        FuiChild[] children = comp.Children;
        int n = children.Length;
        var nodes = new NodeHandle[n + 1];
        nodes[0] = compRoot;
        var editorIds = new string?[n + 1];
        var byEditorId = new Dictionary<string, ushort>(n, StringComparer.Ordinal);

        var groupOf = new int[n];
        var isGroup = new bool[n];
        for (int i = 0; i < n; i++)
        {
            groupOf[i] = children[i].GroupId;
            isGroup[i] = children[i].Type == FuiObjectType.Group;
        }
        int[] parentOf = GroupPlan.Plan(groupOf, isGroup, site, diag);

        for (int i = 0; i < n; i++)
        {
            FuiChild c = children[i];
            nodes[i + 1] = table.CreateNode(TypeOf(c.Type), (ushort)(i + 1));
            editorIds[i + 1] = c.Id;
            if (c.Id != null && !byEditorId.ContainsKey(c.Id)) byEditorId.Add(c.Id, (ushort)(i + 1));
        }

        // 组件空间几何（原点换算在此，一次算清再减父原点得局部坐标）。
        var sizeW = new float[n + 1];
        var sizeH = new float[n + 1];
        var originX = new float[n + 1];
        var originY = new float[n + 1];
        var anchored = new bool[n + 1];
        var pivotX = new float[n + 1];
        var pivotY = new float[n + 1];
        for (int i = 0; i < n; i++)
        {
            FuiChild c = children[i];
            DefaultSize(pkg, c, out float w, out float h, site, diag);
            if (c.HasSize) { w = c.Width; h = c.Height; }
            sizeW[i + 1] = w;
            sizeH[i + 1] = h;
            anchored[i + 1] = c.HasPivot && c.PivotAsAnchor;
            pivotX[i + 1] = c.HasPivot ? c.PivotX : 0f;
            pivotY[i + 1] = c.HasPivot ? c.PivotY : 0f;
            // pivotAsAnchor 原点换算：.fui 的 x/y 是锚点坐标（fork 把它存进 _x、由 xMin getter
            // 每次读时换算）；本运行时 position 恒为原点（机制⑪），换算烘死在建树期。
            originX[i + 1] = anchored[i + 1] ? c.X - pivotX[i + 1] * w : c.X;
            originY[i + 1] = anchored[i + 1] ? c.Y - pivotY[i + 1] * h : c.Y;
        }

        for (int i = 0; i < n; i++)
        {
            int p = parentOf[i];
            table.AddChild(nodes[p < 0 ? 0 : p + 1], nodes[i + 1]);
        }

        for (int i = 0; i < n; i++)
        {
            FuiChild c = children[i];
            NodeHandle h = nodes[i + 1];
            int p = parentOf[i];
            float px = p < 0 ? 0f : originX[p + 1];
            float py = p < 0 ? 0f : originY[p + 1];

            table.SetSize(h, sizeW[i + 1], sizeH[i + 1]);
            table.SetPosition(h, originX[i + 1] - px, originY[i + 1] - py);

            bool group = isGroup[i];
            if (c.HasScale && !group) table.SetScale(h, c.ScaleX, c.ScaleY);
            if (c.Rotation != 0f && !group) table.SetRotation(h, c.Rotation * (MathF.PI / 180f));
            if (c.HasSkew && !group)
            {
                // 双值 skew 在 M1 归一为单值（水平剪切 = SkewX）；SkewY 无列，有声不编。
                if (c.SkewX != 0f) table.SetSkew(h, c.SkewX * (MathF.PI / 180f));
                if (c.SkewY != 0f)
                    diag.Add(FgmCodes.ContentNotInM1, FgmSeverity.Info, Site(site, c),
                        "skewY=" + c.SkewY + " 未进 M1 面（单值剪切列，双值需求出现前不加列）");
            }
            // pivot 对 Group 也写：不变量 9 的组纯度管的是 scale/rotation/contentRef，pivot 不在其列，
            // 且在恒等基（组无 scale/rotation/skew）下 LocalMatrix 里 pivot 项自相消 ⇒ 写它零影响。
            // 必须写：原点换算（上面用 c.PivotX）与锚定算子的修正系数（求值现场读 pivot 列）是同一个
            // 数，列上缺一份就会分叉——fork 的 GGroup 同样继承 GObject 的 pivotAsAnchor 语义。
            if (c.HasPivot) table.SetPivot(h, c.PivotX, c.PivotY);
            if (c.Alpha != 1f) table.SetAlpha(h, c.Alpha);
            if (!c.Visible) table.SetVisible(h, false);
            if (!c.Touchable) table.SetTouchable(h, false);
            if (c.Grayed) table.SetGrayed(h, true);

            // ── ③ 内容 ──────────────────────────────────────────────────────
            AttachContent(pkg, c, h, ctx, table, content, layout, text, site, diag);
        }

        // ── ④ 约束编译 ──────────────────────────────────────────────────────
        if (comp.Relations.Length > 0)
            diag.Add(FgmCodes.OwnerRelationSkipped, FgmSeverity.Warning, site,
                "组件自身的 " + comp.Relations.Length + " 条关系未编译（M1 面：容器由外层图或 authored 定形）");

        var cc = new ConstraintCompiler(n + 1);
        for (int i = 0; i < n; i++)
        {
            FuiChild c = children[i];
            for (int r = 0; r < c.Relations.Length; r++)
            {
                FuiRelation rel = c.Relations[r];
                ushort dstLocal = (ushort)(i + 1);
                ushort srcLocal;
                if (rel.TargetIndex == -1) srcLocal = 0;
                else if (rel.TargetIndex >= 0 && rel.TargetIndex < n) srcLocal = (ushort)(rel.TargetIndex + 1);
                else
                {
                    diag.Add(FgmCodes.RelationTargetInvalid, FgmSeverity.Warning, Site(site, c),
                        "关系目标下标 " + rel.TargetIndex + " 越界（孩子数 " + n + "），该条跳过");
                    continue;
                }
                if (srcLocal == dstLocal)
                {
                    diag.Add(FgmCodes.RelationTargetInvalid, FgmSeverity.Warning, Site(site, c),
                        "关系目标指向自己，该条跳过");
                    continue;
                }
                // 坐标空间：真节点化后节点边在**父空间**求值。dst 与 src 的有效父相同 ⇒ 同空间。
                // 局部 0（组件根）的边在其子空间给出（ReadSrcEdge 的容器分支），对根空间的孩子同系。
                int dstParent = parentOf[i];
                int srcParent = srcLocal == 0 ? -1 : parentOf[srcLocal - 1];
                cc.AddRelation(dstLocal, srcLocal, rel.Sides, dstParent == srcParent, Site(site, c), diag);
            }
        }
        int anchorPins = cc.InjectAnchorPins(anchored, table, nodes);

        ConstraintGraph? graph = null;
        if (cc.OpCount > 0)
        {
            ConstraintGraphResult sealResult = cc.Seal();
            if (!sealResult.Ok)
            {
                if (sealResult.CyclePath.Length > 0)
                    diag.Add(FgmCodes.CycleRejected, FgmSeverity.Error, site,
                        "约束环拒绝：" + CyclePathText(sealResult.CyclePath, editorIds)
                        + "（" + sealResult.Error + "）");
                else
                    diag.Add(FgmCodes.ConstraintInvalid, FgmSeverity.Error, site, sealResult.Error);
                return null;
            }
            graph = sealResult.Graph;
            layout.Arm(graph!, nodes);
        }

        // ── ⑤ 编译期 P5 + 度量（真跑相位机；门全程开启）────────────────────
        var time = FrameTime.First(1f / 60f, 1f / 60f);
        int ticks = 0;
        do
        {
            kernel.Tick(in time);
            time = time.Step(1f / 60f, 1f / 60f);
            ticks++;
        }
        while (layout.Stats.PendingWork && ticks < 5);

        LayoutStats stats = layout.Stats;
        if (stats.PendingWork)
        {
            diag.Add(FgmCodes.ShapeNotConverged, FgmSeverity.Error, site,
                "编译期 P5 在 " + ticks + " 帧后仍有余活（反向依赖链未被静态断开）：" + stats.LastOverflowNote);
            return null;
        }
        if (stats.IdempotenceFailures != 0 || stats.DifferentialFailures != 0 || stats.LateSlotAllocs != 0)
        {
            diag.Add(FgmCodes.ShapeGateRed, FgmSeverity.Error, site,
                "编译期布局门红：idempotence=" + stats.IdempotenceFailures
                + " differential=" + stats.DifferentialFailures
                + " lateSlotAllocs=" + stats.LateSlotAllocs
                + "（编译期布局写集已在布防期定死，迟到分配/门红即编译器 bug）"
                + (stats.LastGateError.Length == 0 ? "" : " —— " + stats.LastGateError));
            return null;
        }

        var locals = new LocalIdMap(nodes, editorIds, byEditorId);
        return new ShapedComponent(item, comp, kernel, layout, text, content, locals, graph, anchorPins, ticks);
    }

    private static string Site(string component, FuiChild c) => component + "/" + (c.Id ?? c.Name ?? "?");

    private static string CyclePathText(ushort[] path, string?[] editorIds)
    {
        var parts = new string[path.Length];
        for (int i = 0; i < path.Length; i++)
        {
            ushort local = path[i];
            parts[i] = local < editorIds.Length && editorIds[local] != null
                ? editorIds[local]! : (local == 0 ? "<root>" : "#" + local);
        }
        return string.Join(" → ", parts);
    }

    private static ushort TypeOf(FuiObjectType t)
    {
        switch (t)
        {
            case FuiObjectType.Image: return NodeType.Image;
            case FuiObjectType.MovieClip: return NodeType.Image;    // 帧动画的静态形 = 图（内容 M2）
            case FuiObjectType.Swf: return NodeType.Image;
            case FuiObjectType.Graph: return NodeType.Shape;
            case FuiObjectType.Loader: return NodeType.Loader;
            case FuiObjectType.Loader3D: return NodeType.Loader;
            case FuiObjectType.Group: return NodeType.Group;
            case FuiObjectType.Text: return NodeType.Text;
            case FuiObjectType.RichText: return NodeType.Text;
            case FuiObjectType.InputText: return NodeType.Text;
            case FuiObjectType.List: return NodeType.List;
            default: return NodeType.Component;                     // Component 与全部扩展形态
        }
    }

    /// <summary>无显式尺寸的默认值 = 引用条目的源尺寸（fork sourceWidth/sourceHeight 语义）。</summary>
    private static void DefaultSize(FuiPackage pkg, FuiChild c, out float w, out float h,
        string site, CompileDiagnostics diag)
    {
        w = 0f;
        h = 0f;
        if (c.Src == null) return;
        if (c.PkgId != null)
        {
            if (!c.HasSize)
                diag.Add(FgmCodes.ResourceUnresolved, FgmSeverity.Info, Site(site, c),
                    "跨包引用 " + c.PkgId + "/" + c.Src + " 的源尺寸不可得（默认 0，装载期 M1-22 回填）");
            return;
        }
        if (pkg.TryGetItemById(c.Src, out FuiItem? src))
        {
            w = src.Width;
            h = src.Height;
        }
        else if (!c.HasSize)
            diag.Add(FgmCodes.ResourceUnresolved, FgmSeverity.Warning, Site(site, c),
                "资源引用 '" + c.Src + "' 在包内不存在（默认尺寸 0）");
    }

    // ────────────────────────────────────────────────────────────────────────
    // 内容挂载（③）：进 M1 面的编、不进的有声不编（机制 4：要么正确、要么明确不显示并计数）
    // ────────────────────────────────────────────────────────────────────────

    private static void AttachContent(FuiPackage pkg, FuiChild c, NodeHandle h, ShapeContext ctx,
        NodeTable table, ContentTable content, LayoutEngine layout, TextSystem? text,
        string site, CompileDiagnostics diag)
    {
        try
        {
            switch (c.Type)
            {
                case FuiObjectType.Image:
                    AttachImage(pkg, c, h, ctx, table, content, site, diag);
                    break;
                case FuiObjectType.Graph:
                    AttachGraph(c, h, table, content, site, diag);
                    break;
                case FuiObjectType.Text:
                case FuiObjectType.RichText:
                case FuiObjectType.InputText:
                    AttachText(c, h, ctx, layout, text, site, diag);
                    break;
                case FuiObjectType.MovieClip:
                case FuiObjectType.Swf:
                    diag.Add(FgmCodes.ContentNotInM1, FgmSeverity.Info, Site(site, c),
                        c.Type + " 内容未进 M1 编译面（帧表/播放随 M2；几何已建）");
                    break;
                case FuiObjectType.Loader:
                case FuiObjectType.Loader3D:
                    diag.Add(FgmCodes.ContentNotInM1, FgmSeverity.Info, Site(site, c),
                        "Loader 内容是装载期动态物（url 绑定随 M1-22/M2；几何已建）");
                    break;
                // Group / Component / List / 扩展形态：本级无叶内容（嵌套组件的实例化归 M1-22）。
            }
        }
        catch (FuiFormatException ex)
        {
            // 类型专属块读坏只废内容不废节点：几何已经过正常 setter 落好。
            diag.Add(FgmCodes.ChildBlockRejected, FgmSeverity.Warning, Site(site, c),
                "类型专属块读取失败（内容跳过）：" + ex.Message);
        }
    }

    private static void AttachImage(FuiPackage pkg, FuiChild c, NodeHandle h, ShapeContext ctx,
        NodeTable table, ContentTable content, string site, CompileDiagnostics diag)
    {
        if (c.Src == null) return;
        if (c.PkgId != null)
        {
            diag.Add(FgmCodes.ResourceUnresolved, FgmSeverity.Info, Site(site, c),
                "跨包图片 " + c.PkgId + "/" + c.Src + " 的 UV 归装载期 PTCH（M1-22），本级不编内容");
            return;
        }
        if (!pkg.TryGetItemById(c.Src, out FuiItem? item) || !pkg.TryGetSprite(c.Src, out FuiSprite? sp))
        {
            diag.Add(FgmCodes.ResourceUnresolved, FgmSeverity.Warning, Site(site, c),
                "图片条目/sprite '" + c.Src + "' 缺失，内容不编");
            return;
        }
        float aw = sp.Atlas.Width;
        float ah = sp.Atlas.Height;
        if (!(aw > 0f) || !(ah > 0f))
        {
            diag.Add(FgmCodes.ContentNotInM1, FgmSeverity.Info, Site(site, c),
                "图集 '" + sp.Atlas.Id + "' 无尺寸（UV 无法归一），内容不编");
            return;
        }
        if (sp.Rotated)
        {
            diag.Add(FgmCodes.ContentNotInM1, FgmSeverity.Info, Site(site, c),
                "旋转入图集的 UV 变换未进 M1 发射面，内容不编");
            return;
        }
        if (sp.OffsetX != 0 || sp.OffsetY != 0 || sp.OriginalWidth != sp.RectWidth || sp.OriginalHeight != sp.RectHeight)
        {
            diag.Add(FgmCodes.ContentNotInM1, FgmSeverity.Info, Site(site, c),
                "裁白偏移（trim）未进 M1 发射面，内容不编");
            return;
        }
        if (!ctx.AtlasTex.TryGetValue(sp.Atlas.Id, out TexId tex))
        {
            diag.Add(FgmCodes.ResourceUnresolved, FgmSeverity.Warning, Site(site, c),
                "图集 '" + sp.Atlas.Id + "' 未登记纹理 id，内容不编");
            return;
        }

        var uv = new Vector4(sp.RectX / aw, sp.RectY / ah, (sp.RectX + sp.RectWidth) / aw, (sp.RectY + sp.RectHeight) / ah);
        SpriteRegion region = item.HasScale9Grid
            ? SpriteRegion.Sliced(uv, sp.RectWidth, sp.RectHeight,
                new Vector4(item.Scale9GridX, item.Scale9GridY, item.Scale9GridWidth, item.Scale9GridHeight))
            : SpriteRegion.Full(uv, sp.RectWidth, sp.RectHeight);
        region.TileGridIndice = item.TileGridIndice;      // 平铺拒发在 Extract 有声（DegradeKind 计数）
        region.ScaleByTile = item.ScaleByTile;

        if (c.HasColorFilter)
            diag.Add(FgmCodes.ContentNotInM1, FgmSeverity.Info, Site(site, c), "颜色滤镜随 M2-12，忽略");

        LeafSpec spec = LeafSpec.Image(tex, in region, 0xFFFFFFFFu, BlendOf(c.BlendMode, Site(site, c), diag));
        table.SetContentRef(h, content.AddLeaf(in spec));
    }

    /// <summary>
    /// fork BlendMode（BlendMode.cs @ oracle 08a2d56：Normal 0 / None 1 / Add 2 / Multiply 3 /
    /// Screen 4 / Erase 5 / Mask 6 / Below 7 / Off 8 / One_OneMinusSrcAlpha 9 / Custom1-3 10-12）
    /// → 段键混合类别。M1 段键只有 Normal/Add/Multiply/Screen 四类，**其余回退 Normal 但有声**
    /// （机制 4：要么正确、要么明确不显示并计数——静默回退会让编译产物画错且无信号）。
    /// </summary>
    private static BlendClass BlendOf(byte fuiBlend, string site, CompileDiagnostics diag)
    {
        switch (fuiBlend)
        {
            case 0: return BlendClass.Normal;
            case 2: return BlendClass.Add;
            case 3: return BlendClass.Multiply;
            case 4: return BlendClass.Screen;
            default:
                diag.Add(FgmCodes.ContentNotInM1, FgmSeverity.Info, site,
                    "BlendMode=" + fuiBlend + " 不在 M1 段键的四类里（Normal/Add/Multiply/Screen），回退 Normal");
                return BlendClass.Normal;
        }
    }

    /// <summary>Graph 块 5（fork GGraph.Setup_BeforeAdd 同序）：M1 面只编无线框直角矩形 = 纯色叶。</summary>
    private static void AttachGraph(FuiChild c, NodeHandle h, NodeTable table, ContentTable content,
        string site, CompileDiagnostics diag)
    {
        ByteBuffer b = c.Open();
        if (!b.Seek(0, 5)) return;
        int type = b.ReadByte();
        if (type == 0) return;                                     // 空图形：不产渲染单元

        int lineSize = b.ReadInt();
        b.ReadColor();                                             // lineColor（M1 不编线框）
        Color32 fill = b.ReadColor();
        bool rounded = b.ReadBool();
        if (type != 1 || lineSize != 0 || rounded)
        {
            diag.Add(FgmCodes.ContentNotInM1, FgmSeverity.Info, Site(site, c),
                "Graph 形态（type=" + type + " line=" + lineSize + (rounded ? " rounded" : "")
                + "）未进 M1 面，内容不编");
            return;
        }
        LeafSpec spec = LeafSpec.Solid(fill.Pack());
        table.SetContentRef(h, content.AddLeaf(in spec));
    }

    /// <summary>文本块 5/6（fork GTextField.Setup_BeforeAdd / Setup_AfterAdd 同序）。</summary>
    private static void AttachText(FuiChild c, NodeHandle h, ShapeContext ctx,
        LayoutEngine layout, TextSystem? text, string site, CompileDiagnostics diag)
    {
        ByteBuffer b = c.Open();
        if (!b.Seek(0, 5)) return;

        string? font = b.ReadS();
        short fontSize = b.ReadShort();
        Color32 color = b.ReadColor();
        byte align = b.ReadByte();
        byte valign = b.ReadByte();
        b.ReadShort();                                             // lineSpacing（M1 单 style 面无列）
        b.ReadShort();                                             // letterSpacing
        bool ubb = b.ReadBool();
        byte autoSize = b.ReadByte();                              // 0 None / 1 Both / 2 Height / 3 Shrink / 4 Ellipsis
        b.ReadBool();                                              // underline
        b.ReadBool();                                              // italic
        b.ReadBool();                                              // bold
        bool singleLine = b.ReadBool();

        string? str = null;
        if (b.Seek(0, 6)) str = b.ReadS();

        if (text == null)
        {
            diag.Add(FgmCodes.FontUnmapped, FgmSeverity.Warning, Site(site, c),
                "无字体注册（CompileOptions.Fonts 为空），文本只有几何、无内容与度量");
            return;
        }
        ushort face;
        if (string.IsNullOrEmpty(font)) face = ctx.FallbackFace;   // 编辑器默认字体 → 回退
        else if (font!.StartsWith("ui://", StringComparison.Ordinal))
        {
            diag.Add(FgmCodes.FontUnmapped, FgmSeverity.Warning, Site(site, c),
                "位图字体 " + font + " 未进 M1 面（预烘/位图页归 M2-09），文本只有几何");
            return;
        }
        else if (!ctx.FacesByName.TryGetValue(font, out face))
        {
            diag.Add(FgmCodes.FontUnmapped, FgmSeverity.Warning, Site(site, c),
                "字体 '" + font + "' 无映射（机制 13：font-map 缺失 = 编译诊断），回退到默认注册字体");
            face = ctx.FallbackFace;
        }
        if (ubb)
            diag.Add(FgmCodes.ContentNotInM1, FgmSeverity.Info, Site(site, c),
                "UBB 富文本按纯文本排（RichRun 归 M2-08）");
        if (autoSize == 3)
            diag.Add(FgmCodes.ContentNotInM1, FgmSeverity.Info, Site(site, c),
                "autoSize=Shrink 未进 M1 面（缩放适配需第二把尺的迭代求解，归 M2-08），按不自适应排");

        var style = new TextStyle
        {
            FaceId = face,
            SizePx = fontSize > 0 ? fontSize : 12f,
            Color = color.Pack(),
            HAlign = align <= 2 ? (TextHAlign)align : TextHAlign.Left,
            VAlign = valign <= 2 ? (TextVAlign)valign : TextVAlign.Top,
            Wrap = !singleLine,
            Overflow = autoSize == 4 ? TextOverflow.Ellipsis : TextOverflow.Visible,
        };
        bool autoW = autoSize == 1;
        bool autoH = autoSize == 1 || autoSize == 2;
        text.AttachText(h, str ?? string.Empty, in style, autoW, autoH);
        if (autoW || autoH) layout.RegisterMeasured(h, autoW, autoH);
    }
}
