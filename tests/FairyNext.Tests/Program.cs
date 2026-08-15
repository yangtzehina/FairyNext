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
