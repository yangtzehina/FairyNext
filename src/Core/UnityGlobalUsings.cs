// Unity 侧编译接缝（M1-15）：dotnet 构建走 SDK 的 ImplicitUsings（obj/ 里自动生成 global usings），
// Unity 没有这个机制——本文件在 Unity 定义的符号下补上同一组 global using。
// dotnet 编译本文件时内容为空（UNITY_* 符号只有 Unity 定义），不与 SDK 生成物撞车。
// 语言版本经同目录 csc.rsp 提到 C# 10（file-scoped namespace 与 global using 的最低版本；
// Unity 2022.3 自带的 Roslyn 编译得动，只是默认 langversion 钉在 9）。
#if UNITY_5_3_OR_NEWER
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
#endif
