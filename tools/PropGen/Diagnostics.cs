using Microsoft.CodeAnalysis;

namespace FairyNext.PropGen
{
    /// <summary>
    /// 诊断码表（形态承自 fork 的 FGM 体系：一码一缺陷、消息带具体名字、定位到声明点）。
    /// 前缀改 FNP 而不是沿用 FGM：FGM 是 FairyGUI.Mvvm 的账，同码不同义会让两个仓库的诊断串味。
    ///
    /// 全部为 <see cref="DiagnosticSeverity.Error"/>——本生成器守的是「通道归属封闭」（架构不变量 1），
    /// 它的失败形态必须是**编译错**：降为警告等于把不变量退回人工纪律。
    /// </summary>
    internal static class Diag
    {
        internal const string Category = "FairyNext.PropGen";

        internal static readonly DiagnosticDescriptor MissingOwnership = Error(
            "FNP001", "属性缺通道归属",
            "PropId '{0}'（id={1}）在 ABI 单源里已启用，但归属表没有对应的 [NodeProp] 声明——"
            + "通道归属封闭要求每个 PropId 有且仅有一个 Ch 归属");

        internal static readonly DiagnosticDescriptor OrphanDeclaration = Error(
            "FNP002", "归属声明没有对应的 PropId",
            "[NodeProp(\"{0}\")] 在 ABI 单源（Abi.PropIds → AbiLayout.PropId*）里查不到——"
            + "属性 id 的定义点只有 src/Contracts/Abi.cs 一处");

        internal static readonly DiagnosticDescriptor DuplicateDeclaration = Error(
            "FNP003", "属性重复声明",
            "属性 '{0}' 被声明了不止一次——一个 PropId 只能有一个通道归属");

        internal static readonly DiagnosticDescriptor BadChannel = Error(
            "FNP004", "通道归属不是单个上行通道位",
            "属性 '{0}' 的 Channel 必须是**单个**上行通道位（ChMask.Up 内的一位），实得 0x{1}——"
            + "级联用的下行位走 Down，派生位不允许作归属");

        internal static readonly DiagnosticDescriptor BadDownChannel = Error(
            "FNP005", "下行伴随位非法",
            "属性 '{0}' 的 Down 必须落在 ChMask.Down 内，实得 0x{1}");

        internal static readonly DiagnosticDescriptor BadStorage = Error(
            "FNP006", "存储声明不完整或与列不符",
            "属性 '{0}'（Store={1}）的存储声明无效：{2}");

        internal static readonly DiagnosticDescriptor MissingHostSymbol = Error(
            "FNP007", "生成器依赖的宿主符号缺失",
            "找不到 {0}——生成器按符号名与运行时耦合，改名/挪命名空间必须同步改生成器");

        internal static readonly DiagnosticDescriptor UnsupportedTable = Error(
            "FNP008", "归属表类型形态不支持",
            "类型 '{0}' {1}——归属表必须是顶层、非泛型、partial 的类，否则生成的分部会另起一个新类型");

        internal static readonly DiagnosticDescriptor BadPropName = Error(
            "FNP009", "属性名不是合法标识符",
            "属性名 '{0}' 不能作为 C# 标识符——生成的 Write{0}/Store{0} 无法编译");

        private static DiagnosticDescriptor Error(string id, string title, string message) =>
            new DiagnosticDescriptor(id, title, message, Category, DiagnosticSeverity.Error, true);
    }
}
