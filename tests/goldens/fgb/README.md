# 编译产物 golden（M1-20b 门 ①，L1）

四个样例包的 **FGB blob 逐字节基线**，加一份同名的人读账单。用例
`freeze golden: <包名> …`（`tests/FairyNext.Tests/CompilerFreezeTests.cs`）每次跑主 runner
都重新编译并与这里逐字节比。

| 文件 | 是什么 | diff 时看什么 |
|---|---|---|
| `<包名>.fgb` | `FgbCompiler.Compile` 的发布物（**十四段**，规范段序：STRT/TREF/DEPS/COMP/NODE/PLAN/CONT/LOCL/CNST/QUAD/SEGS/LEAF/CLIP/PTCH） | 只回答「变没变」，首差异字节偏移由用例打印 |
| `<包名>.plan.txt` | 同一次编译的 `MemoryPlan` + `ReactiveGraph`（架构机制 11 + 平面六「编译产物 golden」） | 回答「**哪个段、哪个组件**变了」——段字节、逐组件 nodes/quads/segs/leaves/ops/plan/pool、canonical 去重率、掩码占用率、CNST 拓扑序 |

二进制基线单独存在的理由：段序、canonical 的登记序、字符串池排布都是产物的一部分，
在 `.plan.txt` 里看不见。两份一起入库，是因为「变了」和「哪儿变了」需要两种不同的证据。

## 覆盖面

| 包 | 为什么进 golden |
|---|---|
| `VirtualList` | 嵌套组件 + 关系 + 控制器混排的基本盘；三组件 |
| `ScrollPane` | 五组件、组件级 `overflow=scroll`、关系边类型混合 |
| `PullToRefresh` | GGroup 两组真节点化、纯矩形 Graph、跨组关系 |
| `TurnPage` | 九组件（本仓最大）、`pivotAsAnchor` 原点换算、Loader 与文本叶 |

余下两个样例包（`Cooldown`/`TextMeshPro`）不入 blob golden，但照样跑等价性金样、
读回对账、确定性与 canonical 后置扫描——它们的产物性质由那些门守，不额外背一份二进制基线。

## 刷新基线

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"
FAIRYNEXT_UPDATE_FGB_GOLDENS=1 dotnet run --project tests/FairyNext.Tests
```

刷新后**必须逐行读 `.plan.txt` 的 diff**：golden 的全部价值在于「改动是被人看过的」。
只有 blob 变而 plan 不变，说明动的是段内排布或去重登记序——那种改动尤其要在提交信息里写明原因。

## 基线依赖什么

blob 里的文本叶几何来自编译期度量，度量吃的是用例侧合成的测试字体
（`SynthFontT()`，见 `TextGlyphTests.cs`）——**改那个字体会改这里的基线**，这是预期行为：
字体度量进产物，产物就该在字体变时现形。四维身份里的 `scaleLevel`/`branchId` 取
`ShapeOpts()` 的 `(1, 0)`；`combinedRefHash` 恒零（单包编译面给不出链上包的 sourceHash，
见 FGM304），故基线不随依赖包变化；DEPS 段写条目但期望哈希留零，同理。

## 会改动基线的两类跨包改动（M1-22 起）

1. **格式版本 / 段集**：`Abi.FgbFormatVersion` bump 或新增段，整批基线必变（M1-22 从 v1 到 v2：
   PLAN/PTCH/DEPS 三段进驻、COMP 96B→104B、头切 `pkgId`）。
2. **编译世界的树形**：M1-22 把组件根从「无头世界的树根」改成「树根的孩子」，模板区间自此从槽 2 起，
   NODE 拓扑列的相对化值随之整体偏移——这正是相对化不再是数值恒等的那一步，
   改回去会让全部装载用例与这里的基线一起红。
