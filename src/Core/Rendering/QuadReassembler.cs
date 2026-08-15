using System.Collections.Generic;
using FairyNext.Numerics;

namespace FairyNext.Core.Rendering;

// ============================================================================
// 移植自 fork：~/ECS/FairyGUI-unity Assets/Scripts/Core/Instanced/QuadReassembler.cs @ 08a2d56。
//
// 用途：把**任意三角网格**里能被 quad 实例无损表达的部分回收成实例。本包（M1-13）自己不用它
// ——我们的叶发射器直接产 quad，不经过网格——它服务两条将来的路径：
//   · 文本/自定义 mesh：外部产网格（M2-08 富文本内联对象、作者自定义 IMeshFactory）；
//   · 编译期回收：M1-20 把 fork 的网格产物转成实例时的准入判据。
// 「本包用不上就作为纯函数入库 + 单测」是工作包的明文安排。
//
// 三处改动（每处写病因，不改判据）：
//  ① **static int[4] 暂存改调用方传入**。fork 的 `static readonly int[] sIndices` 是进程级共享
//     可变状态：编译期并行处理多个组件时两个线程会互相踩下标，症状是偶发的「某个 quad 的 UV
//     取自另一个网格」——非确定、不可复现、在 CI 上表现为随机像素门失败。改成 `Span<int>`
//     入参后，暂存的生命周期与调用栈一致，并行安全由类型系统给。
//  ② Unity 类型换 Numerics shim：`Vector3/Vector2/Color32/Matrix4x4` 一一对应
//     （`Matrix4x4` 的最小 shim 正是 M1-02 为本文件保留的——网格顶点带 z，
//     换 Affine2D 要重写数学并丢 z，对拍 oracle 时引入数学改写差异）。
//  ③ 输出换我们的 <see cref="QuadInstance"/>：`color` 是**未乘 α 的基色**（机制 7），
//     `rect/uvA/uvB` 按角归位（fork 那侧本来就是这么写的，字段名不同而已）；
//     route 三分量一律留零——发射侧不写 route 是本仓库的入口纪律。
//
// 判据本体（fork 原文，逐条保留）：三角形按连续两两成对处理，一对必须恰好覆盖 **4 个不同顶点**、
// 变换后包围盒非退化、四点各占一个包围盒角（cornerMask == 15）、四顶点颜色一致。
// 任何一条不满足即**跳过并计数**——调用方按跳过数把该元素整个交给网格兜底路径。
// 为什么这么严：一个实例只带一个 color 与一个轴对齐 rect，
// 三角扇（任意多边形）与顶点色渐变都能伪装成「4 个不同顶点」，
// 静默压平的结果是画错，而画错这一级本设计不留（机制 4「无一级画错」）。
// ============================================================================

/// <summary>
/// 网格 → quad 实例的回收器（纯函数）。准入不过的三角对**只跳过并计数**，绝不近似。
/// </summary>
public static class QuadReassembler
{
    /// <summary>顶点下标暂存的必需长度（一次判定最多记 4 个不同顶点）。</summary>
    public const int ScratchLength = 4;

    /// <summary>
    /// 把网格里合格的 quad 追加到 <paramref name="output"/>，返回追加数。
    /// </summary>
    /// <param name="output">输出实例列表（追加）。</param>
    /// <param name="vertices">顶点位置（网格本地）。</param>
    /// <param name="uvs">顶点 UV（与 <paramref name="vertices"/> 等长）。</param>
    /// <param name="colors">顶点色（可为 null / 短于顶点数 = 视作全白）。</param>
    /// <param name="triangles">三角形下标（每 6 个连续下标视作一对）。</param>
    /// <param name="toLocal">网格本地 → 叶本地的变换。</param>
    /// <param name="offset">叶本地的平移。</param>
    /// <param name="flags">发射器 flags（原样落到每个实例）。</param>
    /// <param name="scratch">调用方给的下标暂存（长度 ≥ <see cref="ScratchLength"/>；fork 的 static 数组在此改为传入）。</param>
    /// <param name="skippedPairs">未能成 quad 的三角对数（&gt;0 = 调用方须走网格兜底）。</param>
    public static int Append(List<QuadInstance> output,
        IReadOnlyList<Vector3> vertices, IReadOnlyList<Vector2> uvs, IReadOnlyList<Color32>? colors,
        IReadOnlyList<int> triangles, in Matrix4x4 toLocal, Vector2 offset, uint flags,
        Span<int> scratch, out int skippedPairs)
    {
        int appended = 0;
        skippedPairs = 0;
        if (output == null || vertices == null || uvs == null || triangles == null)
        {
            UiAssert.That(false, "QuadReassembler.Append 收到 null 输入");
            return 0;
        }
        if (scratch.Length < ScratchLength)
        {
            UiAssert.That(false, $"QuadReassembler.Append 的暂存 span 短于 {ScratchLength}");
            return 0;
        }

        // 三角形数不是 6 的倍数就不可能全是 quad（三角扇的奇数尾）——计一次，让调用方兜底。
        if (triangles.Count % 6 != 0) skippedPairs++;
        bool hasColors = colors != null && colors.Count >= vertices.Count;

        for (int t = 0; t + 5 < triangles.Count; t += 6)
        {
            int distinct = 0;
            for (int k = 0; k < 6; k++)
            {
                int idx = triangles[t + k];
                bool seen = false;
                for (int d = 0; d < distinct; d++)
                {
                    if (scratch[d] == idx) { seen = true; break; }
                }
                if (!seen)
                {
                    if (distinct == ScratchLength) { distinct = ScratchLength + 1; break; }
                    scratch[distinct++] = idx;
                }
            }
            if (distinct != ScratchLength) { skippedPairs++; continue; }

            if (!IndicesInRange(scratch, vertices.Count) || !IndicesInRange(scratch, uvs.Count))
            {
                // fork 直接按下标取值（越界即异常）。编译期要吃外部网格，坏下标必须是「跳过 + 计数」。
                skippedPairs++;
                continue;
            }

            Vector2 p0 = ToPlane(in toLocal, vertices[scratch[0]]);
            Vector2 p1 = ToPlane(in toLocal, vertices[scratch[1]]);
            Vector2 p2 = ToPlane(in toLocal, vertices[scratch[2]]);
            Vector2 p3 = ToPlane(in toLocal, vertices[scratch[3]]);

            Vector2 min = Vector2.Min(Vector2.Min(p0, p1), Vector2.Min(p2, p3));
            Vector2 max = Vector2.Max(Vector2.Max(p0, p1), Vector2.Max(p2, p3));
            Vector2 size = max - min;
            if (!(size.x > 0f) || !(size.y > 0f)) { skippedPairs++; continue; }

            // 每个源顶点的 UV 归到它所在的包围盒角（旋转图集因此仍然采样正确），
            // **并要求四点各占一个不同的角**：三角扇对（任意多边形）与任意旋转的 quad
            // 都有「4 个不同顶点」这个签名，但它们不是轴对齐矩形，实例 rect 表达不了。
            float epsX = size.x * 0.001f + 0.001f;
            float epsY = size.y * 0.001f + 0.001f;
            Vector2 uv00 = default, uv10 = default, uv01 = default, uv11 = default;
            int cornerMask = 0;
            for (int k = 0; k < 4; k++)
            {
                Vector2 p = k == 0 ? p0 : k == 1 ? p1 : k == 2 ? p2 : p3;
                bool xMin = p.x - min.x <= epsX, xMax = max.x - p.x <= epsX;
                bool yMin = p.y - min.y <= epsY, yMax = max.y - p.y <= epsY;
                if (!(xMin || xMax) || !(yMin || yMax)) { cornerMask = -1; break; }

                int corner = (xMax ? 1 : 0) | (yMax ? 2 : 0);
                if ((cornerMask & (1 << corner)) != 0) { cornerMask = -1; break; }
                cornerMask |= 1 << corner;

                Vector2 uv = uvs[scratch[k]];
                if (corner == 0) uv00 = uv;
                else if (corner == 1) uv10 = uv;
                else if (corner == 2) uv01 = uv;
                else uv11 = uv;
            }
            if (cornerMask != 15) { skippedPairs++; continue; }

            // 一个实例只带一个 color：四顶点颜色不一致（RectMesh 角渐变、渐变字形）在 fork 的
            // 旧路径上被压平成「下标序里第一个顶点的颜色」——既画错又依赖拓扑。这里改走网格路径
            // （批次差一点，好过像素错一片）。
            uint color = 0xFFFFFFFFu;
            if (hasColors)
            {
                Color32 c0 = colors![scratch[0]];
                bool uniform = true;
                for (int k = 1; k < 4; k++)
                {
                    if (colors[scratch[k]] != c0) { uniform = false; break; }
                }
                if (!uniform) { skippedPairs++; continue; }
                color = c0.Pack();
            }

            output.Add(new QuadInstance
            {
                Rect = new Vector4(min.x + offset.x, min.y + offset.y, size.x, size.y),
                UvA = new Vector4(uv00.x, uv00.y, uv10.x, uv10.y),
                UvB = new Vector4(uv01.x, uv01.y, uv11.x, uv11.y),
                Color = color,      // **未乘 α 的基色**；最终 color 由流从 baseColor 重算（机制 7）
                Route = 0u,         // 发射侧不写 route（三分量由流盖写）
                Flags = flags,
            });
            appended++;
        }

        return appended;
    }

    private static bool IndicesInRange(ReadOnlySpan<int> scratch, int count)
    {
        for (int i = 0; i < ScratchLength; i++)
        {
            if ((uint)scratch[i] >= (uint)count) return false;
        }
        return true;
    }

    private static Vector2 ToPlane(in Matrix4x4 m, Vector3 v)
    {
        Vector3 p = m.MultiplyPoint3x4(v);
        return new Vector2(p.x, p.y);
    }
}
