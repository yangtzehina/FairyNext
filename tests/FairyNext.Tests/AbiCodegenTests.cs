using FairyNext.AbiGen;
using FairyNext.Contracts;

namespace FairyNext.Tests;

/// <summary>
/// ABI 字节比对门（M1-01）——验证平面不变量 10 / 编译平面构建期断言 6。
///
/// 门的形状：在内存重新生成三份产物，与仓库已提交文件**逐字节**比较。不比语义、不比 AST、
/// 不做规范化：行尾、空格、注释一并算数。理由是这条门守的是「C#↔HLSL 边界上两份手抄的偏移
/// 会错位」，而错位表现为无法归因的花屏——门必须比 shader 编译器更早、更严。
///
/// 失败时打印首差异偏移与上下文（首差异字节定位），修法只有一条：
///   dotnet run --project tools/AbiGen 并提交生成物。
/// </summary>
public static partial class Program
{
    private static void AbiCodegenSuite()
    {
        string? root = RepoRoot.Find(AppContext.BaseDirectory);
        Check($"ABI 门: 定位仓库根（向上找 {RepoRoot.MarkerFile}）", root != null);

        foreach (GeneratedFile file in AbiGenerator.GenerateAll())
        {
            string? drift = root == null ? "仓库根未定位，无法比对" : Drift(root, file);
            Check($"ABI 字节比对门: {file.RelativePath}", drift == null);
            if (drift != null) Console.WriteLine($"     {drift}");
        }

        Check("ABI: QuadInstance = 80B",
            Abi.QuadInstanceSize == 80 && AbiLayout.QuadInstanceSize == 80);

        Check("ABI: ClipEntry = 48B",
            Abi.ClipEntrySize == 48 && AbiLayout.ClipEntrySize == 48);

        // 相邻字段首尾相接、首字段落在 0、末字段收口于结构尺寸——空洞与重叠都是 ABI 事故。
        Check("ABI: 偏移表自洽（相邻字段 offset + size == 下一 offset）",
            FieldsContiguous(Abi.QuadInstanceFields, Abi.QuadInstanceSize)
            && FieldsContiguous(Abi.ClipEntryFields, Abi.ClipEntrySize));

        // 位域表必须覆盖整个 u32（含保留段）：没被任何条目认领的位 = 将来两处各自认领。
        Check("ABI: route/flags 位域覆盖全 32 位",
            BitsCoverWord(Abi.RouteBits) && BitsCoverWord(Abi.QuadFlagBits));

        // 生成物常量 vs 定义点表的交叉校验（生成物自带），外加分组区间与 id 单调性。
        Check("ABI: 定义点交叉校验 + PropId 分组自洽",
            AbiLayout.Verify() == null && PropGroupsDisjoint() && PropIdsWellFormed());
    }

    /// <summary>返回 null = 逐字节相等；否则返回首差异说明（含修法）。</summary>
    private static string? Drift(string root, GeneratedFile file)
    {
        byte[] expected = AbiGenerator.Encode(file.Text);
        string path = RepoRoot.ToAbsolute(root, file.RelativePath);
        if (!File.Exists(path)) return "生成物缺失——跑 dotnet run --project tools/AbiGen 并提交生成物。";

        byte[] actual = File.ReadAllBytes(path);
        int diff = ByteCompare.FirstDifference(expected, actual);
        if (diff < 0) return null;

        return ByteCompare.Describe(expected, actual, diff)
            + "\n        修法：dotnet run --project tools/AbiGen 后提交生成物。";
    }

    private static bool FieldsContiguous(AbiField[] fields, int totalSize)
    {
        if (fields.Length == 0 || fields[0].Offset != 0) return false;
        for (int i = 1; i < fields.Length; i++)
        {
            if (fields[i - 1].Offset + fields[i - 1].Size != fields[i].Offset) return false;
        }
        AbiField last = fields[fields.Length - 1];
        return last.Offset + last.Size == totalSize;
    }

    private static bool BitsCoverWord(AbiBitField[] bits)
    {
        if (bits.Length == 0 || bits[0].Shift != 0) return false;
        for (int i = 1; i < bits.Length; i++)
        {
            if (bits[i - 1].Shift + bits[i - 1].Width != bits[i].Shift) return false;
        }
        AbiBitField last = bits[bits.Length - 1];
        return last.Shift + last.Width == 32;
    }

    /// <summary>分组区间升序且互不重叠；id 0 保留给 None 哨兵，故首组不得从 0 起。</summary>
    private static bool PropGroupsDisjoint()
    {
        if (Abi.PropGroups.Length == 0 || Abi.PropGroups[0].First == 0) return false;
        for (int i = 0; i < Abi.PropGroups.Length; i++)
        {
            if (Abi.PropGroups[i].First > Abi.PropGroups[i].Last) return false;
            if (i > 0 && Abi.PropGroups[i].First <= Abi.PropGroups[i - 1].Last) return false;
        }
        return true;
    }

    /// <summary>每个 id 落在自称的分组区间内，且表内严格递增（append-only 的可机检形式）。</summary>
    private static bool PropIdsWellFormed()
    {
        int prev = 0;
        foreach (AbiPropId p in Abi.PropIds)
        {
            if (p.Id <= prev) return false;
            prev = p.Id;

            bool located = false;
            foreach (AbiPropGroup g in Abi.PropGroups)
            {
                if (g.Name != p.Group) continue;
                located = p.Id >= g.First && p.Id <= g.Last;
                break;
            }
            if (!located) return false;
        }
        return true;
    }
}
