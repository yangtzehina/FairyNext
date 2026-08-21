# FairyNext 全程开发计划（M1 骨架 → M2 完整运行时 → M3 生产化）

## Context

FairyNext（FairyGUI 运行时 green-field 重写）已完成：设计书 v1.3（十条承诺/法定帧协议/14 裁决，吸收业界对标 25 条 + PocketJS 分析）、定稿系统架构（六平面 + 不变量清单）、仓库骨架（`~/ECS/FairyNext` @ 4a95fb3，214 行 C#，构建与 7 条测试全绿，oracle.lock 钉 fork @ d1a9d7d）。本计划把设计落成从当前骨架到最终版本的**完整 WBS**：M1 26 包（周级，~14 周）、M2 16 包、M3 8 包，含门上线时间表、移植清单落位、风险对策落位、每期明确不做。尺度：单人 + AI 代理协作，**一个工作包 ≈ 一个代理会话可交付**。

已确认事实（探索产出）：fork 中零 Unity 依赖可直搬 = AdjacencySorter 74 / Binder 228 / KeyedListDiffer 85 / ViewModel 54 / CommandQueue 63 / Bind+Observable 属性 47 / EaseManager 247（3 处 Mathf.PI，保留 BSD 头）/ UBBParser 226（去 static 单例）；ByteBuffer 465 仅 2 处改写；Fqs blob IO+FNV 段 ~340 行仅 1 处 Mathf.Clamp；QuadReassembler 165 与 PixelHitTest 82 需数学 shim；Roslyn 生成器 498 行零程序集引用（仅字符串符号耦合，可改造）；CurveFontStore 纯 TTF 解析段 278-597（54%）可搬；ScrollPane 惯性公式行号 2040-2318 内约 200 行（抄公式）；InstancedUIStream 2955 行属参考重写。

**批准后立即执行**：① 本计划落库 `~/ECS/FairyNext/docs/plan.md` + Obsidian `21 号笔记` + 提交推送；② 开工 W1-W2 四个并行包（M1-01/02/03/04）。

---

## 1. 关键路径与并行轨

**串行主链**：M1-01 ABI codegen → M1-06 NodeTable → M1-07 失效协议 → M1-08 相位机 → M1-11 RenderStream → M1-14 排水打通（增量门上线）→ M1-16 布局 P5（幂等+神谕上线）→ M1-18 TextCore 度量 → **M1-20 FgbCompiler**（=无头运行时，须等布局/文本/Extract 就绪——全计划最硬排序约束）→ M1-22 装载+实例化 → M1-26 M1 验收 → M2 状态层（01→02→03）→ M2-06 滚动 → M2-07 虚拟列表 → M2-14 像素全矩阵（M2 收口）→ M3。

**四条并行副轨**：mock 后端轨（M1-10，只依赖 ABI，阻塞一切 L2 行为门）；资产前端轨（M1-12 .fui 读取器 → M1-19 FGB 读写）；oracle 基建轨（M1-04，W1 开工，先于大规模功能）；WebGL2 风险压测轨（M1-05，首月出结论回填 Abi 预算）。

**真依赖**：shim 数学库阻塞 QuadReassembler/PixelHitTest；dirtyWord 列阻塞失效队列阻塞一切排水；mock 阻塞全部行为门；.fui 前端→FGB→实例化→journey；布局+文本度量阻塞编译器；SlotTable 阻塞 local⊗slotMatrix 命中；Manual 时域+PostInput 单口+mock 共同阻塞回放门。

## 2. M1 骨架（14 周，26 包）

### W1-W2 地基（四包并行）
- **M1-01 ABI 单源 codegen**（~600 行）：Abi.cs 升唯一纯数据定义点（80B 偏移/位域、48B ClipEntry、Ch/PropId/FGB 记录布局）；codegen 生成 C# 偏移断言/mock 常量/HLSL include；CI 内存重生成逐字节比对。门：**ABI 字节比对（L0）上线**。
- **M1-02 数学 shim 库**（~400 行）：`FairyNext.Numerics`——Vector2/4、Color32、Rect、Affine2D、MathF、FNV-1a；API 面按被移植件调用面裁剪。
- **M1-03 fork 直搬包**（551+247 行）：AdjacencySorter/KeyedListDiffer/CommandQueue/Binder/ViewModel/属性 直搬；EaseManager 直搬（Mathf.PI→MathF.PI，保 BSD 头）；配 L1 单测。Binder/VM 只入库，挂接在 M2-05。门：**L1 纯函数回归上线**。
- **M1-04 oracle 对拍基建**（~500 行）：UniCli 驱动脚本化（fork 建场景/截帧/导布局数值）、golden 格式（PNG+json）、容差比对器、首张基线。风险#1 地基。

### W2-W4 副轨
- **M1-05 WebGL2 真机 uniform 预算压测**（~800 行 spike + 报告，**首月必须出结论**）：手写最小 WebGL2 页（顶点流+32 槽+16 clip+4 采样器）低端 Android/iOS 真机跑；产出 BenchHeader 法定格式首份报告；结论回填 Abi.cs。风险#3 落位；**bench 缺字段拒收（L0）生效**。

### W3-W4 数据宪法
- **M1-06 NodeTable SoA + 代际句柄**（~1200 行）：全列集、TryResolve（gen∧DEAD）、标记即死+P9 换代、slab 雏形、AddChild/paintOrder 全量重展、pivot 唯一公式。验证：数据宪法·不变量 1/10/13/15。
- **M1-07 失效协议**（~600 行）：`Invalidation.Mark`（唯一入口、reason 必填）、7 条上行队列、下行子树戳、BoundsD 遇脏即停、全局失效挂起集。验证：时间宪法·不变量 2/15。

### W4-W5 时间宪法
- **M1-08 UiKernel 相位机 + Clock**（~700 行）：Tick P0-P9 骨架、相位写门（P7/P8 断言/入下帧）、P3 波次≤4+责任链、Clock 三时域+TimerHandle 代际、PostInput、P9 换代与锁存。验证：时间宪法·不变量 9/10/11。
- **M1-09 setter/属性 codegen**（498 行改造 + ~300 行）：fork Roslyn 生成器改造——生成节点 setter（位等比较→写值→Mark(ch,reason)、WriteSource 分流）+ 归属表单测 + 每 PropId 同值不脏单测。门：**通道归属封闭（L0）、等值切断单测（L1）上线**。

### W5-W7 渲染最小闭环
- **M1-10 mock 后端 + 参考光栅**（~1500 行）：IRenderBackend 语义层充实、mock 实现（流快照只读、GateReport 七字段）、参考光栅三规则。**阻塞一切 L2/L3 门**。
- **M1-11 RenderStream 核心**（~2000 行，参考重写 InstancedUIStream）：CPU 镜像全表、段键（≤4 纹理×blendClass）、RunRecord 类型上无 AABB、SlotTable（写前位等）、ClipEntry Inherited/Owned+inner 剪枝、预算 32/16+溢出阶梯。
- **M1-12 .fui 前端读取器**（465 移植 + ~500 新写，副轨）：ByteBuffer 移植（仅 ReadColor/ReadPath 改写）；包/组件/显示列表/sprite 解析。对 oracle 样例包字段级对照。

### W7-W8 排水打通
- **M1-13 叶发射器 + Extract + 整流重编**（~1200 行）：Image/九宫/径向填充（ABI 求值不落孤岛）发射；Extract=发射→AdjacencySorter→切段；Structure=面板整流重编。移植：QuadReassembler shim（static 暂存改传入）。
- **M1-14 P6/P7/P8 排水打通**（~1500 行）：paintOrder 切片拼接+structEpoch、world/worldVisual 一遍两算、五通道消化、P8 合并区间上传+序派生。门：**增量正确性门（L2）、零脏帧空转收据上线**。

### W8-W9 后端与布局
- **M1-15 Unity 顶点流后端 + local package**（~1500 行）：unity/ 引 src/、段 MeshRenderer 四角展开、UInt32 顶点属性、y 翻转唯一点、fence 回收（≤4）。验收项（2026-08 审计遗留）：① **上传字节神谕**——增量门比的是流的 CPU 镜像，P8 合并区间真正交给后端的字节区间没有第二条腿；本包须对拍「增量帧的上传区间并集 ⊇ 两帧流镜像的差异字节」并断言区间贴点（mock 已有 80B 粒度断言，Unity 侧要有等价物）。② **多面板 Attach 语义**——内核相位钩子单占（14b-3 起 Attach 硬独占、跨实例 throw），多面板共享内核的扇出件在本包定形（一个扇出器持多条流，或明文「一内核一面板」写进公开 API 注释并测死）。**已交付**：①=`MockBackend.MirrorProbe` 两道核对（上传区间逐字节 + 帧末全量对拍，随 PipeFixture 随行全部管线用例；Unity 侧等价物 `ValidateMirror`），②=`PanelFanout`（扇出件占钩子、五通道按 PanelRoot 路由、面板增删帧边界安全、后端互异硬检查）；Unity 后端 + local package（csc.rsp 提 C#10 + UnityGlobalUsings 接缝 + PropGen analyzer 随包）验收记录见 unity/README.md，形态回写 architecture.md 平面三。
- **M1-16 布局：约束图 + LinearLayout**（~1800 行）：ConstraintOp/FanOut 拓扑序单遍、offset 捕获、resolved 槽唯一写者、P5 四层骨架、parentUsesSize、受控窗（≤3 轮兜底）。门：**P5 幂等断言、布局差分神谕（L2）上线**（随机手建树，不等 FGB）。

### W9-W10 文本与 FGB
- **M1-17 字形源 + GlyphStore**（278-597 段移植 + ~1200 行）：CPU 字体表（仅 glyf）、位图/SDF 页、append-only 三本账、页淘汰双门、generation 仅 P2、双半区接口。CFF 归编译器离线（M2-09）、emoji 走位图。
- **M1-18 TextCore 无状态排版**（~1300 行）：Layout 纯函数（断行/对齐/ellipsis/measure 两把尺/LayoutArena/ref struct 结果）；M1 简化面=cmap 直映+纯文本；P5 量/P7 出 quad/Pending alpha=0。验证：文本·不变量 1/2/3（跨 DPI bit-identical）。M1-16 留缝：度量接缝签名已钉（`ITextMeasure` + `LayoutEngine.TextMeasure` 属性，P5 四层的第一层），Text 通道的排水消费者由本包注册（布局引擎只消费 Layout）。
- **M1-19 FGB blob 写读**（340 移植 + ~900 行）：头/段目录/全段写入器与 Cast 视图；NODE 列序=NodeTable ABI（同源 codegen）。fuzz 雏形。移植：Fqs blob IO+FNV 直搬。

### W10-W11 编译器与事件
- **M1-20 FgbCompiler = 无头运行时**（~2500 行，3 会话，关键路径汇合点）：.fui→建树→跑 P5+度量→Extract→冻结各段；relation→EdgeFollow+分层拒环+拓扑排序；pivotAsAnchor 编译期消灭；GGroup 真节点化；localId 映射；canonical 去重；内存计划打印。门：**约束环拒绝（L0）、编译产物 golden（L1）、等价性金样（编译 Extract=运行时 Extract 逐字节）上线**。M1-16 留缝三条：① 约束环拒绝门**复用** `ConstraintGraphBuilder.Seal`（CycleRejected 带环路径已落地，本包把结果变 FGM 诊断，不另写第二套拒环）；② `ConstraintOp.pivotCorrect` 是保留位——修正系数与置位入口随 pivotAsAnchor 消灭在此烘焙（builder 现拒绝置位）；③ 编译期布局写集落地后，`LayoutStats.LateSlotAllocs` 应恒零并升格为门（手建路径的迟到槽分配从此只属于测试）。
- **M1-21 事件：命中与派发**（~1500 行 + PixelHitTest 82 shim）：HitMode 列、迭代命中（上帧序、local⊗slotMatrix、clip 剪枝、1bit 位图）、EventId<T>/ListenerBlock/链快照、downChain click、CaptureTouch/monitor、EventCtx ref struct。验收项（2026-08 审计遗留）：① **DownLayer 通道要有首个 Mark 者与覆盖**——该通道在 M1-14 阶段无人 Mark（level/sortingOrder 类属性未接），排水代码是零覆盖死路径；本包接入首个写者时必须补「DownLayer 级联落到命中/绘制序」的行为用例。② **clip 剪枝消费 Extract 的 `_clipOf` 数据面，不得读 worldVisual**——其 clip 域 16 位已裁决为保留段恒零、取值宏已删（14b-4 死字段裁决，见 architecture.md 平面三回写）；命中测试的 clip 语义与渲染同源，须有用例钉死两侧同判。

### W11-W12 装载与孤岛
- **M1-22 装载三操作 + 同步实例化**（~1500 行）：验证/视图/绑定（PTCH 回填/DEPS/纹理懒装载）、LoadReport、降级二分（结构性拒载 vs 哈希降 Extract）、PLAN 实例化（顶层块+嵌套 slab+memcpy+arm-not-mount）、Pool 雏形。门：**fuzz 门转常设**。M1-16 留缝：resolved 槽的「实例化批量分配」接 `LayoutEngine.Arm`/`RegisterLinear` 的手建分配路径——实例化时按编译产物的布局写集一次分齐，运行期 resolvedRef 不再变（不变量 7 的另一半）。
- **M1-23 孤岛②③④**（~1200 行）：AddIsland/IIslandContent（含 StillAnimating）、visual 并入下行、②自定义材质（clip include/scissor 降级）、③外部原生（SortingGroup 对齐 run 序、Spine kind）、④stencil 括号。验收项（2026-08 审计遗留）：① `AnyIslandAnimating` 是零脏帧短路的第三前提，M1-14 阶段恒 false 无门——本包必须补「活孤岛自报 StillAnimating ⇒ stats.Dirty 为真、presents 照涨」的正例与「自报静止 ⇒ 短路恢复」的收据断言，否则第三前提仍是无人执法的纸面条款。② **孤岛表进规范形**——`CanonicalStream` 的首用序重编号只盖槽表与 clip 表，孤岛挂载记录不在规范化范围内；本包落地 AddIsland 时必须把孤岛表纳入同一套「分配序非身份」的重编号（否则增量门与 Trace 哈希对孤岛记录要么漏比要么把分配序漂移误报为差异）。M1-15 留缝：`UnityVertexStreamBackend` 对孤岛只记账不产原生对象（Create/Sync/Destroy 句柄面已备）——本包在 Unity 侧补 SortingGroup 对齐 run 序与 kind 分派。

### W12-W13 验证与工具
- **M1-24 输入带 + Trace + 回放**（~1000 行）：InputTape RLE 常开环（派生数据不入带/未录轨钉中性值）、Manual 回放、Trace 逐帧流哈希+FirstDivergentFrame、ReplayBundle。门：**回放确定性门（L3）上线**。
- **M1-25 树查看器最小版 + 诊断面**（~1200 行）：EditorWindow 树视图（localId 路径/DebugName）、属性面板走正常 setter、DebugPick 点选（不复用 HitTester）、高亮走流、FrameStats/LoadReport+P9 锁存。风险#6 落位。M1-15 留缝：宿主 MonoBehaviour（LateUpdate 挂 `UiKernel.Tick` 的 UIPanel 继任件）未随后端交付——本包建预览宿主时落地；编辑器验收脚本 `FairyNextBackendCheck`（建树→接管线→Tick→截帧）可作雏形，Unity 侧上传对拍走 `UnityVertexStreamBackend.ValidateMirror`。

### W13-W14 集成验收
- **M1-26 journey 集成 + 像素抽样门**（~800 行 + 用例）：端到端 journey、首批像素对照（静态图/九宫/径向/SDF 文本/遮罩）、延迟表契约测试、**GateReport 零断言捆绑接入**。
- **M1 收口**：编译门绿 + 行为套件绿 + 抽样像素绿 + 回放门绿 + WebGL 压测报告归档。

## 3. M2 完整运行时（16 包）

**阶段一 状态层**：M2-01 StateRec+三段流水线+P4 Resolve（~1500）→ M2-02 gear→BIND 编译+CtrlFanout（~1800；门：**超集断言（L0）、PropId>64 编译错、换页线性基准上线**）→ M2-03 中央时间轴（~1500；Seek/reverse/sampleStartLive/holdVisible）→ M2-04 tween 直写小道+userBreaks（~800；门：**1000 tween/帧 <0.2ms 硬基准**）→ M2-05 Binder/VM 挂接 P4+keyed diff 汇合（~600）。

**阶段二 滚动与虚拟化**：M2-06 ScrollPane 显式状态机（~1500 + 抄公式 200 行 fork 2040-2318；门：**滚动命中端到端转正、Idle⇒visual==round(logical) 断言**）→ M2-07 虚拟列表三件套（~1500；固定物理节点集/epoch/key/焦点 dataKey/受控窗快路径；门：**满屏滚动 uploadBytes 基准上线**，风险#4 落位）。

**阶段三 文本完整化**：M2-08 富文本 IR（UBBParser 直搬 + Html 1450 移植 + ~600；内联对象=真子节点）→ M2-09 曲线字形源移植+预烘+font-map（~1800；GPU 段参考重写、per-size 路由、3500 常用字预烘、CJK 淡入）→ M2-10 文本输入+IME+焦点（~2000；gap buffer+OpLog、CommitTrigger 三选一、CompositionPatch 不写文档、ITextInputHost 查询面、AxisDelta 归一）。

**阶段四 收口**：M2-11 异步实例化（~600；同一 PLAN 前缀切片，异步/同步产物逐字节等价）→ M2-12 滤镜 RT 域+fadeGroup（~1500；事件驱动 recapture、离屏 pass 前置、Unity 沿用 CaptureCamera。M1-15 留缝：`UnityVertexStreamBackend` 现为 `SupportsOffscreen=false`、`BeginOffscreenPass` 计违约——本包补 pass 语义并翻能力位）→ M2-13 chaos+会话金样+诊断完整化（~1200；门：**chaos 门、金样门（L3）上线**；六诊断面统一 HUD）→ **M2-14 像素门全矩阵**（M2 收口件：24 关系×3 pivot×2 percent + transition/滚动/文本 + 降级各级专属用例 + 字形换代帧单列 + oracle 已知错豁免；风险#1 收口。含 2026-08 审计登记：**九宫格平铺发射落地**——tileGridIndice/scaleByTile 在 M1 发射器无表达、按有声原则拒发并计 `DegradeKind.Scale9TileUnimplemented`（LeafEmitter/Extract 有注释指到此处）；本包前须实现平铺发射并把语料上该计数清零，平铺像素用例进矩阵）→ M2-15 M2 集成验收（金样首批入库、延迟表全表）→ M2-16 WebGL2 预研收尾（副轨，为 M3 Buffer 立项供数）。

## 4. M3 生产化（8 包）

M3-01 热重载+编辑器连线（~1500；进程内 JIT、整面板重建不做状态回放、预览宿主+一键录带）→ M3-02 LANG/branch 变体（~1000）→ M3-03 WebGL2 顶点流后端产品化（~2000；零脏帧短路+ticks/presents 收据、三后端同像素入 L4。M1-15 登记：Unity 侧 `MeshRenderer.sortingOrder` 是 int16 语义，paintOrder×16 超限夹紧并计违约——WebGL2 按数组序绘制无此上限，同像素门要把「巨树序」列为差异豁免或换 Unity 侧绘制驱动）→ **M3-04 Buffer 后端（条件立项 ⇐ uploadBytes bench 超预算）** → **M3-05 作用域重编译（条件立项 ⇐ FrameStats 实据；增量正确性门是其安全网）** → **M3-06 G 层兼容 façade（条件立项 ⇐ 迁移裁决）** → M3-07 上限校准与生产加固（K=3/四波/槽 32/clip 16 按真实项目诊断分布校准，风险#7）→ M3-08 发布工程与版本纪律（append-only diff 门严格化、formatVersion 手册）。

## 5. 门上线时间表（摘要）

| 门 | 上线包 |
|---|---|
| ABI 字节比对（L0） | M1-01 |
| 通道归属封闭 + 等值切断单测（L0/L1） | M1-09 |
| L1 纯函数回归 | M1-03 |
| 约束环拒绝（L0）+ 编译产物 golden + 等价性金样 | M1-20 |
| 增量正确性门 + 零脏帧收据（L2） | M1-14 |
| P5 幂等 + 布局差分神谕（L2） | M1-16 |
| fuzz 装载门常设 | M1-22 |
| 回放确定性门（L3） | M1-24 |
| GateReport 零断言 + 像素抽样门（L4 首版） | M1-26 |
| 超集断言 + PropId>64 编译错 + 换页线性基准 | M2-02 |

> 「PropId>64 编译错」指的是**状态层脏掩码的位下标**上限（`Abi.MaxObservableProps`），不是节点 PropId 的 id 值——节点 id 是分组编号空间（`X=128`），照字面实现会判死全部变换属性。执法点在 setter 生成器里（M1-09 已建 tools/PropGen 与诊断骨架），但要等 M2-01/02 的可观测属性声明面落地才有对象可数。详见 architecture.md 平面二不变量清单末尾的实现期澄清。
| tween 硬基准 / uploadBytes 基准 | M2-04 / M2-07 |
| chaos + 会话金样（L3） | M2-13 |
| 像素门全矩阵（L4 收口） | M2-14 |
| 三后端同像素 + presents 收据 | M3-03 |
| append-only diff 严格化 | M3-08 |

## 6. 每期明确不做 + 条件立项

- **M1 不做**：作用域重编译（整流重编先行）、Buffer 后端、热重载、曲线文本（ABI 位预留）、滤镜、状态层、滚动/虚拟列表、文本输入、异步实例化。
- **M2 不做**：3D 透视孤岛（等语料扫描）、多语言运行时切换、外部纹理孤岛。
- **永久不做**（按裁决）：OrderList 双序机构、Epoch 注册表、紧栅栏、layoutDelta、运行时 AddRelation、热重载状态回放。
- **条件立项**（证据一律 BenchHeader 法定格式）：Buffer 后端 ⇐ uploadBytes；作用域重编译 ⇐ FrameStats；G façade ⇐ 迁移裁决。

## 7. 风险对策落位

风险#1 布局像素兼容 → M1-04/M1-26/M2-14；#2 规模节奏 → 本 WBS 每包一会话+每期收口；#3 WebGL 预算 → M1-05 首月；#4 滚动带宽 → M2-07 基准门；#5 字体三陡坡 → M1-17 只诺 glyf+CFF 离线+emoji 位图；#6 调试体验 → M1-25 必有；#7 拍脑袋上限 → M3-07 校准；#8 PropId 上限 → M2-02 编译错+扩展属性只走 Binder。

## Verification

- 每包合入即跑 `dotnet build FairyNext.sln && dotnet run --project tests/FairyNext.Tests`（RESULT 判定行，fail>0 红）；该包对应门自此进强制集。
- 里程碑收口：M1-26 / M2-15 / M3-08 各自的收口条件全绿才进入下期。
- oracle 对拍：所有像素/数值 golden 出自 `oracle.lock` 所钉的 fork 提交（当前 08a2d56，演进见其 shaHistory），UniCli 驱动；oracle 变更须 bump SHA + 重跑全部基线。
- 关键文件：`src/Contracts/Abi.cs`（ABI 根）、`src/Core/UiKernel.cs`（相位/失效落点）、`src/Compiler/FgbCompiler.cs`（关键路径汇合）、`src/Backend.Mock/`（行为门宿主）、`tests/FairyNext.Tests/Program.cs`（门载体）。
