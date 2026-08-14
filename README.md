# FairyNext

FairyGUI 运行时的 green-field 重写。**输入格式不变**（FairyGUI 编辑器 .fui 发布物），运行时换代：

- **单 SoA 节点树 + 64bit 代际句柄**——废除 GObject/DisplayObject 双树与每节点 GameObject；
- **GPU 常驻 quad 实例流**为唯一渲染主路径（80B/实例 ABI，段/run/栅栏/孤岛/transform 槽/per-instance ClipEntry）——滚动 = 写一个矩阵槽；
- **推送式失效协议 + 法定帧相位 P0-P9**——setter 只写值+Mark，帧 = 按相位排水，静止帧零遍历；
- **编译期资产管线**：.fui → FGB blob（零解析、MemoryMarshal.Cast 直读），编译器 = 无头运行时（同一实现，无双实现漂移），arm-not-mount 首用协议；
- **核心零依赖纯 C#**（netstandard2.1）：`dotnet build && dotnet test` 不碰 Unity；Unity 2022.3 与 WebGL2 是后端。

设计书（十条承诺 / 帧协议 / 14 条仲裁裁决，v1.3）见 `docs/design/`。参考实现（像素 oracle）= [FairyGUI-unity fork](https://github.com/yangtzehina/FairyGUI-unity)，由 `oracle.lock` 钉死 SHA。

## 布局

```
src/Core            核心：树/失效/状态/布局/文本（零依赖）
src/Contracts       ABI 常量单一事实源（codegen → HLSL/mock）
src/Compiler        .fui → FGB（引用 Core：编译器即无头运行时）
src/Backend.Mock    mock 后端 + 参考光栅（无 GPU 行为测试）
tests/              自建 runner（RESULT pass=N fail=N）；journey/golden/chaos 进驻中
unity/              Unity 宿主工程（段后端 + 编辑期预览 + 树查看器）
docs/design/        宪法三篇（v1.3）
```

## 里程碑

M1 骨架（顶点流+mock 后端、整流重编、静态渲染、事件命中、FGB 装载、SDF/位图文本、树查看器最小版）→ M2 完整运行时（状态层/滚动/虚拟列表/文本输入/曲线字形）→ M3 生产化。详见设计书 §5。
