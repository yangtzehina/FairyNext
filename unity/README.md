# unity/ — Unity 宿主工程（M1 后半接入）

Unity 2022.3.62f3（与本机安装及 oracle 一致，勿改版本——中国版版本号坑见 FairyGUI-unity 的 AGENTS.md）。

接入方式（M1 决定项，倾向方案已定）：`src/` 以 UPM local package 引入——在 `src/` 放 `package.json` + 各工程 asmdef，Unity 经 `"file:../../src"` 引源码；dotnet 侧构建产物已由 `Directory.Build.props` 的 ArtifactsPath 移出 `src/`，包目录内不会出现 bin/obj。`.csproj` 会被 Unity 当 DefaultAsset 导入（无害）；`src/` 内的 `.meta` 噪音在启用 local package 时一并提交。

本工程承载：Unity 段后端、编辑期预览宿主（UIPanel 继任）、树查看器 EditorWindow（M1 最小版）。
