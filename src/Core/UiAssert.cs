using System.Diagnostics;

namespace FairyNext.Core;

/// <summary>
/// 不变量执法点。架构文档里凡写「debug 断言」的门都落在这里：
/// <see cref="That"/> 带 <c>[Conditional("DEBUG")]</c>——release 构建下**调用点整体消失**
/// （连参数表达式一起消失，所以门里的 O(depth) 环检测在 release 不花钱）。
///
/// 明文边界：断言只负责「响亮」。架构文档要求的降级行为（如相位写门 release 下
/// 「Mark 照记但入下帧队列」）必须写在断言**之外**的普通分支里，否则 release 行为跟着断言一起蒸发。
///
/// 测试装 <see cref="Handler"/> 接管失败：门本身要能被单测断言「确实响了」，
/// 而不是让进程 FailFast（这也是不用 System.Diagnostics.Debug.Assert 的原因——
/// 它在 .NET Core 无调试器时直接终止进程，门无法被验证）。
/// </summary>
public static class UiAssert
{
    /// <summary>非 null 时接管失败（测试用，不抛）；为 null 时抛 <see cref="InvalidOperationException"/>。</summary>
    public static Action<string>? Handler;

    /// <summary>debug 门。condition 为假即失败；release 下本调用不存在。</summary>
    [Conditional("DEBUG")]
    public static void That(bool condition, string message)
    {
        if (!condition) Fail(message);
    }

    /// <summary>无条件失败（非 Conditional：给 release 也必须响的路径用）。</summary>
    public static void Fail(string message)
    {
        var h = Handler;
        if (h != null) { h(message); return; }
        throw new InvalidOperationException("FairyNext 门断言失败：" + message);
    }
}
