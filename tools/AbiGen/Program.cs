using FairyNext.AbiGen;

namespace FairyNext.AbiGen.Cli;

/// <summary>
/// ABI codegen 外壳：`dotnet run --project tools/AbiGen [--check] [仓库根]`。
///
///  - 默认：把三份生成物写入仓库（内容未变则不落笔——不制造无谓的 mtime 变动）。
///  - --check：只比对不写，漂移则打印首差异并以 1 退出（CI 里等价于测试宿主的字节比对门）。
///
/// 决策逻辑全在 AbiGen.Core 的纯函数里；本文件不得引入任何影响生成文本的输入
/// （时间、环境变量、机器路径）——它只负责搬运字节。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        bool check = false;
        string? root = null;
        foreach (string a in args)
        {
            if (a == "--check") check = true;
            else if (a.StartsWith("-", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"未知参数：{a}（可用：--check [仓库根]）");
                return 2;
            }
            else root = a;
        }

        root ??= RepoRoot.Find(AppContext.BaseDirectory) ?? RepoRoot.Find(Directory.GetCurrentDirectory());
        if (root == null)
        {
            Console.Error.WriteLine($"找不到仓库根（向上未见 {RepoRoot.MarkerFile}）；可显式传入路径。");
            return 2;
        }

        int drifted = 0, written = 0, unchanged = 0;
        foreach (GeneratedFile file in AbiGenerator.GenerateAll())
        {
            byte[] expected = AbiGenerator.Encode(file.Text);
            string path = RepoRoot.ToAbsolute(root, file.RelativePath);
            byte[]? actual = File.Exists(path) ? File.ReadAllBytes(path) : null;

            if (actual != null && ByteCompare.FirstDifference(expected, actual) < 0)
            {
                unchanged++;
                Console.WriteLine($"  未变  {file.RelativePath}");
                continue;
            }

            if (check)
            {
                drifted++;
                Console.WriteLine($"  漂移  {file.RelativePath}");
                if (actual != null) Console.WriteLine("        " + ByteCompare.Describe(expected, actual, ByteCompare.FirstDifference(expected, actual)));
                else Console.WriteLine("        文件缺失");
                continue;
            }

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, expected);
            written++;
            Console.WriteLine($"  写入  {file.RelativePath}");
        }

        if (check && drifted > 0)
        {
            Console.Error.WriteLine($"ABI 生成物与定义点不一致（{drifted} 份）：跑 dotnet run --project tools/AbiGen 并提交生成物。");
            return 1;
        }

        Console.WriteLine($"ABIGEN written={written} unchanged={unchanged}");
        return 0;
    }
}
