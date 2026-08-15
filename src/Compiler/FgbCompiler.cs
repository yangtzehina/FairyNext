namespace FairyNext.Compiler;

/// <summary>
/// .fui → FGB 编译器门面（M1 落地）。
/// 宪法约束：编译 = 对着无头运行时跑一遍再冻结产物——不存在第二套布局/排序实现（承诺 8）。
/// .fui 前端（两级块表 ByteBuffer 读取器）只活在本工程；发布版运行时不含 .fui 解析。
/// 每次编译打印内存计划（blob 字节 / 实例块字节 / 池预算 / 掩码占用率——v1.2）。
///
/// 前端已就位（M1-12，见 <c>Fui/</c>）：<see cref="Fui.FuiPackage.TryParse"/> 出包描述符，
/// <see cref="Fui.FuiComponent.TryParse"/> 出显示列表数据模型（本期不解释的部分留 span）。
/// 缺的是消费者：M1-19 的 FGB 段写入器与 M1-20 的「建树 → 跑 P5 + 度量 → Extract → 冻结」。
/// </summary>
public static class FgbCompiler
{
    public static CompileResult Compile(ReadOnlyMemory<byte> fuiBytes, in CompileOptions options)
        => throw new NotImplementedException("M1-19/M1-20：FGB 段写入器 + 建树/布局/Extract/冻结（.fui 前端见 Fui/）");
}

public readonly struct CompileOptions
{
    public readonly int ScaleLevel;
    public readonly int BranchId;
    public CompileOptions(int scaleLevel, int branchId) { ScaleLevel = scaleLevel; BranchId = branchId; }
}

public sealed class CompileResult
{
    public ReadOnlyMemory<byte> Blob { get; }
    public string MemoryPlan { get; }     // 人读账单，同时是测试断言锚（v1.2）
    public string ReactiveGraph { get; }  // 绑定表/拓扑序/掩码的规范文本 golden（v1.2）

    public CompileResult(ReadOnlyMemory<byte> blob, string memoryPlan, string reactiveGraph)
    {
        Blob = blob; MemoryPlan = memoryPlan; ReactiveGraph = reactiveGraph;
    }
}
