# unity/ — Unity 宿主工程（M1-15 起可用）

Unity 2022.3.62f3（与本机安装及 oracle 一致，勿改版本——中国版版本号坑见 FairyGUI-unity 的 AGENTS.md）。

## 接入形态（M1-15 落地）

`src/` 以 UPM local package 引入：`src/package.json`（`com.fairynext.core`）+ 每工程一个 asmdef，
`Packages/manifest.json` 经 `"file:../../src"` 引源码。dotnet 构建产物由 `Directory.Build.props`
的 ArtifactsPath 移出 `src/`，包目录内不出现 bin/obj。`.csproj` 被 Unity 当 DefaultAsset 导入（无害）；
`src/` 内的 `.meta` 随 local package 一并提交。

三个编译接缝（都在 src/ 各程序集目录内，Unity 专用、dotnet 不受影响）：

- **csc.rsp**（`-langversion:10 -nullable:enable`）：Unity 2022.3 默认钉 C# 9，而 src/ 全程
  file-scoped namespace（C# 10）+ nullable 注解；自带 Roslyn 编译得动，只是要把 langversion 提上去。
- **UnityGlobalUsings.cs**：dotnet 走 SDK ImplicitUsings，Unity 没有——`#if UNITY_5_3_OR_NEWER`
  内补同一组 global using（dotnet 编译时内容为空，不与 SDK 生成物撞车）。
- **src/Core/Analyzers/FairyNext.PropGen.dll**（label `RoslynAnalyzer`、全平台禁用）：属性 setter
  是生成代码，生成器不在场 Core 编译不过。DLL 从 `dotnet build tools/PropGen -c Release` 拷入；
  改了生成器要**手动重拷**（.meta 钉 guid，别删）。

Editor-only 程序集：Backend.Mock（行为门不进游戏包）、Compiler（JIT 预览）、Tools.OracleCompare。

## 本工程承载

- `Assets/FairyNext/UnityVertexStreamBackend.cs`：**Unity 顶点流后端**（M1-15）——每段一个
  MeshRenderer、80B QuadInstance 四角展开成 60B/顶点（color/route/flags/aux 走 UInt32 顶点属性）、
  **y 翻转唯一点 = 流根 GameObject 的 localScale=(1,-1,1)**、fence 回收消费 `Abi.GpuFenceDepth`
  （先 SetActive(false) 再入坟场，满深度帧才 Destroy）。明文边界：离屏 pass M2-12、孤岛渲染 M1-23、
  sortingOrder 是 Unity int16 语义超限夹紧（M3-03 登记）。
- `Assets/FairyNext/Shaders/FairyNextStream.shader` + `abi.g.hlsl`：shader 只消费 AbiGen 生成宏
  （含 FN_RADIAL_FILL_*——径向填充按 extra 的求值形态一条判据完成）；`abi.g.hlsl` 是 AbiGen 的
  **第四份生成物**（与 `shaders/abi.g.hlsl` 逐字节同源，字节比对门共守）。
- `Assets/FairyNext/Editor/FairyNextBackendCheck.cs`：最小验收（菜单 Tools/FairyNext/Backend Check，
  或无头 `-batchmode -executeMethod FairyNext.UnityBackend.Editor.FairyNextBackendCheck.Run`，
  **别传 -nographics**）。产物写 `Logs/`（Temp/ 会在退出时被清空）。
- 编辑期预览宿主（LateUpdate 挂 Tick 的 UIPanel 继任）与树查看器归 M1-25。

## M1-15 验收记录（2026-08-21，本机 2022.3.62f3 batchmode）

**必达：编译零错。** `Unity -batchmode -quit -projectPath unity` exit 0，
9 个程序集（Contracts/Numerics/State/Core/Backend.Mock/Compiler/Tools.OracleCompare/
Backend.Unity/Backend.Unity.Editor）全部产出，**0 error 0 warning**；FairyNext.Core 编译通过
即证明 PropGen analyzer 在 Unity 编译内生效（无生成物则 Core 必炸）。

**尽力：最小场景一帧提交。** `FairyNextBackendCheck.Run` 无头跑，exit 0，七项全 PASS：

```
PASS 两帧走完：ticks=2、presents>=1
PASS 流建成：2 叶 2 quad、段>=1
PASS 上传字节对拍（全量帧）：全等          ← ValidateMirror（mock 侧 MirrorProbe 的等价物）
PASS 后端零协议违约
PASS 截帧非空（5973 亮像素 / 65536）       ← 白 60×60=3600 + 红 Radial360@65%≈2340，账对得上
PASS 上传字节对拍（增量帧）：全等          ← SetAlpha 半透后 Color 通道原位重写
PASS 增量帧后仍零违约
FAIRYNEXT BACKEND CHECK: PASS
```

截帧（`Logs/fairynext-backend-check.png`）人工复核：白方块 + 红色 65% 顺时针扇形（12 点起、
缺口在左上），y 方向正确（内容在相机上半区，未上下镜像）——y 翻转唯一点与径向填充 shader
求值均实证。像素级 oracle 对照归 M1-26/M2-14。
