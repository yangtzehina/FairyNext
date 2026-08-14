namespace FairyNext.AbiGen;

/// <summary>
/// 一份生成物：仓库相对路径 + 全文。路径用 '/' 分隔且恒为相对——**绝对路径不得进入生成流程**，
/// 否则生成结果依赖工作机布局，字节比对门在别人机器上必红（生成器确定性铁律）。
/// </summary>
public readonly struct GeneratedFile
{
    /// <summary>仓库根起算的相对路径，'/' 分隔。</summary>
    public readonly string RelativePath;

    /// <summary>文件全文。行尾恒为 '\n'，编码恒为无 BOM UTF-8（见 <see cref="AbiGenerator.Encode"/>）。</summary>
    public readonly string Text;

    public GeneratedFile(string relativePath, string text)
    {
        RelativePath = relativePath;
        Text = text;
    }
}
