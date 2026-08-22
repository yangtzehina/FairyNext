namespace FairyNext.Core.Fgb;

/// <summary>
/// 发布文件名的身份语义（装载门 5）：<c>名字_id[.sN][.bX].fgb</c>。
///
/// **以 id 定身份，不以名字**——这条来自 fork 的重名/非导出遮蔽教训：包名可以撞、可以改，
/// 只有 id 是编辑器生成后不变的那一个。名字段留在文件名里纯粹给人看，装载器一个字节都不比。
///
/// <c>.sN</c> = scaleLevel、<c>.bX</c> = branchId，缺省即 0（不写 = 基准档/主干）。
/// 二者是**编译期变体**：改的是几何与资源本体，不是运行时开关，故它们进四维身份、
/// 也进文件名——文件名与头内值不一致 = 部署错配（改名/改档没重烘），响亮拒载。
/// </summary>
public readonly struct FgbFileName
{
    /// <summary>包名（人读段；装载器不比对）。</summary>
    public readonly string Name;
    /// <summary>包 id 字符串（身份段）。</summary>
    public readonly string Id;
    /// <summary>内容缩放档（缺省 0）。</summary>
    public readonly int ScaleLevel;
    /// <summary>branch 变体（缺省 0）。</summary>
    public readonly int BranchId;

    private FgbFileName(string name, string id, int scaleLevel, int branchId)
    {
        Name = name; Id = id; ScaleLevel = scaleLevel; BranchId = branchId;
    }

    /// <summary>
    /// 解析文件名。false = 不合语义（缺 <c>_id</c> 段 / 档位非数字 / 扩展名不对）——
    /// 调用方按门 5 拒载：一个连名字都不对的文件不该被当成这个包。
    /// 只接受 <c>.fgb</c> 扩展名；路径分隔符由调用方先剥（本函数是纯字符串判定，不碰文件系统）。
    /// </summary>
    public static bool TryParse(string fileName, out FgbFileName parsed, out string error)
    {
        parsed = default;
        error = "";
        if (string.IsNullOrEmpty(fileName)) { error = "文件名为空"; return false; }

        string s = fileName;
        int slash = s.LastIndexOfAny(new[] { '/', '\\' });
        if (slash >= 0) s = s.Substring(slash + 1);

        const string Ext = ".fgb";
        if (!s.EndsWith(Ext, StringComparison.Ordinal)) { error = "扩展名不是 " + Ext + "：" + s; return false; }
        s = s.Substring(0, s.Length - Ext.Length);

        // 变体段从尾部逐个剥：顺序不敏感，但每段只许出现一次（.s1.s2 是错配不是「后者胜」）。
        int scale = 0, branch = 0;
        bool sawScale = false, sawBranch = false;
        while (true)
        {
            int dot = s.LastIndexOf('.');
            if (dot < 0) break;
            string tail = s.Substring(dot + 1);
            if (tail.Length >= 2 && (tail[0] == 's' || tail[0] == 'b') && AllDigits(tail, 1))
            {
                if (!int.TryParse(tail.Substring(1), System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out int v))
                {
                    error = "变体段数值越界：." + tail;
                    return false;
                }
                if (tail[0] == 's')
                {
                    if (sawScale) { error = "重复的 scaleLevel 段：." + tail; return false; }
                    sawScale = true; scale = v;
                }
                else
                {
                    if (sawBranch) { error = "重复的 branchId 段：." + tail; return false; }
                    sawBranch = true; branch = v;
                }
                s = s.Substring(0, dot);
                continue;
            }
            break;
        }

        int us = s.LastIndexOf('_');
        if (us <= 0 || us == s.Length - 1) { error = "缺 名字_id 段：" + s; return false; }
        parsed = new FgbFileName(s.Substring(0, us), s.Substring(us + 1), scale, branch);
        return true;
    }

    private static bool AllDigits(string s, int from)
    {
        if (from >= s.Length) return false;
        for (int i = from; i < s.Length; i++)
            if (s[i] < '0' || s[i] > '9') return false;
        return true;
    }

    /// <inheritdoc/>
    public override string ToString() => Name + "_" + Id + ".s" + ScaleLevel + ".b" + BranchId + ".fgb";
}
