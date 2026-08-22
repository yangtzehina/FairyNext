using FairyNext.Contracts;
using FairyNext.Core;

namespace FairyNext.Tests;

/// <summary>
/// 自建 runner：每条用例一行 PASS/FAIL，末行判定 RESULT pass=N fail=N（机器可判，CI grep 即门）。
/// </summary>
public static partial class Program
{
    private static int _pass, _fail;

    public static int Main()
    {
        HandlePackRoundtrip();
        HandleNoneSemantics();
        AbiSanity();
        PhaseTableFrozen();
        NumericsSuite();   // M1-02，用例在 NumericsTests.cs（partial）
        StateSuite();      // M1-03 直搬包，用例在 StateTests.cs（partial）
        AdjacencySuite();  // M1-03 渲染平面纯函数 golden，用例在 AdjacencySorterTests.cs（partial）
        AbiCodegenSuite(); // M1-01 ABI 字节比对门 + 布局表自洽，用例在 AbiCodegenTests.cs（partial）
        OracleGoldenSuite(); // M1-04 oracle golden 管线（无 Unity 依赖），用例在 OracleGoldenTests.cs（partial）
        NodeTableSuite();   // M1-06 数据宪法不变量，用例在 NodeTableTests.cs（partial）
        InvalidationSuite(); // M1-07 失效协议（时间宪法不变量），用例在 InvalidationTests.cs（partial）
        UiKernelSuite();     // M1-08 相位机 + Clock（时间宪法不变量 9/10/11/14），用例在 UiKernelTests.cs（partial）
        NodePropsSuite();    // M1-09 setter codegen 的运行期面（归属表自洽 / 逐 PropId 等值切断 / 来源分流）
                             // 生成期面另有独立门：dotnet run --project tools/PropGen.Tests
        MockBackendSuite();  // M1-10 mock 后端 + 参考光栅 + 规范化流哈希 + GateReport 七字段，
                             // 用例在 MockBackendTests.cs（partial）——L2 行为门与 L3 回放门的宿主
        RenderStreamSuite(); // M1-11 RenderStream 核心（CPU 镜像 SoA / 段键 / SlotTable / ClipBook /
                             // 颜色 tier / 提交路径），用例在 RenderStreamTests.cs（partial）
        LeafEmitterSuite();  // M1-13 叶发射器（九宫格切片与 UV / 线性与径向填充 / 填充参数的 aux-extra
                             // 位布局）+ QuadReassembler 移植件，用例在 LeafEmitterTests.cs（partial）
        ExtractSuite();      // M1-13 Extract（paintOrder 遍历 → 相邻性排序 → 切段）与
                             // Structure 通道的整流重编，用例在 ExtractTests.cs（partial）
        PipelineSuite();     // M1-14 P6/P7/P8 排水打通（切片拼接 / 派生列增量 / 下行下钻 / 五通道 /
                             // 合并区间上传）+ **增量正确性门（L2）与零脏帧空转收据**上线，
                             // 正例负例各一，用例在 PipelineTests.cs（partial）
        FuiReaderSuite();    // M1-12 .fui 前端读取器（ByteBuffer 移植 + 包/组件/显示列表解析）：
                             // 对 tests/fixtures/fui 的真实样例包做字段级对照（对照物 = 编辑器授权 XML），
                             // 用例在 FuiReaderTests.cs（partial）
        LayoutSuite();       // M1-16 布局：约束图（拓扑序/分层拒环/offset 捕获）+ LinearLayout +
                             // parentUsesSize 受控窗 + **P5 幂等断言与布局差分神谕（L2）**上线，
                             // 正例负例各配，用例在 LayoutTests.cs（partial）
        BackendM15Suite();   // M1-15 Core/mock 半边：多面板扇出件（PanelFanout——钩子单占下的
                             // 分发/路由/帧边界安全）+ **上传字节神谕**（MirrorProbe：上传区间
                             // 逐字节对拍 CPU 镜像 + 帧末全量对拍），用例在 BackendM15Tests.cs（partial）
        TextGlyphSuite();    // M1-17 字形源 + GlyphStore：TTF 解析 golden（合成钉字节 + Monaco
                             // 独立神谕）/ cmap format 4+12 直映 / CFF 拒载有声 / 三本账
                             // append-only / 页淘汰双门 / generation 仅 P2 / 双半区独立，
                             // 用例在 TextGlyphTests.cs（partial）
        TextCoreSuite();     // M1-18 TextCore 无状态排版：断行/对齐/ellipsis golden + 两把尺 +
                             // kerning 求和 + 文本·不变量 1/2/3（跨 DPI 位等/双跑/arena 复用）+
                             // P5 度量层（autoSize/回流）+ 三级惰性与 slack 档 + Pending α=0 +
                             // 页引用纪律 + 核按钮端到端，用例在 TextCoreTests.cs（partial）
        FgbSuite();          // M1-19 FGB blob 写读：容器门矩阵（magic/版本/flags/目录越界/对齐/
                             // selfHash）+ Cast 直读 + 写读往返与确定性 + NODE 列序三方对账
                             // （生成物 ⇄ Abi 表 ⇄ NodeTable 实际列）+ FNV fork 对拍 +
                             // fuzz 雏形（1200 变体 seed 固定；M1-22 转常设门），
                             // 用例在 FgbTests.cs（partial）
        CompilerShapeSuite(); // M1-20a FgbCompiler 前半：.fui → 建树（GGroup 真节点化 /
                             // pivotAsAnchor 编译期消灭 / localId 映射）→ relation → 约束编译
                             //（**约束环拒绝 FGM101（L0）上线**）→ 编译期 P5 + 度量（复用
                             // TextSystem/LayoutEngine，LateSlotAllocs 恒零升门 FGM902），
                             // 用例在 CompilerShapeTests.cs（partial）
        CompilerFreezeSuite(); // M1-20b FgbCompiler 后半：已定形的树 → 编译期 Extract → canonical
                             // 去重 → 十一段冻结 → 内存计划打印。三道门上线：**编译产物 golden（L1，
                             // 四包 FGB 逐字节 + 人读账单入 tests/goldens/fgb）**、**等价性金样
                             //（不变量 18：编译期 Extract == 运行时管线 Extract 的 CanonicalStream
                             // 逐字节，六包 25 组件）**、**FGB 读回 sanity（NODE/COMP/CNST/LOCL/
                             // STRT/LEAF 逐段与原树对账）**；另有 canonical 后置扫描（不变量 8）、
                             // 内存计划断言锚、冻结前置门 FGM903，用例在 CompilerFreezeTests.cs（partial）
        EventSuite();        // M1-21 事件平面：命中（迭代下行 / 上帧序 / local⊗slotMatrix / clip 剪枝 /
                             // 1bit 位图）+ 链快照派发与句柄双验 + downChain click + CaptureTouch/monitor
                             // + P0 接线与相位纪律；另结两笔 2026-08 审计遗留账（DownLayer 覆盖与裁决、
                             // 命中/渲染 clip 同判），用例在 EventTests.cs（partial）

        Console.WriteLine($"RESULT pass={_pass} fail={_fail}");
        return _fail == 0 ? 0 : 1;
    }

    private static void HandlePackRoundtrip()
    {
        var h = new NodeHandle(0xABCDEF, 0x1234, 0x0007);
        var r = NodeHandle.Unpack(h.Pack());
        Check("NodeHandle Pack/Unpack 往返", r.Equals(h) && r.Index == 0xABCDEF && r.Gen == 0x1234 && r.Tree == 7);
    }

    private static void HandleNoneSemantics()
    {
        Check("NodeHandle.None 语义", NodeHandle.None.IsNone && !new NodeHandle(1, 0, 0).IsNone
            && NodeHandle.Unpack(0).IsNone);
    }

    private static void AbiSanity()
    {
        Check("Abi: quad 16B 对齐", Abi.QuadInstanceSize % 16 == 0);
        Check("Abi: 段纹理槽 2bit 可编码", Abi.SegmentMaxTextures <= 4);
        Check("Abi: 掩码硬顶 = 单 word", Abi.MaxObservableProps == 64);
        Check("Abi: FGB magic = FGB1", Abi.FgbMagic == 0x31424746);
    }

    private static void PhaseTableFrozen()
    {
        // 法定词表冻结：编号即契约（设计书 §02），挪动任何相位 = 破坏所有接缝。
        Check("FramePhase 法定编号冻结",
            (byte)FramePhase.P0_Input == 0 && (byte)FramePhase.P4_State == 4 &&
            (byte)FramePhase.P7_RenderDrain == 7 && (byte)FramePhase.P9_FrameEnd == 9);
    }

    private static void Check(string name, bool ok)
    {
        if (ok) { _pass++; Console.WriteLine($"PASS {name}"); }
        else { _fail++; Console.WriteLine($"FAIL {name}"); }
    }
}
