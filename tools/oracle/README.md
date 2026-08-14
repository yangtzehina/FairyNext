# oracle 对拍基建（M1-04）

把钉死的 fork（`~/ECS/FairyGUI-unity @ d1a9d7d`）脚本化成**可编程 oracle**：建场景 → 截帧 → 导布局数值 → 基线入库 → 容差比对。
架构里像素门（L4）与布局全组合矩阵的地基，也是风险 #1（布局像素兼容）的地基。

分工是硬的：

| 侧 | 位置 | 依赖 Unity | 何时跑 |
|---|---|---|---|
| 生成端 | `tools/oracle/`（bash + 注入 UniCli 的 C# 片段） | 是 | 只在本机，人工重生成基线时 |
| 消费端 | `src/Tools.OracleCompare`（netstandard2.1，零 NuGet） | 否 | 每次 `dotnet run --project tests/FairyNext.Tests` |
| 产物 | `tests/goldens/oracle/<scene-id>/` | — | 入库，CI 只读 |

**这套东西不承诺什么**：不保证跨机器像素一致（不同 GPU/驱动的光栅与色彩管线不同）——所以 `meta.json` 记下 `graphicsDevice`
与 `colorSpace`，比对器在两侧不同时报「基线不可比」而不是「实现回归」。不保证 fork 的行为是对的——已知的旧实现错误按架构
§平面六的规定标豁免，不追认为规范。不在 CI 里跑 Unity。

## 前置

- Unity **2022.3.62f3** 打开着 `~/ECS/FairyGUI-unity`（版本号由 `oracle.lock` 钉死，脚本会核对 `ProjectSettings/ProjectVersion.txt`）。
- UniCli 客户端 `~/bin/unicli` + 工程里装好 `com.yucchiy.unicli-server`（`unicli check` 应报 installed / running）。
- fork 的 `git HEAD` 必须等于 `oracle.lock` 的 `sha`，否则脚本拒跑。

体检（不动编辑器状态）：

```bash
./tools/oracle/capture.sh --check
# oracle: oracle=/Users/ai/ECS/FairyGUI-unity @ d1a9d7d · Unity 2022.3.62f3 · unicli 1.6.0
```

覆盖默认路径用环境变量：`ORACLE_DIR`、`UNICLI`、`ORACLE_TIMEOUT_MS`。

## 跑法

```bash
./tools/oracle/capture.sh scene-001                    # 直接覆盖 tests/goldens/oracle/scene-001/
./tools/oracle/capture.sh scene-001 --out /tmp/cand    # 截到别处（重生成前先比差异）
```

脚本做的事，按顺序：核对 SHA/版本 → 等编辑器空闲 → 记下当前场景 → `Scene.New empty` → `PlayMode.Enter` →
`Eval` 注入 `lib/declarations.cs` 并调 `FairyNextOracle.Run` → `PlayMode.Exit` → 把原场景开回来 → 校验三件套非空 → 打印 sha256。

## 产物：golden 三件套

```
tests/goldens/oracle/scene-001/
  frame.png     截帧（RGBA8，尺寸 = 场景描述符的 stageWidth×stageHeight）
  layout.json   逐节点数值：path/name/type/x/y/width/height/scaleX/scaleY/rotation/alpha/visible
  meta.json     受理条件 + 容差参数
```

`meta.json` 的字段是**受理条件**，不是备注。`OracleGolden.Load` 缺任一字段即抛；`meta.image` 的尺寸必须与 PNG 实际解码
尺寸一致、`meta.scene` 必须与 `layout.scene` 一致——三件套不同步是重生成流程被打断的典型痕迹。

`tolerance` 块的唯一事实源是场景描述符 `scenes/<id>.json`，截取时抄进 meta，比对时从 meta 读回。C# 侧的
`OracleTolerance.Baseline` 只是新场景的起步值，并由单测钉住它与首个入库 golden 一致——两处一漂移测试就红。

默认容差与各自抓什么：

| 参数 | 值 | 作用 |
|---|---|---|
| `layoutEpsPx` | 0.5 | x/y/width/height 逐字段 |
| `layoutEpsUnitless` | 0.001 | scaleX/scaleY/alpha |
| `layoutEpsDegrees` | 0.01 | rotation |
| `pixelChannelDelta` | 2 | 单像素任一通道 \|Δ\| 超过它才算「差异像素」 |
| `pixelMaxChannelDelta` | 64 | 硬线：任一像素超过它直接失败（少量但离谱的差异会被占比阈值稀释掉） |
| `pixelDiffRatio` | 0.002 | 差异像素占比上限 |
| `hotspotCell` | 16 | 热点框的网格边长 |

## 重生成基线

行为**有意**变更（改了场景描述符、bump 了 oracle SHA）时才重生成。步骤：

```bash
# 1. 截到临时目录，不要直接覆盖入库基线
./tools/oracle/capture.sh scene-001 --out /tmp/cand

# 2. 与入库基线做实差比对，把 diff 看清楚再决定
FAIRYNEXT_ORACLE_CANDIDATE=/tmp/cand dotnet run --project tests/FairyNext.Tests
#    输出带布局逐字段差异与像素热点框；这条用例只在设了环境变量时存在

# 3. 确认差异是预期的，再覆盖并跑全绿
./tools/oracle/capture.sh scene-001
dotnet run --project tests/FairyNext.Tests

# 4. 提交时在正文写清「为什么这张基线变了」——像素基线的 diff 在 review 里是不可读的，
#    唯一可 review 的是这段话
```

## oracle 只读纪律与 bump SHA

`oracle.lock` 钉死参考实现。本目录的脚本对 fork **只读**：开的是 untitled 空场景，跑完把原场景开回来，全部产物写到
FairyNext 侧。除 Unity 自己的 `Library/ Temp/ Logs/`（fork 内已 gitignore）外不落任何文件进 oracle 工程。

需要动 fork（修 oracle 的 bug）时：

1. 在 fork 里改、提交，拿到新的短 SHA。
2. 改 `oracle.lock` 的 `sha`。
3. **重跑全部 golden**（当前只有 scene-001，之后按 `scenes/` 全量跑一遍）。
4. 单测 `meta.oracleSha == oracle.lock.sha` 会替你把「改了 lock 却忘了重生成」按红。
5. 提交里写明改了 fork 的什么、为什么，以及哪些基线因此变化。

不 bump SHA 就改 fork，等于让基线随 fork 静默演进——「与谁一致」当场失去定义。

## 加一个新场景

1. 写 `scenes/<id>.json`。目前的节点 `kind`：`graph`（`GGraph.DrawRect`，带 `fill`/`line`/`lineSize`）与
   `packageItem`（`UIPackage.CreateObject(pkg, item)`，`width`/`height` 用来拉伸，九宫格由资源自带的 `scale9Grid` 决定）。
   新 `kind` 加在 `lib/declarations.cs` 的 `BuildScene`。
2. 全部用**绝对坐标**。stage 尺寸由驱动钉死，但依赖 GRoot 尺寸做居中/百分比的场景会把 stage 尺寸变成隐藏输入。
3. `tolerance` 块必填（缺了驱动直接报错）；从 `OracleTolerance.Baseline` 的值起步，放宽要在场景 json 里写明理由注释。
4. `./tools/oracle/capture.sh <id>`，然后在 `OracleGoldenTests.cs` 里加装载与自比对的用例。

fork 自带的可用资源可以现场探明，例如列出 `UI/Basics` 里带九宫格的图：

```bash
UNICLI_PROJECT=~/ECS/FairyGUI-unity ~/bin/unicli eval '
var pkg = FairyGUI.UIPackage.GetByName("Basics") ?? FairyGUI.UIPackage.AddPackage("UI/Basics");
var sb = new System.Text.StringBuilder();
foreach (var it in pkg.GetItems())
    if (it.type == FairyGUI.PackageItemType.Image && it.scale9Grid.HasValue)
        sb.Append(it.name).Append(" ").Append(it.width).Append("x").Append(it.height).Append("\n");
return sb.ToString();' --json --no-focus
```

## 确定性是怎么来的

scene-001 连截**九次**（每次一个独立的 PlayMode 会话），`frame.png` 的 sha256 全为
`045997f0d37c726570ebc7af34316c7c8acd6d70d417a18587829772c105a0a5`。靠三条：

1. **stage 尺寸不读 `Screen.width/height`**，由 `Stage.HandleScreenSizeChanged(W,H,0.02)` 强制钉死（internal，反射调用）。
   否则 Game View 窗口一改大小，基线就变。
2. **不用 `Screenshot.Capture`**（它截 Game View，输出尺寸取决于窗口大小与 Aspect 下拉框）。自建正交相机 + `RenderTexture`，
   几何精确复刻 `StageCamera`：`orthographicSize = H/2*upp`、`position = (orthoSize*W/H, -orthoSize, 0)`、`near/far = -30/30`。
3. **不靠「等几帧」**。截帧前用反射调 `Stage.InternalUpdate()` 把显示列表 flush 成网格——编辑器在后台时是否走帧不再影响结果。

第 1、3 条依赖 fork 的 internal 方法名。方法没了就抛「oracle SHA drifted?」，不静默降级。

## 已知边界

- **跨机器像素不保证**。`meta.graphicsDevice`（本机 `Metal`）与 `colorSpace`（`Gamma`）不同即判不可比。换机器生成基线 = 换基线。
- **`capturedUtc` 每次重生成都变**，因此 `meta.json` 每次都进 diff。这是有意的：基线什么时候截的是要留痕的。可比性判定不看这个字段。
- **Unity 侧自带一份 JSON 读取器**（`lib/declarations.cs` 里 60 行）。不能复用 `Tools.OracleCompare` 的那份：eval 程序集由
  `Assembly.Load(byte[])` 载入，要引 FairyNext 的 DLL 就得把它拷进 oracle 的 `Assets/`，违反只读纪律。也不能用
  `UnityEngine.JsonUtility`：实测该程序集里嵌套自定义类字段恒为 null（`Assembly.Location` 为空，Unity 原生序列化器解析不了）。
- **只有 layout + 像素两条腿**。实例流级比对（架构 §平面六「分层断言」的中间那一层）要等自家 RenderStream 存在，不在本包。

## 排障

| 现象 | 原因 / 处置 |
|---|---|
| `找不到 unicli` | 装 UniCli 或设 `UNICLI=/path/to/unicli` |
| `Unity 编辑器服务器没连上` | 打开 fork 工程；`unicli check` 应报 running |
| `oracle HEAD=xxx ≠ oracle.lock sha=yyy` | fork 被切走了。切回去，或走上面的 bump SHA 流程 |
| `UniCli 协议层偶发，重试：…` | 服务器把命名管道里的两份 JSON 当成一份（报 `JSON parse error`），命令没执行。脚本自动重试一次；连续两次即 die |
| `not-in-play-mode` | `PlayMode.Enter` 没生效（编辑器弹了模态框会卡住主循环）。看一眼编辑器窗口 |
| `FairyGUI.Stage.HandleScreenSizeChanged missing` | fork 改了内部 API。驱动要跟着改，且必须 bump SHA |
