using System.Reflection;
using System.Runtime.InteropServices;
using FairyNext.AbiGen;
using FairyNext.Backend.Mock;
using FairyNext.Core;
using FairyNext.Core.Rendering;
using FairyNext.Numerics;

namespace FairyNext.Tests;

/// <summary>
/// M1-10 mock 后端 + 参考光栅用例（Program 的 partial 分片）。
/// 逐条对齐 docs/architecture.md：
/// 「平面三 · 渲染平面」不变量 1 ABI 定长 / 2 栅栏无限盒（类型上不可表达）/ 5 预算溢出必有声 /
/// 6 axis-aligned 位一致性 / 10 序派生只随 structEpoch / 11 fence 回收与 use-after-free /
/// 13 零脏帧合法性 / 14 离屏 pass 序 / 16 槽写等值切断；
/// 「平面六 · 验证平面」不变量 13 参考光栅三规则、GateReport 七字段零断言、规范化流哈希。
/// </summary>
public static partial class Program
{
    private static void MockBackendSuite()
    {
        // 接口契约
        NullBackendIsTheNoGpuContract();
        AbiStructsMatchGeneratedOffsets();
        RunRecordHasNoBoundsField();
        FrameBracketRejectsOutsideUploads();
        OffscreenPassMustPrecedeMainSurface();
        OffscreenDependencyOrderIsInnerFirst();
        LegalPassOrderIsSilent();
        PhaseProbeCatchesUploadOutsideDrain();

        // 留痕与快照
        MockRecordsEveryCall();
        SnapshotMirrorsWhatWasUploaded();
        SnapshotIsACopyNotAView();
        UseAfterFreeIsLoud();
        FenceQueueDepthIsCounted();

        // 契约执法
        SlotWriteReVerifiesAxisAlignedBit();
        RedundantSlotWriteIsCounted();
        RelabelRequiresStructEpochChange();
        ReservedBitsMustBeZero();
        ZeroDirtyFrameSkipsPresent();
        ZeroDirtyFrameWithUploadsIsLoud();
        FrameIdMismatchIsLoud();

        // GateReport 七字段
        GateReportPassIsAllZero();
        GateReportHasExactlySevenCounters();
        GateReportCountsBudgetOverflow();
        GateReportDemandsALadderEvent();
        GateReportMergesKernelAndBackend();

        // 参考光栅（三规则）
        RasterGoldenSolidRect();
        RasterGoldenIntegerBlend();
        RasterGoldenGrayedLuminance();
        RasterHonoursClipWindow();
        RasterSamplesTextureByUv();
        RasterIsBitIdenticalAcrossRuns();
        RasterIgnoresBufferHistoryAndSliceOffset();
        RasterSourceObeysThreeRules();
        RasterOutputFeedsThePixelGate();

        // 规范化流哈希
        CanonicalHashIgnoresHandlesAndPoolIndices();
        CanonicalHashSeesGeometry();
        CanonicalHashIgnoresUnreferencedResidue();
        CanonicalBytesLocateFirstDifference();
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    /// <summary>吞掉 debug 断言跑一段（本包大量用例故意踩门；门是否响另有 <see cref="AssertFires"/>）。</summary>
    private static void Quiet(Action a)
    {
        var prev = UiAssert.Handler;
        UiAssert.Handler = _ => { };
        try { a(); }
        finally { UiAssert.Handler = prev; }
    }

    private static uint Rgba(byte r, byte g, byte b, byte a) =>
        (uint)(r | (g << 8) | (b << 16) | (a << 24));

    private static QuadInstance Quad(float x, float y, float w, float h, uint color)
    {
        var q = new QuadInstance
        {
            Rect = new Vector4(x, y, w, h),
            UvA = new Vector4(0f, 0f, 1f, 0f),   // corner(0,0) 与 corner(1,0)
            UvB = new Vector4(0f, 1f, 1f, 1f),   // corner(0,1) 与 corner(1,1)
            Color = color,
        };
        return q;
    }

    private static SlotEntry Slot(in Affine2D m, TransformSlotFlags flags = TransformSlotFlags.None) =>
        new SlotEntry { M = m, Owner = NodeHandle.None, Flags = flags, WriteFreq = 0 };

    /// <summary>取像素 (x,y) 的 RGBA 四元。</summary>
    private static (byte R, byte G, byte B, byte A) Px(byte[] rgba, int width, int x, int y)
    {
        int i = (y * width + x) * 4;
        return (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
    }

    private static int OpaqueCount(byte[] rgba)
    {
        int n = 0;
        for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] != 0) n++;
        return n;
    }

    // ── 接口契约 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 「未装后端」不是一个状态：无 GPU 进程挂 <see cref="NullBackend"/>，
    /// 全部调用合法、收据照出、presents 恒 0（FgbCompiler = 无头运行时的前提）。
    /// </summary>
    private static void NullBackendIsTheNoGpuContract()
    {
        IRenderBackend b = new NullBackend();
        StreamHandle s = b.CreateStream(StreamDesc.ForQuads(4, "headless"));
        b.BeginFrame(1);
        b.UploadInstances(s, 0, new[] { Quad(0, 0, 1, 1, Rgba(255, 0, 0, 255)) });
        b.UploadClips(s, 0, new[] { default(ClipEntry) });
        b.WriteSlots(s, 0, new[] { SlotEntry.Identity });
        b.SetSegments(s, new[] { new SegmentDesc { TexCount = 1, Start = 0, Count = 1 } });
        b.SetRunOrders(s, 1, new[] { new RunOrder(0, 0) });
        IslandHandle island = b.CreateIsland(s, new IslandDesc { Kind = IslandKind.CustomMaterial, RenderEveryN = 1 });
        b.SyncIsland(island, default);
        b.DestroyIsland(island);
        PassHandle pass = b.BeginOffscreenPass(new OffscreenPassDesc { Kind = PassKind.FilterCapture });
        b.EndOffscreenPass(pass);
        b.BindMainSurface(new SurfaceDesc { Width = 8, Height = 8 });
        b.DrawStream(s, PassHandle.None);
        b.ReportDegrade(DegradeKind.ScissorFallback, "no-op");
        FrameReceipt r = b.EndFrame(new FrameStats { FrameId = 1, Dirty = true, QuadCount = 1 });
        b.DestroyStream(s);

        Check("后端契约: NullBackend 全调用合法、presents 恒 0、ABI 版本自报",
            r.Presents == 0 && !r.Presented && r.Ticks == 1
            && b.Name == "null" && b.ShaderAbiVersion == FairyNext.Contracts.Abi.ShaderAbiVersion
            && b.Caps.MaxTextureSlots == FairyNext.Contracts.Abi.SegmentMaxTextures);
    }

    /// <summary>
    /// 不变量 1：运行时结构的字节布局 ≡ ABI 生成物的偏移表。
    /// 这是 <c>Abi.cs → 生成物</c> 那条门的**下游半边**：生成物自洽不代表 C# 结构体照着它排。
    /// </summary>
    private static void AbiStructsMatchGeneratedOffsets()
    {
        var q = new QuadInstance
        {
            Rect = new Vector4(1f, 2f, 3f, 4f),
            UvA = new Vector4(5f, 6f, 7f, 8f),
            UvB = new Vector4(9f, 10f, 11f, 12f),
            Color = 0xAABBCCDDu,
            Route = 0x00012345u,
            Flags = 0x0000FF01u,
            Aux = 0x0BADF00Du,
            Extra = new Vector4(13f, 14f, 15f, 16f),
        };
        ReadOnlySpan<byte> qb = MemoryMarshal.AsBytes<QuadInstance>(new[] { q });

        bool quadOk = qb.Length == AbiMock.QuadInstanceSize
            && BitConverter.ToSingle(qb.Slice(AbiMock.QuadRectOffset, 4)) == 1f
            && BitConverter.ToSingle(qb.Slice(AbiMock.QuadUvAOffset, 4)) == 5f
            && BitConverter.ToSingle(qb.Slice(AbiMock.QuadUvBOffset, 4)) == 9f
            && BitConverter.ToUInt32(qb.Slice(AbiMock.QuadColorOffset, 4)) == 0xAABBCCDDu
            && BitConverter.ToUInt32(qb.Slice(AbiMock.QuadRouteOffset, 4)) == 0x00012345u
            && BitConverter.ToUInt32(qb.Slice(AbiMock.QuadFlagsOffset, 4)) == 0x0000FF01u
            && BitConverter.ToUInt32(qb.Slice(AbiMock.QuadAuxOffset, 4)) == 0x0BADF00Du
            && BitConverter.ToSingle(qb.Slice(AbiMock.QuadExtraOffset, 4)) == 13f;

        var c = new ClipEntry
        {
            Rect = new Vector4(21f, 22f, 23f, 24f),
            Soft = new Vector2(25f, 26f),
            Radii = new Vector4(27f, 28f, 29f, 30f),
            Slot = 7u,
        };
        ReadOnlySpan<byte> cb = MemoryMarshal.AsBytes<ClipEntry>(new[] { c });
        bool clipOk = cb.Length == AbiMock.ClipEntrySize
            && BitConverter.ToSingle(cb.Slice(AbiMock.ClipRectOffset, 4)) == 21f
            && BitConverter.ToSingle(cb.Slice(AbiMock.ClipSoftOffset, 4)) == 25f
            && BitConverter.ToSingle(cb.Slice(AbiMock.ClipRadiiOffset, 4)) == 27f
            && BitConverter.ToUInt32(cb.Slice(AbiMock.ClipSlotOffset, 4)) == 7u;

        Check("ABI: QuadInstance/ClipEntry 运行时布局 ≡ 生成物偏移表", quadOk && clipOk);

        // route 位域的读写往返（位域访问器不得手抄第二份移位常量）。
        var r = default(QuadInstance);
        r.SlotIndex = 200u;
        r.ClipIndex = 3000u;
        r.TexSlot = 3u;
        Check("ABI: route 位域读写往返（slot/clipIndex/texSlot 互不串扰）",
            r.SlotIndex == 200u && r.ClipIndex == 3000u && r.TexSlot == 3u
            && AbiMock.RouteReserved(r.Route) == 0u);
    }

    /// <summary>
    /// 渲染平面不变量 2：栅栏一律无限盒——<see cref="RunOrder"/> 在**类型上**没有 AABB 字段。
    /// 反射执法是因为「注释里写了别加 AABB」拦不住三年后的一次手滑。
    /// </summary>
    private static void RunRecordHasNoBoundsField()
    {
        FieldInfo[] fields = typeof(RunOrder).GetFields(BindingFlags.Public | BindingFlags.Instance);
        bool clean = true;
        foreach (FieldInfo f in fields)
        {
            string n = f.Name.ToLowerInvariant();
            if (n.Contains("aabb") || n.Contains("bound") || n.Contains("rect")) clean = false;
            if (f.FieldType == typeof(Rect) || f.FieldType == typeof(Vector4)) clean = false;
        }
        Check("渲染不变量 2: RunOrder 类型上没有 AABB/包围盒字段（紧栅栏不可表达）",
            clean && fields.Length == 2);
    }

    /// <summary>帧括号外的提交是协议违约——「跨帧提交碰巧能用」不该存在。</summary>
    private static void FrameBracketRejectsOutsideUploads()
    {
        var b = new MockBackend();
        StreamHandle s = b.CreateStream(StreamDesc.ForQuads(2));
        Quiet(() => b.UploadInstances(s, 0, new[] { Quad(0, 0, 1, 1, Rgba(1, 2, 3, 255)) }));
        Check("帧括号: BeginFrame 之前的上传即违约", b.Violations.Count == 1);
    }

    /// <summary>
    /// v1.3 时序契约 / 不变量 14：**离屏 pass 必须先于主表面绑定**。
    /// 违约既进 <c>Violations</c>（release 也记），又计入 GateReport.phaseViolation。
    /// </summary>
    private static void OffscreenPassMustPrecedeMainSurface()
    {
        var b = new MockBackend();
        StreamHandle s = b.CreateStream(StreamDesc.ForQuads(2));
        b.BeginFrame(1);
        b.BindMainSurface(new SurfaceDesc { Width = 4, Height = 4 });

        bool fired = AssertFires(() =>
        {
            PassHandle p = b.BeginOffscreenPass(new OffscreenPassDesc { Kind = PassKind.FilterCapture, RtId = 7 });
            b.EndOffscreenPass(p);
        });
        Quiet(() =>
        {
            b.DrawStream(s, PassHandle.None);
            b.EndFrame(new FrameStats { FrameId = 1, Dirty = true });
        });

        Check("不变量 14: 离屏 pass 晚于主表面绑定 → 断言 + phaseViolation",
            (fired || !DebugGates) && b.Violations.Count >= 1 && b.Gates.PhaseViolation >= 1
            && !b.Gates.Pass);
    }

    /// <summary>不变量 14 的第二半：capture 依赖序内层先——消费者已提交后才开被消费者 = 违约。</summary>
    private static void OffscreenDependencyOrderIsInnerFirst()
    {
        var b = new MockBackend();
        b.BeginFrame(1);
        PassHandle outer = b.BeginOffscreenPass(new OffscreenPassDesc { Kind = PassKind.FilterCapture, RtId = 1 });
        b.EndOffscreenPass(outer);
        Quiet(() =>
        {
            // 内层 pass 声明 outer 是它的消费者，却晚于 outer 提交：外层捕到的会是上一帧的内容。
            PassHandle inner = b.BeginOffscreenPass(
                new OffscreenPassDesc { Kind = PassKind.FilterCapture, RtId = 2, Consumer = outer });
            b.EndOffscreenPass(inner);
        });
        Check("不变量 14: capture 依赖序反了（外层先于内层）即违约", b.Violations.Count == 1);
    }

    /// <summary>合法序（内层先 → 外层 → 主表面）必须**一声不吭**——门不能对正确路径叫。</summary>
    private static void LegalPassOrderIsSilent()
    {
        var b = new MockBackend();
        StreamHandle s = b.CreateStream(StreamDesc.ForQuads(2, "filtered"));
        b.BeginFrame(1);

        PassHandle inner = b.BeginOffscreenPass(new OffscreenPassDesc { Kind = PassKind.FilterCapture, RtId = 2 });
        b.DrawStream(s, inner);
        b.EndOffscreenPass(inner);

        PassHandle outer = b.BeginOffscreenPass(
            new OffscreenPassDesc { Kind = PassKind.FadeGroup, RtId = 1, Consumer = PassHandle.None });
        b.DrawStream(s, outer);
        b.EndOffscreenPass(outer);

        b.BindMainSurface(new SurfaceDesc { Width = 8, Height = 8 });
        b.DrawStream(s, PassHandle.None);
        FrameReceipt r = b.EndFrame(new FrameStats { FrameId = 1, Dirty = true });

        Check("pass 序: 内层→外层→主表面 零违约、零门计数、present 一次",
            b.Violations.Count == 0 && b.Gates.Pass && r.Presented && b.Passes.Count == 2
            && b.Passes[0].RtId == 2 && b.Passes[1].RtId == 1 && b.Passes[0].Ended);
    }

    /// <summary>相位探针（M1-14 接线后由内核提供）：后端提交只在 P7/P8。</summary>
    private static void PhaseProbeCatchesUploadOutsideDrain()
    {
        var b = new MockBackend();
        FramePhase phase = FramePhase.P8_Submit;
        b.PhaseProbe = () => phase;
        StreamHandle s = b.CreateStream(StreamDesc.ForQuads(2));
        b.BeginFrame(1);
        b.UploadInstances(s, 0, new[] { Quad(0, 0, 1, 1, Rgba(9, 9, 9, 255)) });
        bool cleanInSubmit = b.Gates.PhaseViolation == 0;

        phase = FramePhase.P4_State;
        Quiet(() => b.UploadInstances(s, 1, new[] { Quad(1, 1, 1, 1, Rgba(9, 9, 9, 255)) }));

        Check("相位探针: P8 上传合法、P4 上传即 phaseViolation",
            cleanInSubmit && b.Gates.PhaseViolation == 1);
    }

    // ── 留痕与快照 ──────────────────────────────────────────────────────────

    /// <summary>留痕完整性：接口的每一个方法都必须在日志里留下自己那一条，且按调用序。</summary>
    private static void MockRecordsEveryCall()
    {
        var b = new MockBackend();
        StreamHandle s = b.CreateStream(StreamDesc.ForQuads(4, "trace"));
        b.BeginFrame(1);
        b.UploadInstances(s, 0, new[] { Quad(0, 0, 2, 2, Rgba(255, 0, 0, 255)) });
        b.UploadClips(s, 0, new[] { default(ClipEntry), new ClipEntry { Rect = new Vector4(0f, 0f, 4f, 4f) } });
        b.WriteSlots(s, 0, new[] { SlotEntry.Identity });
        b.SetSegments(s, new[] { new SegmentDesc { Tex0 = new TexId(1), TexCount = 1, Start = 0, Count = 1 } });
        b.SetRunOrders(s, 1, new[] { new RunOrder(0, 0), new RunOrder(1, 16) });
        IslandHandle island = b.CreateIsland(s, new IslandDesc
        {
            Kind = IslandKind.ExternalNative, RenderEveryN = 1, Visual = IslandVisual.Opaque, DebugName = "spine",
        });
        b.SyncIsland(island, new IslandSync { SlotMatrix = Affine2D.Identity, StillAnimating = true });
        bool islandBlocksIdle = !b.AllIslandsStill();   // 自报仍在动的孤岛 = 没有跳帧资格（不变量 13）
        PassHandle pass = b.BeginOffscreenPass(new OffscreenPassDesc { Kind = PassKind.IslandRt, RtId = 3 });
        b.DrawStream(s, pass);
        b.EndOffscreenPass(pass);
        b.BindMainSurface(new SurfaceDesc { Width = 16, Height = 16 });
        b.DrawStream(s, PassHandle.None);
        b.ReportDegrade(DegradeKind.ScissorFallback, "材质拒绝 clip include");
        b.EndFrame(new FrameStats { FrameId = 1, Dirty = true, QuadCount = 1 });
        b.DestroyIsland(island);
        b.DestroyStream(s);

        bool everyKind = true;
        for (int k = 1; k <= (int)MockCallKind.EndFrame; k++)
            if (b.CallCount((MockCallKind)k) == 0) everyKind = false;

        bool ordered = b.Calls[0].Kind == MockCallKind.CreateStream
            && b.Calls[1].Kind == MockCallKind.BeginFrame
            && b.LastCall(MockCallKind.DestroyStream).Kind == MockCallKind.DestroyStream;

        bool degradeDetail = b.LastCall(MockCallKind.ReportDegrade).Detail == "材质拒绝 clip include"
            && b.LastCall(MockCallKind.UploadInstances).Count == 1
            && b.LastCall(MockCallKind.SyncIsland).Count == 1;   // StillAnimating 进留痕

        Check("留痕: 17 类调用全部留痕、按序、载荷可查，孤岛自报进零脏帧判据",
            everyKind && ordered && degradeDetail && b.Violations.Count == 0
            && islandBlocksIdle && b.AllIslandsStill());   // 拆掉之后没有活孤岛，判据回到「可跳帧」
    }

    /// <summary>快照 = 后端真正收到的东西（六个数组逐项对拍，含 mock 专有的 baseColor 侧信道）。</summary>
    private static void SnapshotMirrorsWhatWasUploaded()
    {
        var b = new MockBackend();
        StreamHandle s = b.CreateStream(StreamDesc.ForQuads(4, "mirror"));
        b.BeginFrame(1);
        QuadInstance q0 = Quad(1f, 2f, 3f, 4f, Rgba(10, 20, 30, 255));
        QuadInstance q1 = Quad(5f, 6f, 7f, 8f, Rgba(40, 50, 60, 128));
        b.UploadInstances(s, 0, new[] { q0, q1 });
        b.SetBaseColors(s, 0, new[] { Rgba(10, 20, 30, 255), Rgba(40, 50, 60, 255) });
        b.UploadClips(s, 0, new[] { default(ClipEntry), new ClipEntry { Rect = new Vector4(1f, 1f, 9f, 9f), Slot = 1u } });
        b.WriteSlots(s, 0, new[] { SlotEntry.Identity, Slot(Affine2D.TRS(new Vector2(3f, 4f), 0f, Vector2.one)) });
        b.SetSegments(s, new[] { new SegmentDesc { Tex0 = new TexId(9), TexCount = 1, Start = 0, Count = 2, RunIndex = 0 } });
        b.SetRunOrders(s, 3, new[] { new RunOrder(0, 0) });
        b.EndFrame(new FrameStats { FrameId = 1, Dirty = true });

        StreamSnapshot snap = b.Snapshot(s);
        Check("快照: quads/baseColor/segments/runs/clips/slots 六项与上传逐项一致",
            snap.Quads.Length == 2 && snap.Quads[0].Equals(q0) && snap.Quads[1].Equals(q1)
            && snap.HasBaseColor && snap.BaseColor[1] == Rgba(40, 50, 60, 255)
            && snap.Clips.Length == 2 && snap.Clips[1].Slot == 1u
            && snap.Slots.Length == 2 && snap.Slots[1].M.tx == 3f
            && snap.Segments.Length == 1 && snap.Segments[0].Tex0.Value == 9u
            && snap.Runs.Length == 1 && snap.StructEpoch == 3u
            && snap.DebugName == "mirror" && b.Violations.Count == 0);
    }

    /// <summary>快照是**拷贝**：定格之后再上传不该改动已经拿到手的那一份（对拍腿的地基）。</summary>
    private static void SnapshotIsACopyNotAView()
    {
        var b = new MockBackend();
        StreamHandle s = b.CreateStream(StreamDesc.ForQuads(2));
        b.BeginFrame(1);
        b.UploadInstances(s, 0, new[] { Quad(0f, 0f, 1f, 1f, Rgba(255, 0, 0, 255)) });
        StreamSnapshot before = b.Snapshot(s);
        ulong hashBefore = CanonicalStream.Hash(before);

        b.UploadInstances(s, 0, new[] { Quad(0f, 0f, 1f, 1f, Rgba(0, 255, 0, 255)) });
        StreamSnapshot after = b.Snapshot(s);

        Check("快照: 定格后不随后续上传变动（拷贝而非视图）",
            before.Quads[0].Color == Rgba(255, 0, 0, 255)
            && after.Quads[0].Color == Rgba(0, 255, 0, 255)
            && CanonicalStream.Hash(before) == hashBefore
            && CanonicalStream.Hash(after) != hashBefore);
    }

    /// <summary>不变量 11：pending 中的缓冲收到 upload/draw = use-after-free，无 GPU 也要当场暴露。</summary>
    private static void UseAfterFreeIsLoud()
    {
        var b = new MockBackend();
        StreamHandle s = b.CreateStream(StreamDesc.ForQuads(2));
        b.BeginFrame(1);
        b.DestroyStream(s);
        bool fired = AssertFires(() => b.UploadInstances(s, 0, new[] { Quad(0f, 0f, 1f, 1f, Rgba(1, 1, 1, 255)) }));
        Check("不变量 11: 已销毁流上的上传 = use-after-free，断言 + 记账",
            (fired || !DebugGates) && b.Violations.Count == 1
            && b.Violations[0].Contains("已销毁"));
    }

    /// <summary>不变量 11：fence 队列深度 ≤ <see cref="AbiMock.GpuFenceDepth"/>；超深即计数。</summary>
    private static void FenceQueueDepthIsCounted()
    {
        var b = new MockBackend();
        var handles = new StreamHandle[AbiMock.GpuFenceDepth + 1];
        for (int i = 0; i < handles.Length; i++) handles[i] = b.CreateStream(StreamDesc.ForQuads(1));

        b.BeginFrame(1);
        for (int i = 0; i < handles.Length; i++) b.DestroyStream(handles[i]);
        int depth = b.FencePendingDepth;
        b.EndFrame(new FrameStats { FrameId = 1, Dirty = false });

        // 深度 GpuFenceDepth 帧之后到期释放：此时池位才允许复用。
        for (ulong f = 2; f <= 1 + (ulong)AbiMock.GpuFenceDepth; f++)
        {
            b.BeginFrame(f);
            b.EndFrame(new FrameStats { FrameId = f, Dirty = false });
        }

        Check("不变量 11: fence 队列超深计数 + 到期才释放",
            depth == AbiMock.GpuFenceDepth + 1 && b.Gates.FencePending == 1
            && b.FencePendingDepth == 0 && b.Violations.Count == 0);
    }

    // ── 契约执法 ────────────────────────────────────────────────────────────

    /// <summary>不变量 6：axis-aligned 位在每次写槽时重验，不是建槽时验一次。</summary>
    private static void SlotWriteReVerifiesAxisAlignedBit()
    {
        var b = new MockBackend();
        StreamHandle s = b.CreateStream(StreamDesc.ForQuads(2));
        b.BeginFrame(1);
        b.WriteSlots(s, 0, new[] { SlotEntry.Identity });
        int cleanViolations = b.Violations.Count;

        var rotated = Slot(Affine2D.TRS(Vector2.zero, 0.5f, Vector2.one), TransformSlotFlags.AxisAligned);
        Quiet(() => b.WriteSlots(s, 1, new[] { rotated }));

        Check("不变量 6: 旋转矩阵却置 axisAligned 位 → 违约",
            cleanViolations == 0 && b.Violations.Count == 1);
    }

    /// <summary>不变量 16：同值槽写不该发生（零脏帧短路以此为前提），发生了要有收据。</summary>
    private static void RedundantSlotWriteIsCounted()
    {
        var b = new MockBackend();
        StreamHandle s = b.CreateStream(StreamDesc.ForQuads(2));
        b.BeginFrame(1);
        var entry = Slot(Affine2D.TRS(new Vector2(2f, 3f), 0f, Vector2.one), TransformSlotFlags.AxisAligned);
        b.WriteSlots(s, 1, new[] { entry });
        int afterFirst = b.RedundantSlotWrites;
        b.WriteSlots(s, 1, new[] { entry });                 // 位等重写
        int afterSecond = b.RedundantSlotWrites;
        entry.M.tx = 2.5f;
        b.WriteSlots(s, 1, new[] { entry });                 // 真的变了
        Check("不变量 16: 同值槽写有收据、异值写不计数",
            afterFirst == 0 && afterSecond == 1 && b.RedundantSlotWrites == 1);
    }

    /// <summary>不变量 10：序派生重推**当且仅当** structEpoch 变；序本身严格同序。</summary>
    private static void RelabelRequiresStructEpochChange()
    {
        var b = new MockBackend();
        StreamHandle s = b.CreateStream(StreamDesc.ForQuads(2));
        b.BeginFrame(1);
        var runs = new[] { new RunOrder(0, 0), new RunOrder(1, 16), new RunOrder(2, 32) };
        b.SetRunOrders(s, 5, runs);
        int clean = b.Violations.Count;
        Quiet(() => b.SetRunOrders(s, 5, runs));             // epoch 没变还重推
        int afterSameEpoch = b.Violations.Count;
        b.SetRunOrders(s, 6, runs);                          // epoch 变了，合法
        Quiet(() => b.SetRunOrders(s, 7, new[] { new RunOrder(0, 32), new RunOrder(1, 16) }));  // 序不递增

        Check("不变量 10: epoch 未变即重推 / sortingOrder 不递增 都是违约",
            clean == 0 && afterSameEpoch == 1 && b.Violations.Count == 2);
    }

    /// <summary>ABI append-only 纪律的运行期半边：保留位必须写零。</summary>
    private static void ReservedBitsMustBeZero()
    {
        var b = new MockBackend();
        StreamHandle s = b.CreateStream(StreamDesc.ForQuads(2));
        b.BeginFrame(1);
        QuadInstance dirty = Quad(0f, 0f, 1f, 1f, Rgba(1, 2, 3, 255));
        dirty.Route |= 1u << AbiMock.RouteReservedShift;
        Quiet(() => b.UploadInstances(s, 0, new[] { dirty }));
        Check("ABI: route/flags 保留位非零即违约（今天的垃圾位 = 明天的位域冲突）",
            b.Violations.Count == 1 && b.Violations[0].Contains("保留位"));
    }

    /// <summary>不变量 13：零脏帧跳过 present，收据里 ticks &gt; presents。</summary>
    private static void ZeroDirtyFrameSkipsPresent()
    {
        var b = new MockBackend();
        StreamHandle s = b.CreateStream(StreamDesc.ForQuads(2));

        b.BeginFrame(1);
        b.UploadInstances(s, 0, new[] { Quad(0f, 0f, 2f, 2f, Rgba(255, 255, 255, 255)) });
        b.BindMainSurface(new SurfaceDesc { Width = 4, Height = 4 });
        b.DrawStream(s, PassHandle.None);
        FrameReceipt busy = b.EndFrame(new FrameStats { FrameId = 1, Dirty = true, QuadCount = 1 });

        b.BeginFrame(2);
        FrameReceipt idle = b.EndFrame(new FrameStats { FrameId = 2, Dirty = false });

        Check("不变量 13: 零脏帧不 present，空转收据 ticks=2 presents=1",
            busy.Presented && !idle.Presented && idle.Ticks == 2 && idle.Presents == 1
            && b.Violations.Count == 0);
    }

    /// <summary>零脏帧的判据与实际排水必须一致：说没脏却上传了 = 判据坏了。</summary>
    private static void ZeroDirtyFrameWithUploadsIsLoud()
    {
        var b = new MockBackend();
        StreamHandle s = b.CreateStream(StreamDesc.ForQuads(2));
        b.BeginFrame(1);
        b.UploadInstances(s, 0, new[] { Quad(0f, 0f, 1f, 1f, Rgba(1, 1, 1, 255)) });
        Quiet(() => b.EndFrame(new FrameStats { FrameId = 1, Dirty = false }));
        Check("不变量 13: Dirty=false 却发生上传 → 违约", b.Violations.Count == 1);
    }

    /// <summary>
    /// 2026-08 审计：stats.FrameId 从前无人校验——BuildStats 拿错帧的账（收据、零脏帧短路、
    /// 逐帧哈希全记到别的帧上）后端照收。现在 EndFrame 与 BeginFrame 对表：错帧 = 违约进
    /// Violations（release 照记），对帧静默，0 = 未接帧号的裸调用放行。
    /// </summary>
    private static void FrameIdMismatchIsLoud()
    {
        var b = new MockBackend();
        StreamHandle s = b.CreateStream(StreamDesc.ForQuads(2));
        b.BeginFrame(5);
        b.UploadInstances(s, 0, new[] { Quad(0f, 0f, 1f, 1f, Rgba(1, 2, 3, 255)) });
        Quiet(() => b.EndFrame(new FrameStats { FrameId = 7, Dirty = true, QuadCount = 1 }));
        bool caught = b.Violations.Count == 1 && b.Violations[0].Contains("FrameId");

        b.BeginFrame(6);
        b.UploadInstances(s, 0, new[] { Quad(0f, 0f, 1f, 1f, Rgba(1, 2, 3, 255)) });
        FrameReceipt r = b.EndFrame(new FrameStats { FrameId = 6, Dirty = true, QuadCount = 1 });

        Check("契约: EndFrame 的 stats.FrameId 与 BeginFrame 对表（错帧违约、对帧静默）",
            caught && b.Violations.Count == 1 && r.FrameId == 6);
    }

    // ── GateReport 七字段 ───────────────────────────────────────────────────

    /// <summary>门的判定是「七字段全零」，不是「像素对了」。</summary>
    private static void GateReportPassIsAllZero()
    {
        var clean = default(GateReport);
        var dirty = default(GateReport);
        dirty.Degrade = 1;
        Check("GateReport: 全零即通过，任一非零即不过（Pass ⇔ AllZero）",
            clean.Pass && clean.AllZero && !dirty.Pass && dirty.Total == 1
            && clean.Describe().Contains("pass") && dirty.Describe().Contains("degrade=1"));
    }

    /// <summary>七字段是契约：多一个少一个都会让 <see cref="GateReport.AllZero"/> 悄悄漏判。</summary>
    private static void GateReportHasExactlySevenCounters()
    {
        FieldInfo[] fields = typeof(GateReport).GetFields(BindingFlags.Public | BindingFlags.Instance);
        bool allUInt = true;
        foreach (FieldInfo f in fields) if (f.FieldType != typeof(uint)) allUInt = false;

        // 每个字段单独置 1 都必须让 Pass 变 false——AllZero 漏掉任一字段即在此现形。
        bool everyFieldCounts = true;
        for (int i = 0; i < fields.Length; i++)
        {
            object boxed = default(GateReport);
            fields[i].SetValue(boxed, 1u);
            if (((GateReport)boxed).Pass) everyFieldCounts = false;
        }

        Check("GateReport: 恰好七个 uint 计数器，且每一个都参与判定",
            fields.Length == 7 && allUInt && everyFieldCounts);
    }

    /// <summary>不变量 5：槽/clip 越预算必须计数（越了还不吭声才是最坏情况）。</summary>
    private static void GateReportCountsBudgetOverflow()
    {
        var b = new MockBackend();
        StreamHandle s = b.CreateStream(new StreamDesc { QuadCapacity = 2, ClipCapacity = 2, SlotCapacity = 2 });
        b.BeginFrame(1);
        b.ReportDegrade(DegradeKind.SlotStarvation, "槽荒：容器退 tier-2");
        b.WriteSlots(s, AbiMock.TransformSlotBudget, new[] { SlotEntry.Identity });

        b.ReportDegrade(DegradeKind.ClipStarvation, "clip 超限：父窗口降级");
        var clips = new ClipEntry[AbiMock.ClipEntryBudget + 1];
        b.UploadClips(s, 0, clips);
        b.EndFrame(new FrameStats { FrameId = 1, Dirty = true });

        Check("不变量 5: 槽越预算 / clip 越预算各自计数，门因此不过",
            b.Gates.SlotOverflow == 1 && b.Gates.ClipOverflow == 1 && b.Gates.Degrade == 2
            && !b.Gates.Pass && b.Violations.Count == 0);
    }

    /// <summary>不变量 5 的另一半：预算超限**而无阶梯事件** = 静默降级 = 失败。</summary>
    private static void GateReportDemandsALadderEvent()
    {
        var b = new MockBackend();
        StreamHandle s = b.CreateStream(new StreamDesc { QuadCapacity = 1, SlotCapacity = 2 });
        b.BeginFrame(1);
        b.WriteSlots(s, AbiMock.TransformSlotBudget, new[] { SlotEntry.Identity });   // 越预算，没上报
        Quiet(() => b.EndFrame(new FrameStats { FrameId = 1, Dirty = true }));
        Check("不变量 5: 预算超限却零阶梯事件 → 违约（静默降级是被禁的那一种）",
            b.Gates.SlotOverflow == 1 && b.Violations.Count == 1
            && b.Violations[0].Contains("静默降级"));
    }

    /// <summary>门的两个半边（内核 + 后端）相加才是完整的七字段。</summary>
    private static void GateReportMergesKernelAndBackend()
    {
        var table = new NodeTable();
        NodeHandle leaf = table.CreateNode(NodeType.Image);
        table.AddChild(table.Root, leaf);
        var kernel = new UiKernel(table);
        var inv = kernel.Invalidation;

        // P7 里写 = 相位违约写（不变量 9）：内核半边的 phaseViolation 从这里来。
        kernel.PhaseWatch = phase =>
        {
            if (phase == FramePhase.P7_RenderDrain)
                Quiet(() => inv.Mark(leaf, Ch.Color, InvalidateReason.UserWrite));
        };
        kernel.Tick(FrameTime.First(0.016f, 0.016f));
        kernel.PhaseWatch = null;

        GateReport kernelHalf = GateReport.FromKernel(kernel.Diagnostics);

        var b = new MockBackend();
        StreamHandle s = b.CreateStream(StreamDesc.ForQuads(1));
        b.BeginFrame(1);
        b.ReportDegrade(DegradeKind.BlobToExtract, "哈希失配，组件降回 Extract");
        b.EndFrame(new FrameStats { FrameId = 1, Dirty = false });
        GateReport merged = kernelHalf + GateReport.FromBackend(b);

        Check("GateReport: 内核半边（相位违例）+ 后端半边（降级）合并后仍是七字段一本账",
            kernelHalf.PhaseViolation >= 1 && merged.Degrade == 1
            && merged.PhaseViolation == kernelHalf.PhaseViolation && !merged.Pass);
    }

    // ── 参考光栅（三规则）──────────────────────────────────────────────────

    /// <summary>
    /// 已知小图案的像素 golden：rect (2,2,4,3) 覆盖的样点恰是中心落在 [2,6)×[2,5) 的 12 个像素。
    /// 半开区间由 top-left 填充规则保证——贴 max 边的像素不算命中。
    /// </summary>
    private static void RasterGoldenSolidRect()
    {
        var raster = new ReferenceRaster(8, 8);
        raster.Clear(0);
        raster.DrawQuads(new[] { Quad(2f, 2f, 4f, 3f, Rgba(255, 0, 0, 255)) },
            ReadOnlySpan<SlotEntry>.Empty, ReadOnlySpan<ClipEntry>.Empty);
        byte[] px = raster.Pixels;

        Check("参考光栅: 实心矩形 golden（12 个覆盖样点，边界半开）",
            OpaqueCount(px) == 12
            && Px(px, 8, 2, 2) == ((byte)255, (byte)0, (byte)0, (byte)255)
            && Px(px, 8, 5, 4) == ((byte)255, (byte)0, (byte)0, (byte)255)
            && Px(px, 8, 6, 2).A == 0 && Px(px, 8, 1, 2).A == 0 && Px(px, 8, 2, 5).A == 0
            && raster.CoveredSamples == 12 && raster.SkippedNonFinite == 0);
    }

    /// <summary>
    /// 规则 1 的 golden：整数 src-over。蓝底 (0,0,255,255) 上盖 α=128 的红 (255,0,0)：
    /// r = round(255·128/255) = 128、b = round(255·127/255) = 127、a = 255。
    /// 浮点实现在这里会给出 127 或 128 的漂移，正是三规则要消灭的那一类差异。
    /// </summary>
    private static void RasterGoldenIntegerBlend()
    {
        var raster = new ReferenceRaster(4, 4);
        raster.Clear(0);
        raster.DrawQuads(
            new[]
            {
                Quad(0f, 0f, 4f, 4f, Rgba(0, 0, 255, 255)),
                Quad(0f, 0f, 4f, 4f, Rgba(255, 0, 0, 128)),
            },
            ReadOnlySpan<SlotEntry>.Empty, ReadOnlySpan<ClipEntry>.Empty);

        Check("参考光栅: 整数混合 golden（128,0,127,255）",
            Px(raster.Pixels, 4, 0, 0) == ((byte)128, (byte)0, (byte)127, (byte)255)
            && Px(raster.Pixels, 4, 3, 3) == ((byte)128, (byte)0, (byte)127, (byte)255)
            && ReferenceRaster.Div255(255 * 128) == 128 && ReferenceRaster.Div255(255 * 127) == 127);
    }

    /// <summary>规则 1：grayed 走整数亮度权重（77/151/28，和为 256 ⇒ 右移 8 即归一）。</summary>
    private static void RasterGoldenGrayedLuminance()
    {
        var raster = new ReferenceRaster(2, 2);
        raster.Clear(0);
        QuadInstance q = Quad(0f, 0f, 2f, 2f, Rgba(200, 100, 50, 255));
        q.Flags = 1u << AbiMock.FlagsGrayedShift;
        raster.DrawQuads(new[] { q }, ReadOnlySpan<SlotEntry>.Empty, ReadOnlySpan<ClipEntry>.Empty);

        int expected = (77 * 200 + 151 * 100 + 28 * 50) >> 8;   // = 124
        Check("参考光栅: grayed 整数亮度 golden（124,124,124,255）",
            expected == 124
            && Px(raster.Pixels, 2, 0, 0) == ((byte)124, (byte)124, (byte)124, (byte)255));
    }

    /// <summary>裁剪窗与覆盖判定同一条半开区间语义——口径不一致会漏一列像素。</summary>
    private static void RasterHonoursClipWindow()
    {
        var raster = new ReferenceRaster(8, 8);
        raster.Clear(0);
        QuadInstance q = Quad(0f, 0f, 8f, 8f, Rgba(0, 255, 0, 255));
        q.ClipIndex = 1u;
        var clips = new[]
        {
            default(ClipEntry),
            new ClipEntry { Rect = new Vector4(2f, 2f, 6f, 6f), Slot = 0u },   // xMin,yMin,xMax,yMax
        };
        raster.DrawQuads(new[] { q }, ReadOnlySpan<SlotEntry>.Empty, clips);

        Check("参考光栅: clip 窗口按 [min,max) 生效（4×4 = 16 个样点）",
            OpaqueCount(raster.Pixels) == 16
            && Px(raster.Pixels, 8, 2, 2).A == 255 && Px(raster.Pixels, 8, 5, 5).A == 255
            && Px(raster.Pixels, 8, 1, 2).A == 0 && Px(raster.Pixels, 8, 6, 5).A == 0);
    }

    /// <summary>规则 3：UV 走重心线性插值 + nearest 取样；本例同时抓「UV 插反/转置」。</summary>
    private static void RasterSamplesTextureByUv()
    {
        var tex = new CheckerTexture(2, 2, Rgba(255, 255, 255, 255), Rgba(0, 0, 0, 255));
        var raster = new ReferenceRaster(4, 4);
        raster.Clear(0);
        raster.DrawQuads(new[] { Quad(0f, 0f, 4f, 4f, Rgba(255, 255, 255, 255)) },
            ReadOnlySpan<SlotEntry>.Empty, ReadOnlySpan<ClipEntry>.Empty, tex);

        // 左上格 = A（白），右上/左下 = B（黑），右下 = A。UV 若被转置，左下与右上会互换但值相同，
        // 故再查一次「左上 ≠ 右上」确保不是全同色。
        Check("参考光栅: UV 线性插值 + nearest 取样（棋盘四象限对位）",
            Px(raster.Pixels, 4, 0, 0).R == 255 && Px(raster.Pixels, 4, 2, 0).R == 0
            && Px(raster.Pixels, 4, 0, 2).R == 0 && Px(raster.Pixels, 4, 2, 2).R == 255
            && OpaqueCount(raster.Pixels) == 16);
    }

    /// <summary>
    /// 三规则的目的本身：同一输入两次栅格**逐字节**相等（含旋转槽矩阵与纹理采样）。
    /// 这条在单机上是必要条件；充分性由「无超越函数」的源码审计与整数运算共同担保。
    /// </summary>
    private static void RasterIsBitIdenticalAcrossRuns()
    {
        var slots = new[]
        {
            SlotEntry.Identity,
            Slot(Affine2D.TRS(new Vector2(6.25f, 5.5f), 0.3712f, new Vector2(1.7f, 0.83f))),
        };
        var clips = new[] { default(ClipEntry), new ClipEntry { Rect = new Vector4(1f, 1f, 15f, 15f), Slot = 0u } };
        var quads = new QuadInstance[6];
        for (int i = 0; i < quads.Length; i++)
        {
            quads[i] = Quad(0.37f * i, 1.13f * i, 5.5f + i, 4.25f + i,
                Rgba((byte)(30 * i), (byte)(255 - 20 * i), (byte)(7 * i), (byte)(60 + 30 * i)));
            quads[i].SlotIndex = (uint)(i & 1);
            quads[i].ClipIndex = 1u;
        }
        var tex = new CheckerTexture(8, 8, Rgba(240, 10, 20, 255), Rgba(20, 30, 250, 200), 2);

        byte[] first = Rasterize();
        byte[] second = Rasterize();
        bool identical = first.Length == second.Length;
        for (int i = 0; identical && i < first.Length; i++) if (first[i] != second[i]) identical = false;

        byte[] Rasterize()
        {
            var r = new ReferenceRaster(16, 16);
            r.Clear(Rgba(8, 8, 8, 255));
            r.DrawQuads(quads, slots, clips, tex);
            return r.Pixels;
        }

        Check("参考光栅: 同输入两跑逐字节相等（旋转槽 + 纹理 + 混合）",
            identical && OpaqueCount(first) == 256);
    }

    /// <summary>
    /// 结果只能取决于输入：既不能沾上一次绘制的残留（缓冲历史），
    /// 也不能沾输入数组的**下标偏移**（池残留是规范化哈希要剔的东西，光栅同理不许看见）。
    /// </summary>
    private static void RasterIgnoresBufferHistoryAndSliceOffset()
    {
        QuadInstance[] scene =
        {
            Quad(1f, 1f, 3f, 3f, Rgba(200, 30, 40, 200)),
            Quad(2f, 2f, 3f, 3f, Rgba(10, 220, 40, 128)),
        };

        var fresh = new ReferenceRaster(8, 8);
        fresh.Clear(0);
        fresh.DrawQuads(scene, ReadOnlySpan<SlotEntry>.Empty, ReadOnlySpan<ClipEntry>.Empty);

        var reused = new ReferenceRaster(8, 8);
        reused.Clear(0);
        reused.DrawQuads(new[] { Quad(0f, 0f, 8f, 8f, Rgba(255, 255, 255, 255)) },
            ReadOnlySpan<SlotEntry>.Empty, ReadOnlySpan<ClipEntry>.Empty);
        reused.Clear(0);                                        // 洗掉历史
        reused.DrawQuads(scene, ReadOnlySpan<SlotEntry>.Empty, ReadOnlySpan<ClipEntry>.Empty);

        var padded = new QuadInstance[5];
        padded[3] = scene[0];
        padded[4] = scene[1];
        var sliced = new ReferenceRaster(8, 8);
        sliced.Clear(0);
        sliced.DrawQuads(new ReadOnlySpan<QuadInstance>(padded, 3, 2),
            ReadOnlySpan<SlotEntry>.Empty, ReadOnlySpan<ClipEntry>.Empty);

        bool sameAsReused = true, sameAsSliced = true;
        for (int i = 0; i < fresh.Pixels.Length; i++)
        {
            if (fresh.Pixels[i] != reused.Pixels[i]) sameAsReused = false;
            if (fresh.Pixels[i] != sliced.Pixels[i]) sameAsSliced = false;
        }

        Check("参考光栅: 结果与缓冲历史、输入数组下标偏移都无关",
            sameAsReused && sameAsSliced && OpaqueCount(fresh.Pixels) > 0);
    }

    /// <summary>
    /// 规则 3 的机械执法：扫 ReferenceRaster.cs 的**代码**（剥掉注释），
    /// 不许出现 System.Math/MathF 调用与任何超越函数，也不许出现 double。
    /// 「同输入两跑相等」在单机上永远成立，跨 CPU 一致靠的是这条源码纪律。
    /// </summary>
    private static void RasterSourceObeysThreeRules()
    {
        string? root = RepoRoot.Find(AppContext.BaseDirectory);
        string path = root == null ? "" : RepoRoot.ToAbsolute(root, "src/Backend.Mock/ReferenceRaster.cs");
        if (root == null || !File.Exists(path))
        {
            Check("参考光栅: 三规则源码审计（无超越函数 / 无 double）", false);
            return;
        }

        // 剥注释：本文件只有行注释，逐行截到首个 "//"。字符串里若有 "//" 只会让审计更严，不会更松。
        var code = new System.Text.StringBuilder();
        foreach (string line in File.ReadAllLines(path))
        {
            int c = line.IndexOf("//", StringComparison.Ordinal);
            code.Append(c >= 0 ? line.Substring(0, c) : line).Append('\n');
        }
        string body = code.ToString();

        string[] banned = { "Math", "double", "Sqrt", "sqrt", "Sin(", "Cos(", "Tan(", "Pow(", "Exp(", "Log(" };
        string? hit = null;
        foreach (string token in banned)
            if (body.Contains(token, StringComparison.Ordinal)) { hit = token; break; }

        if (hit != null) Console.WriteLine($"     参考光栅源码出现被禁记号：{hit}");
        Check("参考光栅: 三规则源码审计（无超越函数 / 无 double）", hit == null);
    }

    /// <summary>
    /// M1-26 的接缝预演：**后端快照 → 参考光栅 → 像素比对器**一条路走通。
    /// 光栅产出的就是 <c>PngImage.FromRgba</c> 要的那种缓冲（RGBA8、row 0 顶行），
    /// 不需要在中间插一个 PNG 编码器——那会是第二份可能出错的实现。
    /// </summary>
    private static void RasterOutputFeedsThePixelGate()
    {
        var b = new MockBackend();
        StreamHandle s = b.CreateStream(StreamDesc.ForQuads(2, "pixel-gate"));
        b.BeginFrame(1);
        b.UploadInstances(s, 0, new[]
        {
            Quad(1f, 1f, 5f, 5f, Rgba(220, 40, 60, 255)),
            Quad(3f, 3f, 4f, 4f, Rgba(20, 200, 90, 160)),
        });
        b.WriteSlots(s, 0, new[] { SlotEntry.Identity });
        b.BindMainSurface(new SurfaceDesc { Width = 8, Height = 8 });
        b.DrawStream(s, PassHandle.None);
        b.EndFrame(new FrameStats { FrameId = 1, Dirty = true, QuadCount = 2 });

        StreamSnapshot snap = b.Snapshot(s);
        byte[] golden = ReferenceRaster.Render(8, 8, snap, Rgba(0, 0, 0, 255));
        byte[] again = ReferenceRaster.Render(8, 8, snap, Rgba(0, 0, 0, 255));

        var goldenImage = FairyNext.Tools.OracleCompare.PngImage.FromRgba(8, 8, golden);
        var againImage = FairyNext.Tools.OracleCompare.PngImage.FromRgba(8, 8, again);
        var tol = FairyNext.Tools.OracleCompare.OracleTolerance.Baseline;
        var clean = FairyNext.Tools.OracleCompare.PixelComparer.Compare(goldenImage, againImage, tol);

        byte[] perturbed = (byte[])golden.Clone();
        perturbed[(3 * 8 + 3) * 4] ^= 0xFF;                       // 单像素单通道改 255：越硬线立刻失败
        var dirtyImage = FairyNext.Tools.OracleCompare.PngImage.FromRgba(8, 8, perturbed);
        var dirty = FairyNext.Tools.OracleCompare.PixelComparer.Compare(goldenImage, dirtyImage, tol);

        Check("接缝: 后端快照 → 参考光栅 → 像素比对器（M1-26 的走法）",
            clean.Pass && clean.DiffPixels == 0 && !dirty.Pass && dirty.DiffPixels == 1
            && b.Violations.Count == 0 && b.Gates.Pass);
    }

    // ── 规范化流哈希 ────────────────────────────────────────────────────────

    /// <summary>
    /// 规范化的核心承诺：**只差句柄与池下标的两条流，哈希相同**。
    /// 左流用槽 1 / clip 1，右流用槽 3 / clip 2 并带残留条目，画面完全一致 ⇒ 字节必须一致。
    /// </summary>
    private static void CanonicalHashIgnoresHandlesAndPoolIndices()
    {
        var m = Affine2D.TRS(new Vector2(12f, 7f), 0f, Vector2.one);
        var clipRect = new Vector4(0f, 0f, 100f, 50f);

        QuadInstance left = Quad(1f, 2f, 30f, 20f, Rgba(10, 20, 30, 255));
        left.SlotIndex = 1u;
        left.ClipIndex = 1u;
        var leftSnap = new StreamSnapshot(
            new[] { left },
            ReadOnlySpan<uint>.Empty,
            new[] { default(ClipEntry), new ClipEntry { Rect = clipRect, Slot = 1u } },
            new[] { SlotEntry.Identity, Slot(m) },
            new[] { new SegmentDesc { Tex0 = new TexId(4), TexCount = 1, Start = 0, Count = 1 } },
            new[] { new RunOrder(0, 0) },
            7u, "left");

        QuadInstance right = Quad(1f, 2f, 30f, 20f, Rgba(10, 20, 30, 255));
        right.SlotIndex = 3u;
        right.ClipIndex = 2u;
        var rightSnap = new StreamSnapshot(
            new[] { right },
            ReadOnlySpan<uint>.Empty,
            new[] { default(ClipEntry), new ClipEntry { Rect = new Vector4(9f, 9f, 9f, 9f), Slot = 2u }, new ClipEntry { Rect = clipRect, Slot = 3u } },
            new[] { SlotEntry.Identity, Slot(Affine2D.TRS(new Vector2(99f, 99f), 0f, Vector2.one)), Slot(Affine2D.Identity), Slot(m) },
            new[] { new SegmentDesc { Tex0 = new TexId(4), TexCount = 1, Start = 0, Count = 1 } },
            new[] { new RunOrder(0, 0) },
            7u, "right（另一套池下标 + 残留条目）");

        Check("规范化哈希: 只差句柄/池下标 ⇒ 哈希相同、字节相同",
            CanonicalStream.Hash(leftSnap) == CanonicalStream.Hash(rightSnap)
            && CanonicalStream.Equal(leftSnap, rightSnap));
    }

    /// <summary>反向承诺：任何能改像素的位都必须被哈希看见。</summary>
    private static void CanonicalHashSeesGeometry()
    {
        StreamSnapshot Build(float x, uint color, BlendClass blend, int sortingOrder, uint epoch)
        {
            QuadInstance q = Quad(x, 2f, 30f, 20f, color);
            q.SlotIndex = 1u;
            return new StreamSnapshot(
                new[] { q },
                new uint[] { 0xFF112233u },
                new[] { default(ClipEntry) },
                new[] { SlotEntry.Identity, Slot(Affine2D.Identity) },
                new[] { new SegmentDesc { Tex0 = new TexId(4), TexCount = 1, Start = 0, Count = 1, Blend = blend } },
                new[] { new RunOrder(0, sortingOrder) },
                epoch, "probe");
        }

        ulong baseline = CanonicalStream.Hash(Build(1f, Rgba(10, 20, 30, 255), BlendClass.Normal, 0, 7u));
        ulong movedRect = CanonicalStream.Hash(Build(1.0001f, Rgba(10, 20, 30, 255), BlendClass.Normal, 0, 7u));
        ulong recolored = CanonicalStream.Hash(Build(1f, Rgba(10, 20, 31, 255), BlendClass.Normal, 0, 7u));
        ulong reblended = CanonicalStream.Hash(Build(1f, Rgba(10, 20, 30, 255), BlendClass.Add, 0, 7u));
        ulong resorted = CanonicalStream.Hash(Build(1f, Rgba(10, 20, 30, 255), BlendClass.Normal, 16, 7u));
        ulong reEpoched = CanonicalStream.Hash(Build(1f, Rgba(10, 20, 30, 255), BlendClass.Normal, 0, 8u));

        Check("规范化哈希: rect/color/blend/序/structEpoch 任一变化都改哈希",
            baseline != movedRect && baseline != recolored && baseline != reblended
            && baseline != resorted && baseline != reEpoched);
    }

    /// <summary>池残留（没人引用的槽与 clip 条目）不改一个像素，因此不进哈希。</summary>
    private static void CanonicalHashIgnoresUnreferencedResidue()
    {
        QuadInstance q = Quad(0f, 0f, 4f, 4f, Rgba(1, 2, 3, 255));
        var lean = new StreamSnapshot(
            new[] { q }, ReadOnlySpan<uint>.Empty,
            new[] { default(ClipEntry) }, new[] { SlotEntry.Identity },
            ReadOnlySpan<SegmentDesc>.Empty, ReadOnlySpan<RunOrder>.Empty);

        var withResidue = new StreamSnapshot(
            new[] { q }, ReadOnlySpan<uint>.Empty,
            new[] { default(ClipEntry), new ClipEntry { Rect = new Vector4(5f, 5f, 6f, 6f), Slot = 2u } },
            new[] { SlotEntry.Identity, Slot(Affine2D.TRS(new Vector2(50f, 50f), 0f, Vector2.one)), Slot(Affine2D.Identity) },
            ReadOnlySpan<SegmentDesc>.Empty, ReadOnlySpan<RunOrder>.Empty);

        Check("规范化哈希: 未被引用的槽/clip 残留不进哈希",
            CanonicalStream.Hash(lean) == CanonicalStream.Hash(withResidue));
    }

    /// <summary>门失败时要能指到字节：哈希说不等与字节 diff 指出的位置必须是同一个事实。</summary>
    private static void CanonicalBytesLocateFirstDifference()
    {
        QuadInstance a = Quad(1f, 2f, 3f, 4f, Rgba(1, 2, 3, 255));
        QuadInstance b = Quad(1f, 2f, 3f, 4f, Rgba(1, 2, 3, 255));
        b.Rect.x = 1.5f;

        StreamSnapshot Wrap(QuadInstance q) => new StreamSnapshot(
            new[] { q }, ReadOnlySpan<uint>.Empty, new[] { default(ClipEntry) },
            new[] { SlotEntry.Identity }, ReadOnlySpan<SegmentDesc>.Empty, ReadOnlySpan<RunOrder>.Empty);

        byte[] ba = CanonicalStream.Canonicalize(Wrap(a));
        byte[] bb = CanonicalStream.Canonicalize(Wrap(b));
        int diff = CanonicalStream.FirstDifference(ba, bb);

        // 头部 = magic(4) + version(1) + quadCount(4) = 9 字节，随后 4 字节是首个 quad 的 rect.x：
        // 1.0f 与 1.5f 只差指数/尾数的一个字节，所以首差异落在 [9,13) 内而不一定是 9 本身。
        Check("规范化字节: 首差异偏移落在首个 quad 的 rect.x 那 4 个字节内",
            diff >= 9 && diff < 13 && ba.Length == bb.Length
            && CanonicalStream.FirstDifference(ba, ba) == -1
            && !CanonicalStream.Equal(Wrap(a), Wrap(b)));
    }
}
