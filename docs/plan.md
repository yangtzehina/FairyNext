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
- **M1-17 字形源 + GlyphStore**（278-597 段移植 + ~1200 行）：CPU 字体表（仅 glyf）、位图/SDF 页、append-only 三本账、页淘汰双门、generation 仅 P2、双半区接口。CFF 归编译器离线（M2-09）、emoji 走位图。**已交付**：`src/Core/Text/`——TtfFontFace（fork 278-597 移植 + cmap format 12 增补 + 全读口窗口硬化 + CFF/截断拒载有声）、GlyphMetricsTable（度量半区，类型上无页账引用）、GlyphStore（位置/页两账 + 双门淘汰 + 提交二次验门 + `RequestRebuild` 核按钮 + `GlyphStoreFanOut` 硬独占挂接）；落地形态 blockquote 回写 architecture.md 平面四 B「CJK 冷启动」前。测试资产 = 合成 TTF 构造器（入库、字节自洽）+ Monaco.ttf 系统路径 golden（期望值独立采自 Python 手解，缺失 SKIP 有声；Apple 许可不允许入库）。
- **M1-18 TextCore 无状态排版**（~1300 行）：Layout 纯函数（断行/对齐/ellipsis/measure 两把尺/LayoutArena/ref struct 结果）；M1 简化面=cmap 直映+纯文本；P5 量/P7 出 quad/Pending alpha=0。验证：文本·不变量 1/2/3（跨 DPI bit-identical）。M1-16 留缝：度量接缝签名已钉（`ITextMeasure` + `LayoutEngine.TextMeasure` 属性，P5 四层的第一层），Text 通道的排水消费者由本包注册（布局引擎只消费 Layout）。M1-17 留缝四条：① 排版只依赖 `IGlyphMetricsSource`（`GlyphMetricsTable`：MapCodepoint 直映 + advance/bbox em + `KerningEm` M1 恒 0 但**从第一天按「advance + kerning」求和写**，M2-09 落数据零改动）；② 发射侧走 `IGlyphRasterSource.EnsureResident/TryGetLocator`（texel 尺寸由栅格化方给；M1-17 无像素产者，缺字形 Pending alpha=0 的产者是本包/后包）；③ 引用纪律——叶发射时对用到的页 `AddPageRef`、文本变更/销毁对称 `ReleasePageRef`（淘汰门 1 + 换代 fan-out 目标集都靠它）；④ `GlyphStore.BeginFrame` 挂 `UiKernel.BeforeFrame`（页龄时间基），店的 P2 换代已自动经 `AttachInvalidation` 占 `GlyphStoreFanOut` 生效。**已交付**：`src/Core/Text/` 增 TextCore（纯函数排版：断行/对齐 3×3/ellipsis/两把尺——四处求和共用唯一 `AdvanceRun`，em 空间求值、收尾一步乘 SizePx）、LayoutArena + `TextLayoutResult`（ref struct 出不了帧）、TextSystem（一身四职：稀疏文本账 / Ch.Text 排水 + ProductStamp 三级惰性 / ITextMeasure / ITextQuadEmitter 含 Pending α=0 与页引用集合调和）；LeafEmitter 增文本臂（`LeafSpec.Text`+`TextRef`，MaxQuads=SlackHint 不依赖宽高）；LayoutEngine 增度量层（`RegisterMeasured`/MeasurePass，P5 四层齐）；InvalidateReason 增 `ResidencyDelivered`。四条留缝全部按 M1-17 简报接线；偏离与边界（Emit 签名、tier-2 退役、单纹理页帐、同步驻留）blockquote 回写 architecture.md 平面四 B「渲染适配」后。
- **M1-19 FGB blob 写读**（340 移植 + ~900 行）：头/段目录/全段写入器与 Cast 视图；NODE 列序=NodeTable ABI（同源 codegen）。fuzz 雏形。移植：Fqs blob IO+FNV 直搬。**已交付**：`src/Core/Fgb/`——FgbBlobView（fork Fqs.cs 读侧纪律移植 + TryOpen 打开即验完 + 门号词表 FgbGate append-only + selfHash 三段续链）、FgbNodeSection（NODE payload = count 纯函数、无列目录、精确尺寸门）、FgbLoadReport；`src/Compiler/Fgb/FgbWriter`（fork Write 段移植，全标量显式 LE——BE 构建机同字节；段序 = AddSection 调用序）；`NodeTable.Columns.cs`（生成物列号 → 私有列的名义映射，列序不在此）；Abi.cs 增五张数据表（FgbHeaderFields/FgbSectionDirFields/FgbFlagBits/FgbSectionIds/NodeColumns，头 @12 插 4B 填充修正草图的非对齐 u64），AbiGen 扩 AbiLayout 生成物（NodeCol*/FgbSection*/偏移 + 编译期断言 + Verify 交叉校验；mock/HLSL 不进）；FnvHash 增 span/续链/零段入口。fuzz 1200 变体四类语料随主 runner 常驻。落地形态 blockquote 回写 architecture.md 平面五「FGB 顶层布局」后（四条）。计划的 1 处 Mathf.Clamp 无随行对象：scaleLevel 在 FGB 是 u16 头字段非 flags 位段，u16 定义域即合法域。

### W10-W11 编译器与事件
- **M1-20 FgbCompiler = 无头运行时**（~2500 行，3 会话，关键路径汇合点）：.fui→建树→跑 P5+度量→Extract→冻结各段；relation→EdgeFollow+分层拒环+拓扑排序；pivotAsAnchor 编译期消灭；GGroup 真节点化；localId 映射；canonical 去重；内存计划打印。门：**约束环拒绝（L0）、编译产物 golden（L1）、等价性金样（编译 Extract=运行时 Extract 逐字节）上线**。M1-16 留缝三条：① 约束环拒绝门**复用** `ConstraintGraphBuilder.Seal`（CycleRejected 带环路径已落地，本包把结果变 FGM 诊断，不另写第二套拒环）；② `ConstraintOp.pivotCorrect` 是保留位——修正系数与置位入口随 pivotAsAnchor 消灭在此烘焙（builder 现拒绝置位）；③ 编译期布局写集落地后，`LayoutStats.LateSlotAllocs` 应恒零并升格为门（手建路径的迟到槽分配从此只属于测试）。M1-18 留缝两条：① 「跑 P5+度量」直接复用 `TextSystem.Attach(layout)` + `LayoutEngine.RegisterMeasured`——度量是纯函数（文本·不变量 1/2 已钉），编译期求出的 resolved 尺寸与运行时逐位同，不写第二套度量；② 文本叶的发射容量上界（有轮廓字形数+1，`TextSystem.QuadCapacity`）不依赖宽高——编译产物冻结 SlackHint 用同一口径，静态文本的 slack 档在编译期即可定。M1-19 留缝三条：① 容器不排序——`FgbWriter` 段序 = AddSection 调用序，规范段序与 canonical 去重（同 payload 段共享等）在本包定；② NODE 冻结走 `FgbNodeSection.Write(table, start, count)`（列序/宽全走 ABI 表），写出的是**原始真值**（拓扑列绝对下标）——模板镜像的相对化（rebase）变换在本包冻结时做，消费 `Abi.NodeColumns` 的 Rebase 位；③ 全段 fourcc 已占 id（`AbiLayout.FgbSection*`），新段只需 AddSection，读侧未知段跳过已测——段格式演进不动容器。 **已交付（前半 M1-20a，~1350 行 + 35 条用例）**：`src/Compiler/Shape/`——TreeBuilder（一个组件 = 一个**真无头世界**：NodeTable/UiKernel/LayoutEngine/TextSystem/ContentTable 全是运行期同款实例，属性一律走正常 setter；GGroup 真节点化 + 坐标换组空间、pivotAsAnchor 原点换算烘死、localId 双向映射、内容挂载的 M1 面）、GroupPlan（归属清洗/断环/连续性诊断，纯函数）、RelationTranslator（24+Size → EdgeFollow 翻译表有 golden；`ConstraintCompiler` 只做翻译/census/锚定注入决策，**拒环与拓扑排序全部复用 `ConstraintGraphBuilder.Seal`，无第二套**）、FgmDiagnostics（`FGM` 码词表 append-only，0xx 前端/1xx 约束/2xx 组/3xx 内容/9xx 自检）、ShapedPackage（20a↔20b 的接缝：已定形的树 + 诊断，20b 吃它做 Extract/冻结，**不重新解析**）；`FgbCompiler.Shape` 门面上线、`Compile` 变「前半真跑 + 后半 `Freeze` 待进驻」。Core 侧：`ConstraintGraphBuilder.PinAnchor` 兑现 M1-16 留缝 ② 的 pivotCorrect 保留位（Seal 加「锚定必须与写 Size 的算子配对」校验），LayoutEngine 增 `RowOfOp`/`PivotOf` 两形态求值与锚定捕获，NodeTable 增 `PivotAt` 内部读。门：**约束环拒绝 FGM101（L0）上线**（环 = 编译错 + 编辑器 id 闭合环路径 + 该组件除名、余者照编）；编译期 P5 幂等门与布局差分神谕**全程开启**、未收敛 FGM901、门红/迟到槽分配非零 FGM902（M1-16 留缝 ③ 兑现——但**边界写明**：`LinearLayout` 尚未进编译面，故 LateSlotAllocs 目前是**跳线**而非可被资产触发的门，M2-07 虚拟列表进驻后才有真语料）。M1-18 留缝 ① 兑现：编译期度量直接复用 `TextSystem.Attach(layout)` + `RegisterMeasured`，用例证明与 `TextCore.Layout` 纯函数逐位同。样例包补到六个（PullToRefresh/TurnPage，逐字节拷自 oracle）。**发现并修正前置理解偏差**：fork 对「锚点节点被尺寸关系拉伸」有**两套定点**（`RelationItem.cs` 391-431）——源是父时 fork 用 `tmp=xMin; SetSize(…,ignorePivot:true); xMin=tmp;` 显式撤销保锚点、定点是**原点**；源是兄弟时走 `width` setter、定点是**锚点**。注入判据据此改为「恰一个 Size 算子 ∧ 源不是局部 0 ∧ 该轴 pivot 非零」，两支各有用例。落地形态 blockquote 回写 architecture.md 平面四（pivotCorrect 算子形态）与平面五（Shape 接缝 / GGroup 三条明码边界 / 内容挂载 M1 面）。 **已交付（后半 M1-20b，~1000 行 + 23 条用例）**：`src/Compiler/Freeze/`——`FgbFreezer`（唯一入口 `FgbCompiler.Freeze(ShapedPackage)`，**不重新解析 .fui**；逐组件跑**运行期同一个 `Extract`**，离线驱动 = 直调 `Rebuild()` + 自开 `PruneAfterRebuild` 镜像管线 `DrainTail` 的尾剪枝 → canonical 去重 → 十一段按**规范段序** `STRT→TREF→COMP→NODE→CONT→LOCL→CNST→QUAD→SEGS→LEAF→CLIP` 冻结 → 内存计划），`CanonicalTable`/`StringPool`（机制 10：字节等同必同 id，判等逐字节、哈希只找候选）、`FgbRecordIo`（段内标量全显式 LE，偏移一律取生成物，文件里零字面偏移）、`MemoryPlan`（机制 11 的账单 + `ReactiveGraph` 产物文本）；`src/Core/Fgb/FgbSectionLayouts.cs`（STRT/CNST 段布局 = 计数的纯函数，写读同一函数算偏移）、`FgbNodeSection.WriteHeader/WriteSlice`（包级 NODE 段 = 全组件节点拼成的一张扁平表）。`Abi.cs` 增 `AbiRecord`/`FgbRecords`（九张段内定长记录表，生成物出偏移常量 + 首尾相接断言 + Verify 交叉校验，M1-19 的「记录布局落数据表」自此对全段成立）、两个新段 fourcc（`CONT`/`LOCL`）、`AbiFieldKind.Int32/UInt8`、`FgbInstanceHeaderBytes`。三道门上线：**编译产物 golden（L1）**——四包 FGB 逐字节 + 人读账单入 `tests/goldens/fgb/`（`FAIRYNEXT_UPDATE_FGB_GOLDENS=1` 刷新）；**等价性金样（不变量 18）**——路径 A 编译器离线驱动 vs 路径 B **真运行时管线**（Attach → 标结构脏 → Tick 走完 P6/P7/P8）的 `CanonicalStream` 逐字节，六包 25 组件全过，另配「注入裁剪域」用例补上尾剪枝语料（样例包在 M1 一个裁剪域都没有，`overflow=scroll` 归 M2-06）与「1ulp 扰动必红」的有牙用例；**FGB 读回 sanity**——NODE 列/COMP 区间/CNST 四数组/LOCL/STRT/LEAF.contentRef 逐项与原树原图对账。另上线**冻结前置门 FGM903**（离线 Extract 前自验派生列与 paintOrder 已定形——「上游 Tick 过所以新鲜」是关于实现的推断，不变量是关于产物的断言）与 canonical 后置扫描（不变量 8）。M1-19 留缝三条全兑现（① 规范段序 + canonical 归编译器；② NODE 相对化消费 Rebase 位——**边界写明**：编译世界里根恒在槽 1 故 M1 数值恒等，按 `firstAbs` 参数化且有独立用例，真语料随 M1-22 的基址回填出现；③ 新段只 AddSection）。落地形态 blockquote 回写 architecture.md 平面五（段内记录表 + 规范段序 + 组件内相对 / Freeze 与三道门 / 等价性金样的两条路径与三条边界 / 内存计划）。
- **M1-21 事件：命中与派发**（~1500 行 + PixelHitTest 82 shim）：HitMode 列、迭代命中（上帧序、local⊗slotMatrix、clip 剪枝、1bit 位图）、EventId<T>/ListenerBlock/链快照、downChain click、CaptureTouch/monitor、EventCtx ref struct。验收项（2026-08 审计遗留）：① **DownLayer 通道要有首个 Mark 者与覆盖**——该通道在 M1-14 阶段无人 Mark（level/sortingOrder 类属性未接），排水代码是零覆盖死路径；本包接入首个写者时必须补「DownLayer 级联落到命中/绘制序」的行为用例。② **clip 剪枝消费 Extract 的 `_clipOf` 数据面，不得读 worldVisual**——其 clip 域 16 位已裁决为保留段恒零、取值宏已删（14b-4 死字段裁决，见 architecture.md 平面三回写）；命中测试的 clip 语义与渲染同源，须有用例钉死两侧同判。 **已交付**：`src/Core/Events/`（2100 行 + 786 行用例 / 31 条）——`EventIds`（`EventId<T>` 类型化 id、内建区 [0,64) append-only 词表 + **编译期常量除零门** `BuiltinMaskFits`、用户事件 `EventRegistry` ≥64 同名异型即抛、四种 readonly struct 载荷）、`EventCtx`（ref struct + `EventFn<T>`，逃逸编译负例落 `tools/PropGen.Tests`：存字段 CS8345 / 闭包捕获 CS1628，对**真实 Core 程序集**编译）、`ListenerTable`（`listenerHead` 平行列 + `builtinMask` 二级剪枝 + 块池化，`NodeTable.NodeDisposedHook` 独占归还，剪枝无漏全量神谕 `PruneMatchesFullScan`）、`HitArea`（`HitMode` 五态 + `HitPolicyTable` 冷侧表 + **PixelHitTest 移植**：fork `Core/HitTest/PixelHitTest.cs` 32-81 @ 08a2d56，判定式逐位保留含「只查 x 上界」的原样形态）、`HitTester`（显式栈迭代下行、位门直读 authored localVisual、`local ⊗ slotMatrix` 逆变换、clip 剪枝走 `IHitClipSource`）、`TouchState`（10 槽 + downChain 快照 + monitors + fork 同款连击四元组）、`EventSystem`（P0 `InputSink` 独占接管、链快照两相派发 + gen/DEAD 双验、CaptureTouch→monitor 直发、RollOver/RollOut LCA 差集、轴入口拒零拒非整、`EventStats` 九字段）。`Extract` 增实现 `IHitClipSource`（`ClipEntryOf`/`TryGetClipWindow`——**clip 唯一数据面**，14b-4 死字段裁决的正面兑现）；`NodeTable` 增 `NodeDisposedHook`（P9 换代前按下标通知）与四个内部下标读（`LocalVisualAt`/`LocalMatrixAt`/`PaintIndexAt`/`PaintEndAt`）。**验收项①结账（成文裁决 + 覆盖）**：`Ch.DownLayer` 的首个**产品**写者不在本包——命中缓存是帧内的（帧号护栏，不需要失效通道）、绘制序的层归 `Ch.Structure`，真正会写它的是「整棵子树翻层」，即孤岛 visual 并入下行（**M1-23**）与滤镜 RT 域翻层（**M2-12**，fork `Container.SetChildrenLayer` 同源）；本包把该通道的**排水路径**从零覆盖变成有行为用例（`Mark(DownLayer)` → P6 下钻 → 落叶交回 `Ch.Color/CascadeDown`，随增量门与结构门全绿），并留一道「归属表里出现首个 DownLayer 写者即红」的登记门逼出后续覆盖。**验收项②结账**：clip 剪枝只读 `Extract._clipOf`，同判用例钉死「渲染剔除的叶命中也剔除、域内叶两侧都在」，另有嵌套折叠窗口的内外用例。杀变异 18 例注入 18 例被杀（含「读 worldVisual 位门」「孩子正序」「不吃 clip」「不合成槽矩阵」「按活链而非快照派发」「只验 gen」「位图位序翻转」「剪枝漏用户事件」「ClickTest 取当前 target」「双击丢时间窗」「monitor 改冒泡」「不归还监听块」「StepDownLeaf 摘掉 DownLayer 子句」）。落地形态 blockquote 回写 architecture.md 平面四 B（十条）。

### W11-W12 装载与孤岛
- **M1-22 装载三操作 + 同步实例化**（~1500 行）：验证/视图/绑定（PTCH 回填/DEPS/纹理懒装载）、LoadReport、降级二分（结构性拒载 vs 哈希降 Extract）、PLAN 实例化（顶层块+嵌套 slab+memcpy+arm-not-mount）、Pool 雏形。门：**fuzz 门转常设**。M1-16 留缝：resolved 槽的「实例化批量分配」接 `LayoutEngine.Arm`/`RegisterLinear` 的手建分配路径——实例化时按编译产物的布局写集一次分齐，运行期 resolvedRef 不再变（不变量 7 的另一半）。M1-19 留缝四条：① `FgbGate` 编号 append-only——门 4/5（DEPS 依赖比对/文件名语义）在本包续编号进驻，LoadReport 补 patch 数/耗时字段；② 四维身份四字段已在头上可读（SourceHash/CombinedRefHash/ScaleLevel/BranchId），比对语义归本包；③ NODE 导入侧缺席——`NodeTable.Columns.cs` 只出不进，「列 → 表 memcpy + 按 Rebase 位加基址回填」随 PLAN 实例化进驻，`FgbNodeView.Column` 就是 memcpy 的源 span；④ fuzz 转常设门以 `FgbFuzz` 为形态（seed 固定、四类语料、违例三判据），本包扩语料到 NODE payload 内部变异与验证/视图/绑定全链。 **已交付（~1900 行 + 19 条用例）**：`src/Core/Fgb/` 补五件——`FgbPackage`（三操作唯一入口 `TryLoad`，永不抛、失败面二值；验证 = 容器门 1-3 之后补整层段内门：必备段缺席 12 / 段内计数不自洽 13 / COMP 区间与逐行 localId·相对下标·contentRef 14 / PLAN 后序性质 15 / PTCH 目标 16 / **NODE 拓扑门 20**（父·首子·兄弟环三方自洽 + 自根 DFS 恰好触达全部节点——memcpy 出来的链会被 paintOrder 直接走，环路是死循环不是错画面）；视图 = 十四段记 `(offset,length)`，取用切 span/Cast，blob 一生不复制；绑定 = TREF 符号 → 宿主纹理编号 + PTCH **就地**回填 CONT.texId/SEGS.tex[slot]），`FgbInstantiator`（PLAN 后序执行：段分配 + 21 次 `Array.Copy` + 5 列基址回填 + ownerInst 实例身份回填 + resolved 槽按布局写集批量分齐 + `LayoutEngine.Arm`；顶层块与嵌套块各走各自模板 slab bin，嵌套挂宿主 localId 并下传宿主框；`FgbContentSource` 是 arm-not-mount 的触发点），`FgbInstancePool`（不缓存对象：Return = 标记即死、Rent 走同一条 Instantiate 全列重搬，「复用后有残留」结构上不可能），`FgbAssetSource`（两段式纹理通道：装载期只换编号、首用才装像素）、`FgbFileName`（门 5 的纯字符串解析）；`FgbLoadReport` 扩成逐门判决表 + 分档（`FgbGateClass`：结构性拒载 vs 哈希级降级）+ patch 数/耗时 + 确定性 `Describe()`；`FgbRecordIo` 从编译器迁入 Core（写侧读侧共用同一套 LE 原语）；`NodeTable.Columns` 补导入面（`ImportColumn`/`RebaseColumn`/`SetOwnerInstRange`，M1-19 留缝 ③ 兑现）。编译器侧：`Abi.cs` bump `FgbFormatVersion` 2 + 三条新记录（Plan/Patch/Dep）+ COMP 从保留区切 `planStart/planCount`（96B→104B）+ 头切 `pkgId`；`FgbFreezer` 出 PLAN（后序展开嵌套引用，环 = FGM305、越水位 = FGM306）/ PTCH（包内图集编号才回填，运行期自有纹理有声跳过）/ DEPS（`{pkgId, 0}`，0 = 未知）+ 规范段序扩十四段。**M1-20a 的一处改判**：组件根从「无头世界的树根」改成「树根的孩子」——形态对齐运行期，且让模板区间从槽 2 起，**M1-20b 记在案的「摘掉 Relativize 调用不可观测」自此销账**（注入变异，golden 与全部装载用例一起红）；配套冻结期把模板根的 parent/nextSib/prevSib 归零。门：**fuzz 转常设**（2048 轮 seed 固定，语料含十一种段内记录，断言「干净拒载 / 正常装载且全视图可走 + 可实例化」）、**装载门 4/5**、**降级二分两路**、**等价性金样第三条腿**（FGB 装载实例化出的树 vs TreeBuilder 的树：21 列逐位 + resolved 逐位 + CanonicalStream 逐字节）。边界写明：文本叶样式未进 M1 冻结面 ⇒ 解成「不产渲染单元」+ 计数，带文本叶的组件不参与 resolved/Extract 两腿；带嵌套步的组件不参与 Extract 腿；宿主框只在装填那一刻下传（随动归 M2 的 parentUsesSize）；DEPS 的期望哈希与 combinedRefHash 仍留零（多包编译面未进驻）。落地形态 blockquote 回写 architecture.md 平面五（FGB v2 三段 + 头 pkgId + 组件根改判 / 装载三操作与门 4/5 / 降级二分 / PLAN 实例化 / arm-not-mount / Pool / fuzz 常设门）。
- **M1-23 孤岛②③④**（~1200 行）：AddIsland/IIslandContent（含 StillAnimating）、visual 并入下行、②自定义材质（clip include/scissor 降级）、③外部原生（SortingGroup 对齐 run 序、Spine kind）、④stencil 括号。验收项（2026-08 审计遗留）：① `AnyIslandAnimating` 是零脏帧短路的第三前提，M1-14 阶段恒 false 无门——本包必须补「活孤岛自报 StillAnimating ⇒ stats.Dirty 为真、presents 照涨」的正例与「自报静止 ⇒ 短路恢复」的收据断言，否则第三前提仍是无人执法的纸面条款。② **孤岛表进规范形**——`CanonicalStream` 的首用序重编号只盖槽表与 clip 表，孤岛挂载记录不在规范化范围内；本包落地 AddIsland 时必须把孤岛表纳入同一套「分配序非身份」的重编号（否则增量门与 Trace 哈希对孤岛记录要么漏比要么把分配序漂移误报为差异）。M1-15 留缝：`UnityVertexStreamBackend` 对孤岛只记账不产原生对象（Create/Sync/Destroy 句柄面已备）——本包在 Unity 侧补 SortingGroup 对齐 run 序与 kind 分派。 M1-21 转登记：**`Ch.DownLayer` 的首个产品写者候选之一**——「孤岛 visual 并入下行」若落成「整棵子树翻层」，本包即是首个 Mark 者，届时必须补「DownLayer 级联落到命中/绘制序」的行为用例并改掉 EventTests 里那道登记门（`DownLayerHasNoProductWriterYet`）；若本包判定孤岛 visual 仍走 DownColor/DownVisible，则把该登记原样转给 M2-12。

### W12-W13 验证与工具
- **M1-24 输入带 + Trace + 回放**（~1000 行）：InputTape RLE 常开环（派生数据不入带/未录轨钉中性值）、Manual 回放、Trace 逐帧流哈希+FirstDivergentFrame、ReplayBundle。门：**回放确定性门（L3）上线**。
- **M1-25 树查看器最小版 + 诊断面**（~1200 行）：EditorWindow 树视图（localId 路径/DebugName）、属性面板走正常 setter、DebugPick 点选（不复用 HitTester）、高亮走流、FrameStats/LoadReport+P9 锁存。风险#6 落位。M1-15 留缝：宿主 MonoBehaviour（LateUpdate 挂 `UiKernel.Tick` 的 UIPanel 继任件）未随后端交付——本包建预览宿主时落地；编辑器验收脚本 `FairyNextBackendCheck`（建树→接管线→Tick→截帧）可作雏形，Unity 侧上传对拍走 `UnityVertexStreamBackend.ValidateMirror`。

### W13-W14 集成验收
- **M1-26 journey 集成 + 像素抽样门**（~800 行 + 用例）：端到端 journey、首批像素对照（静态图/九宫/径向/SDF 文本/遮罩）、延迟表契约测试、**GateReport 零断言捆绑接入**。
- **M1 收口**：编译门绿 + 行为套件绿 + 抽样像素绿 + 回放门绿 + WebGL 压测报告归档。

## 3. M2 完整运行时（16 包）

**阶段一 状态层**：M2-01 StateRec+三段流水线+P4 Resolve（~1500）→ M2-02 gear→BIND 编译+CtrlFanout（~1800；门：**超集断言（L0）、PropId>64 编译错、换页线性基准上线**）→ M2-03 中央时间轴（~1500；Seek/reverse/sampleStartLive/holdVisible）→ M2-04 tween 直写小道+userBreaks（~800；门：**1000 tween/帧 <0.2ms 硬基准**）→ M2-05 Binder/VM 挂接 P4+keyed diff 汇合（~600）。

**阶段二 滚动与虚拟化**：M2-06 ScrollPane 显式状态机（~1500 + 抄公式 200 行 fork 2040-2318；门：**滚动命中端到端转正、Idle⇒visual==round(logical) 断言**）→ M2-07 虚拟列表三件套（~1500；固定物理节点集/epoch/key/焦点 dataKey/受控窗快路径；门：**满屏滚动 uploadBytes 基准上线**，风险#4 落位）。

**阶段三 文本完整化**：M2-08 富文本 IR（UBBParser 直搬 + Html 1450 移植 + ~600；内联对象=真子节点。M1-18 留缝：`TextCore.Layout` 的 M1 面是纯文本单 style——RichRun 窗口/簇映射/内联对象进驻时断行/对齐/ellipsis golden 与文本·不变量 1/2/3 原样护航；LayoutArena 的 ClusterEntry/DecoRect/InlinePlace/LinkRect 四条切片按既有两条（Glyphs/Lines）的容量纪律增设；「measure 免重排 + 分层缓存」在架构文档有钉、M1-18 未进驻——每次重排、ProductStamp 只省 Content 下游，缓存键含 generation 的两层缓存在富文本落地时一并进）→ M2-09 曲线字形源移植+预烘+font-map（~1800；GPU 段参考重写、per-size 路由、3500 常用字预烘、CJK 淡入。M1-17 留缝：TtfFontFace 只到「二次轮廓收集」——8-band 烘焙从 fork CurveFontStore 378-401 另移；`GlyphMetricsTable.KerningEm` 在此落 kern/GPOS 数据；`ResidencyState.Pending` 的异步烘焙产者与页淘汰的「内存水位」扫描策略在此进驻，双门判据本体不动。M1-18 留缝：`TextSystem.SyncResidency=false` 已是异步驻留的完整行为面——缺字形 α=0 占位 + pending 订阅队列 + `DeliverPending` 补 `Mark(Content, ResidencyDelivered)`，本包只需把 DeliverPending 的调用方从测试换成烘焙完成回调 + P8 预算上传，「不闪不跳版」用例已钉住占位→到货几何逐位不动；单纹理页帐（`AtlasTexture` 叶级段键 + 异页 α=0 静音 `CrossPageMuted` 收据）在 per-page 纹理绑定落地时解除；`GlyphPageKind` 路由已留 `TextSystem.PageKind` 单点）→ M2-10 文本输入+IME+焦点（~2000；gap buffer+OpLog、CommitTrigger 三选一、CompositionPatch 不写文档、ITextInputHost 查询面、AxisDelta 归一）。

**阶段四 收口**：M2-11 异步实例化（~600；同一 PLAN 前缀切片，异步/同步产物逐字节等价）→ M2-12 滤镜 RT 域+fadeGroup（~1500；事件驱动 recapture、离屏 pass 前置、Unity 沿用 CaptureCamera。M1-15 留缝：`UnityVertexStreamBackend` 现为 `SupportsOffscreen=false`、`BeginOffscreenPass` 计违约——本包补 pass 语义并翻能力位。M1-21 转登记：**`Ch.DownLayer` 的首个产品写者最终归宿**——RT 域进出要把整棵子树翻到 CaptureCamera 的层（fork `Container.SetChildrenLayer`/`CaptureCamera.layer`，是一个真正沿子树级联、且与命中/绘制序都相关的量）；落地时必须补「DownLayer 级联落到命中/绘制序」的行为用例并改掉 EventTests 的登记门 `DownLayerHasNoProductWriterYet`）→ M2-13 chaos+会话金样+诊断完整化（~1200；门：**chaos 门、金样门（L3）上线**；六诊断面统一 HUD）→ **M2-14 像素门全矩阵**（M2 收口件：24 关系×3 pivot×2 percent + transition/滚动/文本 + 降级各级专属用例 + 字形换代帧单列 + oracle 已知错豁免；风险#1 收口。含 2026-08 审计登记：**九宫格平铺发射落地**——tileGridIndice/scaleByTile 在 M1 发射器无表达、按有声原则拒发并计 `DegradeKind.Scale9TileUnimplemented`（LeafEmitter/Extract 有注释指到此处）；本包前须实现平铺发射并把语料上该计数清零，平铺像素用例进矩阵）→ M2-15 M2 集成验收（金样首批入库、延迟表全表）→ M2-16 WebGL2 预研收尾（副轨，为 M3 Buffer 立项供数）。

## 4. M3 生产化（8 包）

M3-01 热重载+编辑器连线（~1500；进程内 JIT、整面板重建不做状态回放、预览宿主+一键录带）→ M3-02 LANG/branch 变体（~1000）→ M3-03 WebGL2 顶点流后端产品化（~2000；零脏帧短路+ticks/presents 收据、三后端同像素入 L4。M1-15 登记：Unity 侧 `MeshRenderer.sortingOrder` 是 int16 语义，paintOrder×16 超限夹紧并计违约——WebGL2 按数组序绘制无此上限，同像素门要把「巨树序」列为差异豁免或换 Unity 侧绘制驱动）→ **M3-04 Buffer 后端（条件立项 ⇐ uploadBytes bench 超预算）** → **M3-05 作用域重编译（条件立项 ⇐ FrameStats 实据；增量正确性门是其安全网）** → **M3-06 G 层兼容 façade（条件立项 ⇐ 迁移裁决）** → M3-07 上限校准与生产加固（K=3/四波/槽 32/clip 16 按真实项目诊断分布校准，风险#7）→ M3-08 发布工程与版本纪律（append-only diff 门严格化、formatVersion 手册）。

## 5. 门上线时间表（摘要）

| 门 | 上线包 |
|---|---|
| ABI 字节比对（L0） | M1-01 |
| 通道归属封闭 + 等值切断单测（L0/L1） | M1-09 |
| L1 纯函数回归 | M1-03 |
| 约束环拒绝（L0）+ 编译产物 golden + 等价性金样 | M1-20（全部已上线：环拒 20a，golden/金样/读回 20b） |
| 增量正确性门 + 零脏帧收据（L2） | M1-14 |
| P5 幂等 + 布局差分神谕（L2） | M1-16 |
| fuzz 装载门常设 + 装载门 4/5 + 实例化等价性 | M1-22（已上线：2048 轮十一种段内语料 / 门 4/5 / 三腿等价性） |
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
