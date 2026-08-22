using System.Collections.Generic;
using System.Text;

namespace FairyNext.Compiler;

/// <summary>FGM 诊断级别。Error = 该编译单元（组件或整包）不产出产物；Warning/Info 不阻断。</summary>
public enum FgmSeverity : byte
{
    /// <summary>信息（明码边界：某形态按 M1 面拒入编译，运行期有声降级或后续里程碑进驻）。</summary>
    Info = 0,
    /// <summary>警告（语义可疑但可继续：跳过该条目或用回退值）。</summary>
    Warning = 1,
    /// <summary>错误（编译失败面：结构性拒收 / L0 门 / 编译器自检红）。</summary>
    Error = 2,
}

/// <summary>
/// FGM 诊断码词表（**append-only，永不重编号**——与 FgbGate 同一条纪律：
/// 诊断码会进 CI 日志与工具链的过滤器，重编号 = 老日志不可读）。
/// 分组：0xx 前端拒收、1xx 约束编译、2xx GGroup、3xx 内容/资源、9xx 编译器自检。
/// </summary>
public static class FgmCodes
{
    /// <summary>.fui 包描述符拒收（FuiPackage.TryParse 的诊断原文附在消息里）。Error。</summary>
    public const string PackageRejected = "FGM001";

    /// <summary>组件显示列表拒收（FuiComponent.TryParse 失败）。Error。</summary>
    public const string ComponentRejected = "FGM002";

    /// <summary>孩子记录的类型专属块读取失败或字段非法——几何保留、该孩子内容/文本跳过。Warning。</summary>
    public const string ChildBlockRejected = "FGM003";

    /// <summary>约束环拒绝（L0 门）：分层后图层内成环，消息携带闭合环路径（编辑器 id 序列）。Error。</summary>
    public const string CycleRejected = "FGM101";

    /// <summary>约束声明非法：每轴被绑标量 &gt;2 / 同边双写 / 锚定配对非法（Seal 的其余拒绝面）。Error。</summary>
    public const string ConstraintInvalid = "FGM102";

    /// <summary>组件自身关系未编译（M1 面：容器由外层图或 authored 定形，dst=局部 0 无写口）。Warning。</summary>
    public const string OwnerRelationSkipped = "FGM103";

    /// <summary>关系目标非法（下标越界 / 指向自己），该条关系跳过。Warning。</summary>
    public const string RelationTargetInvalid = "FGM104";

    /// <summary>GGroup 组员在显示列表上不连续（真节点化后组块整体占组的绘制位，序可能偏移）。Warning。</summary>
    public const string GroupNotContiguous = "FGM201";

    /// <summary>跨组百分比**位置**关系未编译：组坐标系错切边界（M1 面；百分比尺寸空间不变、照编）。Warning。</summary>
    public const string GroupCrossPercentSkipped = "FGM202";

    /// <summary>组员归属非法（groupId 指向的孩子不是组 / 越界 / 组链成环），按无组处理。Warning。</summary>
    public const string GroupMembershipInvalid = "FGM203";

    /// <summary>字体映射缺失（机制 13：编译诊断而非运行时缺字）——位图字体 ui:// 引用、未注册
    /// 的具名字体、或整包无字体注册。文本节点保几何，内容/度量按情形跳过或回退。Warning。</summary>
    public const string FontUnmapped = "FGM301";

    /// <summary>资源引用缺失或跨包（内容留待装载期 PTCH/DEPS，M1-22）。Info。</summary>
    public const string ResourceUnresolved = "FGM302";

    /// <summary>内容形态未进 M1 编译面（MovieClip / 线框与圆角 Graph / Loader / 裁白与旋转入图集 /
    /// UBB 富文本 / 双值 skew …）——节点保几何，内容按有声原则不编。Info。</summary>
    public const string ContentNotInM1 = "FGM303";

    /// <summary>编译期 P5 未在受控轮次内收敛（编译器内部错误：布局有未静态断开的反向依赖）。Error。</summary>
    public const string ShapeNotConverged = "FGM901";

    /// <summary>编译期布局门红：P5 幂等 / 布局差分神谕 / 迟到槽分配非零——
    /// 「编译器 = 无头运行时」的自检失败（编译器 bug，不是资产问题）。Error。</summary>
    public const string ShapeGateRed = "FGM902";
}

/// <summary>一条 FGM 诊断。</summary>
public readonly struct FgmDiagnostic
{
    /// <summary>诊断码（<see cref="FgmCodes"/>）。</summary>
    public readonly string Code;
    /// <summary>级别。</summary>
    public readonly FgmSeverity Severity;
    /// <summary>位置（"组件名" 或 "组件名/孩子编辑器id"；包级为空串）。</summary>
    public readonly string Site;
    /// <summary>人读消息。</summary>
    public readonly string Message;

    public FgmDiagnostic(string code, FgmSeverity severity, string site, string message)
    {
        Code = code; Severity = severity; Site = site; Message = message;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        Code + " " + (Severity == FgmSeverity.Error ? "error" : Severity == FgmSeverity.Warning ? "warning" : "info")
        + (Site.Length == 0 ? "" : " @" + Site) + ": " + Message;
}

/// <summary>
/// 编译诊断集（一次 Shape/Compile 的全部 FGM 条目，append-only）。
/// <see cref="ToString"/> 输出确定性逐行文本——测试直接断言，也是 CI 日志形态。
/// </summary>
public sealed class CompileDiagnostics
{
    private readonly List<FgmDiagnostic> _items = new List<FgmDiagnostic>();

    /// <summary>全部条目（添加序）。</summary>
    public IReadOnlyList<FgmDiagnostic> Items => _items;

    /// <summary>存在 Error 级条目。</summary>
    public bool HasErrors { get; private set; }

    internal void Add(string code, FgmSeverity severity, string site, string message)
    {
        _items.Add(new FgmDiagnostic(code, severity, site, message));
        if (severity == FgmSeverity.Error) HasErrors = true;
    }

    /// <summary>指定码的条目数。</summary>
    public int CountOf(string code)
    {
        int n = 0;
        for (int i = 0; i < _items.Count; i++)
            if (_items[i].Code == code) n++;
        return n;
    }

    /// <summary>是否存在指定码的条目。</summary>
    public bool Has(string code) => CountOf(code) > 0;

    /// <summary>首个指定码的条目（不存在返回 false）。</summary>
    public bool TryFirst(string code, out FgmDiagnostic diagnostic)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].Code != code) continue;
            diagnostic = _items[i];
            return true;
        }
        diagnostic = default;
        return false;
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _items.Count; i++) sb.AppendLine(_items[i].ToString());
        return sb.ToString();
    }
}
