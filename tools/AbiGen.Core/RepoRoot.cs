using System.IO;

namespace FairyNext.AbiGen;

/// <summary>
/// 仓库根定位：从给定目录向上找 <see cref="MarkerFile"/>。生成器外壳与测试宿主共用一份实现，
/// 两处「各自数几层 ..」是配置漂移的经典来源（artifacts 输出深度一变就断）。
/// 注意：定位结果只用于**读写文件**，绝不进入生成物文本——绝对路径入文即破坏确定性。
/// </summary>
public static class RepoRoot
{
    public const string MarkerFile = "FairyNext.sln";

    /// <summary>找到返回绝对路径，找不到返回 null（调用方负责报错，不抛）。</summary>
    public static string? Find(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, MarkerFile))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>把生成物的相对路径（'/' 分隔）拼成本机绝对路径。</summary>
    public static string ToAbsolute(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
