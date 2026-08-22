using System.Collections.Generic;

namespace FairyNext.Core.Fgb;

/// <summary>
/// 一条纹理/声音符号引用（TREF 记录的解码形态）。
/// 它是**符号**不是资源：pkgId + 条目 id 唯一确定「要哪张图」，
/// 至于那张图在本次运行里叫什么编号、什么时候真被读进显存，由 <see cref="IAssetSource"/> 说了算。
/// </summary>
public readonly struct FgbTexRef
{
    /// <summary>包 id 字符串的 FNV-1a 64（本包引用 = 自身 id）。</summary>
    public readonly ulong PkgId;
    /// <summary>图集条目 id（STRT 取出的字符串）。</summary>
    public readonly string Item;
    /// <summary>种类（0 = 纹理；声音/骨骼按 append-only 续编号）。</summary>
    public readonly ushort Kind;
    /// <summary>**包内**分配的段键纹理编号（PTCH 要把它换成运行期编号的那个值）。</summary>
    public readonly ushort LocalTexId;

    public FgbTexRef(ulong pkgId, string item, ushort kind, ushort localTexId)
    {
        PkgId = pkgId; Item = item ?? string.Empty; Kind = kind; LocalTexId = localTexId;
    }

    /// <inheritdoc/>
    public override string ToString() => $"tex 0x{PkgId:X16}/{Item} kind={Kind} local={LocalTexId}";
}

/// <summary>
/// 可替换的纹理装载通道（架构机制 16「纹理生命周期集中化」+ 机制 2 的「绑定」那一步）。
/// **两段式**，这正是懒装载的落点：
///  · <see cref="TryResolve"/> 在**装载期**跑，把符号换成宿主的运行期编号——只是查表/登记，
///    不碰像素，因而可以对整包一次做完，PTCH 的回填靠的就是它；
///  · <see cref="TryAcquire"/> 在**首用时**跑（实例 Realize），这才是真读文件/上传显存的那次。
/// 分开的理由：段键与 CONT 里的纹理编号必须在装载结束时就正确（否则 Extract 会按编译机的
/// 编号切段），而像素在没有任何实例被画之前一个字节都不该读。
/// </summary>
public interface IAssetSource
{
    /// <summary>符号 → 运行期纹理编号。false = 解析不了（装载期有声计数，不拒载）。</summary>
    bool TryResolve(in FgbTexRef symbol, out uint texId);

    /// <summary>首用时装载像素。false = 装不上（该实例的相关叶降级，不画错）。</summary>
    bool TryAcquire(uint texId);
}

/// <summary>
/// 无宿主的纹理通道（单测 / 无 GPU 的 CI / 编译预览）：按**首次出现序**发运行期编号，
/// 像素永不真装但 <see cref="TryAcquire"/> 记账。同一符号重复解析拿同一个编号——
/// 「符号相等 ⇒ 编号相等」是段键合并的前提，通道换实现也不许破。
/// </summary>
public sealed class NullAssetSource : IAssetSource
{
    private readonly Dictionary<string, uint> _ids = new Dictionary<string, uint>(StringComparer.Ordinal);
    private readonly HashSet<uint> _acquired = new HashSet<uint>();
    private uint _next;

    /// <summary>首个分配的运行期编号（默认 1000——刻意与包内 texId 不重叠，回填漏做会立刻现形）。</summary>
    public NullAssetSource(uint firstId = 1000u) { _next = firstId; }

    /// <summary>已发出的编号数。</summary>
    public int ResolvedCount => _ids.Count;

    /// <summary>已被 <see cref="TryAcquire"/> 装载过的编号数（懒装载的观测点）。</summary>
    public int AcquiredCount => _acquired.Count;

    /// <summary>某个编号是否已装载。</summary>
    public bool IsAcquired(uint texId) => _acquired.Contains(texId);

    /// <inheritdoc/>
    public bool TryResolve(in FgbTexRef symbol, out uint texId)
    {
        string key = symbol.PkgId.ToString("X16") + "/" + symbol.Item;
        if (!_ids.TryGetValue(key, out texId))
        {
            texId = _next++;
            _ids.Add(key, texId);
        }
        return true;
    }

    /// <inheritdoc/>
    public bool TryAcquire(uint texId)
    {
        _acquired.Add(texId);
        return true;
    }
}

/// <summary>
/// 已装载包的登记处（装载门 4 的对照物来源）：按包 id 回答「你那边的 sourceHash 是多少」。
/// DEPS 的逐项比对与 combinedRefHash 的重算都读它——**重算**而不是信头里的数，
/// 因为头里的数是编译那一刻的快照，而依赖链是装载这一刻的事实。
/// </summary>
public interface IFgbPackageRegistry
{
    /// <summary>包 id（FNV-1a 64）→ 该包的 sourceHash。false = 这个依赖当前没装。</summary>
    bool TryGetSourceHash(ulong pkgId, out ulong sourceHash);
}

/// <summary>字典形态的登记处（宿主没有真包表时的最小实现，也是用例的语料口）。</summary>
public sealed class FgbPackageRegistry : IFgbPackageRegistry
{
    private readonly Dictionary<ulong, ulong> _hashes = new Dictionary<ulong, ulong>();

    /// <summary>登记一个包（重复登记覆盖）。</summary>
    public void Register(ulong pkgId, ulong sourceHash) => _hashes[pkgId] = sourceHash;

    /// <inheritdoc/>
    public bool TryGetSourceHash(ulong pkgId, out ulong sourceHash) => _hashes.TryGetValue(pkgId, out sourceHash);
}
