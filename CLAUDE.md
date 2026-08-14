# FairyNext — 仓库约定

FairyGUI 运行时的 green-field 重写：单 SoA 树 + GPU 常驻 quad 实例流 + 推送式失效协议 + 编译期资产管线。核心零依赖纯 C#（netstandard2.1），Unity 只是后端之一。

## 宪法

- 设计权威 = `docs/design/` 三篇（总纲十条承诺 / 法定帧协议 P0-P9 / 所有权仲裁表 14 裁决），当前 v1.3。与其冲突的实现是 bug；要改契约先改文档并在提交里写明改了哪条裁决。完整设计书（9 子系统 + 对标 + 评审全文）在 Obsidian `FairyGUI重写/` 与 artifact（链接见 docs/design/README.md）。
- **oracle 只读**：`oracle.lock` 钉死参考实现（~/ECS/FairyGUI-unity @ SHA）。像素对照/布局矩阵/首分歧帧全部对拍它。不改它；修 oracle bug 须 bump SHA 并重跑 golden。
- `src/Contracts/Abi.cs` 是跨界常量单一事实源：改值 → 跑 codegen → 提交生成物 → 布局变更 bump FormatVersion；id append-only 永不重编号。

## 构建与测试

dotnet 不在 PATH：`export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"`（SDK 9.0.315）。

```bash
dotnet build FairyNext.sln
dotnet run --project tests/FairyNext.Tests   # 判定行 RESULT pass=N fail=N，fail>0 即红
```

测试文化：自建 runner + 判定行（不引 xunit）；行为测试进 mock 后端；journey 带/golden/chaos 对照按设计书 §4.9 逐步进驻。构建产物在 `artifacts/`（ArtifactsPath，勿改——src/ 将被 Unity 以 local package 引入，包内不得出现 bin/obj）。

## git

- 远端 `origin = yangtzehina/FairyNext`。本机 git 全局代理 7890 已死：网络操作用 `git -c http.proxy= -c https.proxy= <cmd>`；push 走 ssh://git@ssh.github.com:443/。
- 提交为 Conventional Commits：`type(scope): summary`。

## 文档写作规范（参考文档，非设计书）

直接陈述机制、加粗具体工程事实、禁口号/拟人/空强化词（"简单地""优雅""魔法"）；承诺必须写边界（"What this does NOT claim"）；workaround 注释带病因与实测数字。设计书自身的「宪法/裁决/法定」语汇保留——那是有 owner 的集成契约命名，不是修辞。
