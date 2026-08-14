using FairyNext.State;
using FairyNext.State.Anim;

namespace FairyNext.Tests;

/// <summary>
/// M1-03 fork 直搬包 L1 回归：ViewModel 掩码 / Binder 重入四件套（V1/V6/V7/V8，
/// 移植自 fork Assets/Examples/Mvvm/BinderReentrancyCheck.cs @ d1a9d7d）/
/// CommandQueue 环形扩容 / KeyedListDiffer render-后-记账 / EaseManager 数值抽查。
/// </summary>
public static partial class Program
{
    /// <summary>与 fork 检查器同构的最小 VM：位 0 = A、位 1 = B。</summary>
    private sealed class Vm : ViewModel
    {
        public int A, B;
        public void SetA(int v) { A = v; MarkDirty(0); }
        public void SetB(int v) { B = v; MarkDirty(1); }
    }

    private static void StateSuite()
    {
        ViewModelMask();
        BinderV1CascadeSurvives();
        BinderV1SelfRemark();
        BinderV6UnbindMidFlush();
        BinderV7NestedFlush();
        BinderV8TombstoneMidGroup();
        CommandQueueRing();
        KeyedListDifferSuite();
        EaseSpotChecks();
    }

    private static void ViewModelMask()
    {
        var vm = new Vm();
        Check("VM: 初始全净", !vm.IsDirty(0) && !vm.IsDirty(63));
        vm.MarkDirty(3);
        Check("VM: MarkDirty 只置本位", vm.IsDirty(3) && !vm.IsDirty(2) && !vm.IsDirty(4));
        vm.MarkAllDirty();
        Check("VM: MarkAllDirty 覆盖 64 位", vm.IsDirty(0) && vm.IsDirty(63));

        // 清除快照语义：Flush 只清快照位——apply 期间新写的位存活（ClearDirty(mask) 而非全清）
        var vm2 = new Vm();
        var binder = new Binder();
        binder.Bind(vm2, 0, () => vm2.MarkDirty(1), applyNow: false);
        vm2.SetA(1);
        binder.Flush();
        Check("VM: Flush 清快照位、apply 期新脏位存活", !vm2.IsDirty(0) && vm2.IsDirty(1));
    }

    private static void BinderV1CascadeSurvives()
    {
        var vm = new Vm();
        var binder = new Binder();
        int bApplied = 0;
        binder.Bind(vm, 0, () => vm.SetB(42), applyNow: false); // A 的 apply 写 B
        binder.Bind(vm, 1, () => bApplied++, applyNow: false);
        vm.SetA(1);
        binder.Flush(); // 冲 A 位；B 位在本次 flush 期间被置
        int afterFirst = bApplied;
        binder.Flush(); // 现在必须 apply B
        Check("Binder V1: apply 期级联写存活到下帧", bApplied == afterFirst + 1);
        binder.Flush();
        Check("Binder V1: 净帧不重复 apply", bApplied == afterFirst + 1);
    }

    private static void BinderV1SelfRemark()
    {
        var vm = new Vm();
        var binder = new Binder();
        int applied = 0;
        binder.Bind(vm, 0, () => { applied++; if (applied == 1) vm.SetA(2); }, applyNow: false);
        vm.SetA(1);
        binder.Flush();
        binder.Flush();
        Check("Binder V1: apply 内重标自身下帧再 apply", applied == 2);
    }

    private static void BinderV6UnbindMidFlush()
    {
        var vm1 = new Vm();
        var vm2 = new Vm();
        var vm3 = new Vm();
        var binder = new Binder();
        int applied2 = 0, applied3 = 0;
        binder.Bind(vm1, 0, () => binder.Unbind(vm2), applyNow: false);
        binder.Bind(vm2, 0, () => applied2++, applyNow: false);
        binder.Bind(vm3, 0, () => applied3++, applyNow: false);
        vm1.SetA(1); vm2.SetA(1); vm3.SetA(1);
        bool threw = false;
        try { binder.Flush(); } catch { threw = true; }
        Check("Binder V6: flush 中 Unbind 不抛", !threw);
        Check("Binder V6: tombstone 组被跳过", applied2 == 0);
        Check("Binder V6: 后续组照常 apply", applied3 == 1);
    }

    private static void BinderV7NestedFlush()
    {
        var vm1 = new Vm();
        var vm2 = new Vm();
        var binder = new Binder();
        int applied2 = 0;
        bool threw = false;
        binder.Bind(vm1, 0, () => binder.Flush(), applyNow: false); // apply 内嵌套 Flush
        binder.Bind(vm2, 0, () => applied2++, applyNow: false);
        vm1.SetA(1); vm2.SetA(1);
        try { binder.Flush(); } catch { threw = true; }
        Check("Binder V7: 嵌套 Flush 不抛（分层 scratch）", !threw);
        Check("Binder V7: 嵌套后外层快照仍可用", applied2 > 0);
    }

    private static void BinderV8TombstoneMidGroup()
    {
        var vm = new Vm();
        var binder = new Binder();
        int later = 0;
        // 同一 vm 两个条目：第一条解绑自身，第二条不得再跑（真实视图中会写进已释放对象）
        binder.Bind(vm, 0, () => binder.Unbind(vm), applyNow: false);
        binder.Bind(vm, 1, () => later++, applyNow: false);
        vm.SetA(1); vm.SetB(1);
        binder.Flush();
        Check("Binder V8: 同组自解绑后条目不再跑", later == 0);
    }

    private static void CommandQueueRing()
    {
        var q = new CommandQueue<int>(4);
        q.Enqueue(1); q.Enqueue(2); q.Enqueue(3);
        q.TryDequeue(out int first);          // head 前移，后续入队回绕
        q.Enqueue(4); q.Enqueue(5);           // 填满 4 槽（含回绕）
        q.Enqueue(6);                         // 触发扩容，须按逻辑序搬运
        var drained = new List<int>();
        while (q.TryDequeue(out int v))
            drained.Add(v);
        Check("CommandQueue: 环形回绕+扩容后排空序 FIFO",
            first == 1 && string.Join(",", drained) == "2,3,4,5,6");
        Check("CommandQueue: 排空后 count=0 且 TryDequeue false",
            q.count == 0 && !q.TryDequeue(out _));
    }

    private static void KeyedListDifferSuite()
    {
        var items = new List<(int Id, string Val)> { (1, "a"), (2, "b") };
        var differ = new KeyedListDiffer<(int Id, string Val), int>(it => it.Id);

        Check("Differ: 首次（计数 0→2）全量渲染", differ.Apply(items, _ => { }) == 2);
        Check("Differ: key 未变零渲染", differ.Apply(items, _ => { }) == 0);

        // render-后-记账：render 抛异常 → 旧 key 保留，下次 Apply 重试该行
        items[1] = (3, "b");
        bool threw = false;
        try { differ.Apply(items, _ => throw new InvalidOperationException("render 失败")); }
        catch (InvalidOperationException) { threw = true; }
        var retried = new List<int>();
        int n = differ.Apply(items, i => retried.Add(i));
        Check("Differ: render 抛异常旧 key 保留、下次重试该行",
            threw && n == 1 && retried.Count == 1 && retried[0] == 1);

        // 计数变化 → 全量（即便已有 key 全部未变）
        items.Add((4, "c"));
        Check("Differ: 计数变化回退全量", differ.Apply(items, _ => { }) == 3);
    }

    private static void EaseSpotChecks()
    {
        static bool Near(float a, float b) => Math.Abs(a - b) < 1e-5f;
        Check("Ease: Linear(0.25/1)=0.25",
            Near(EaseManager.Evaluate(EaseType.Linear, 0.25f, 1f), 0.25f));
        // QuadOut: -(t)(t-2)，t=0.25 → 0.4375
        Check("Ease: QuadOut(0.25/1)=0.4375",
            Near(EaseManager.Evaluate(EaseType.QuadOut, 0.25f, 1f), 0.4375f));
        // BounceOut 第一段（t<1/2.75）：7.5625·t²，t=0.2 → 0.3025
        Check("Ease: BounceOut(0.2/1)=0.3025",
            Near(EaseManager.Evaluate(EaseType.BounceOut, 0.2f, 1f), 0.3025f));
        // 偏离 fork 的 Custom 分支：Func<float,float> 评估器直通
        Check("Ease: Custom 评估器 t²(0.5)=0.25",
            Near(EaseManager.Evaluate(EaseType.Custom, 0.5f, 1f, customEase: t => t * t), 0.25f));
    }
}
