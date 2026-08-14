namespace FairyNext.Contracts;

// ============================================================================
// ABI 常量单一事实源（设计书 v1.2「契约执法机构」①②）。
//
// 程序性纪律（append-only 定义点纪律，改动前必读）：
//  1. 改任何值 → 跑 tools/codegen 重新生成 HLSL include 与 mock 常量 → 提交生成物；
//  2. 改了二进制布局（大小/偏移/位域）→ bump 对应 FormatVersion；
//  3. id/位域 append-only：永不重编号、永不复用；分组之间留数字 gap 供增长；
//  4. CI 在内存重新生成并与已提交生成物逐字节比较——漂移即红灯。
//  生成器必须确定性：无日期、无环境变量、仅按声明序输出。
// ============================================================================
public static class Abi
{
    // ---- FGB 容器（设计书 §4.8）----
    public const uint FgbMagic = 0x31424746;       // "FGB1" little-endian
    public const int FgbFormatVersion = 1;         // 布局变更必 bump（结构性不符 → 拒载）
    public const int FgbSectionAlignment = 16;     // 段 16B 对齐，定长记录 MemoryMarshal.Cast 直读

    // ---- Quad 实例流（设计书 §4.2）----
    public const int QuadInstanceSize = 80;        // bytes/实例，16B 对齐；布局变更必 bump ShaderAbiVersion
    public const int SegmentMaxTextures = 4;       // 段纹理槽（2bit 编码；WR TextureSet[3]+mask 独立收敛）
    public const int ClipEntrySize = 48;           // rect + soft + radii + slot；条目 0 = None 哨兵
    public const int PaintOrderStride = 16;        // 渲染单元 sortingOrder = paintOrder 下标 × 16（裁决 3）
    public const int ShaderAbiVersion = 1;

    // ---- 顶点流后端预算（fork 实测水位起步，溢出走阶梯降级 + 诊断高水位）----
    public const int TransformSlotBudget = 32;     // 槽 0 = identity
    public const int ClipEntryBudget = 16;         // Inherited/Owned 继承共享后按「裁剪域数」计（v1.3）

    // ---- 状态层（设计书 §4.4）----
    public const int MaxObservableProps = 64;      // 64bit 脏掩码硬顶：超出 = 编译错误，不做多 word 回退（v1.1）
    public const int CommandWaveLimit = 4;         // P3 波次上限，超限延帧 + 责任链诊断
    public const int LayoutMicroDrainLimit = 3;    // P5 兜底轮次（不变式快路径优先，v1.3）
}
