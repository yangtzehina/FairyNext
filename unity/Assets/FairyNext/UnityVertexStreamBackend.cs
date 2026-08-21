#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FairyNext.Contracts;
using FairyNext.Core.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace FairyNext.UnityBackend
{
    /// <summary>
    /// Unity 顶点流后端（M1-15；架构「平面三」机制 12 的顶点流半边）：
    /// 每段一个 MeshRenderer，80B <see cref="QuadInstance"/> 四角展开成 60B/顶点的顶点流，
    /// 整数字段（color/route/flags/aux）一律 UInt32 顶点属性——float32 顶点流会把
    /// glyphIndex 圆到邻字形（fork 教训，abi.g.hlsl 头注同款）。
    ///
    /// ── y 翻转唯一点 ─────────────────────────────────────────────────────────
    /// 核心、FGB、命中、提取全程 y 向下；Unity 世界 y 向上。翻转**只**发生在每条流根
    /// GameObject 的 <c>localScale = (1, -1, 1)</c> 一处（架构机制⑪原文）——顶点、槽矩阵、
    /// clip 条目、shader 全部留在流的 y-down 空间里，本文件除流根外不得出现第二个负号。
    /// 绕序随翻转反向，shader 以 <c>Cull Off</c> 消化。
    ///
    /// ── fence 回收 ──────────────────────────────────────────────────────────
    /// <see cref="DestroyStream"/> 不立即销毁：流根先 <c>SetActive(false)</c>（同一帧内
    /// Destroy 不生效，僵尸段会污染像素探针——fork 实测），入 pending 队列；
    /// <see cref="BeginFrame"/> 时对入队满 <see cref="Abi.GpuFenceDepth"/> 帧者才真 Destroy。
    /// pending 中的句柄收到任何调用 = use-after-free，进 <see cref="Violations"/>。
    ///
    /// ── 本包不做（登记在 plan.md 对应包）───────────────────────────────────
    /// 离屏 pass（M2-12 滤镜/fadeGroup）：Caps.SupportsOffscreen = false，误调进 Violations；
    /// 孤岛渲染（M1-23）：记录收下、不产生原生对象；宿主 MonoBehaviour（LateUpdate 挂 Tick）归 M1-25。
    /// sortingOrder 是 Unity 的 int16 语义：paintOrder×16 超 32767 的巨树在本后端会夹紧
    /// （计一次 Violations），WebGL2 后端按数组序绘制无此上限（M3-03）。
    /// </summary>
    public sealed class UnityVertexStreamBackend : IRenderBackend, IDisposable
    {
        // ── 顶点布局（与 FairyNextStream.shader 的 appdata 一一对应）────────────
        [StructLayout(LayoutKind.Sequential)]
        private struct StreamVertex
        {
            public float Px, Py, Pz;          // POSITION：槽本地坐标（y 向下；翻转在流根）
            public float U, V;                // TEXCOORD0
            public uint Color, Route, Flags, Aux;   // TEXCOORD1：UInt32 ×4
            public float Ex, Ey, Ez, Ew;      // TEXCOORD2：extra
            public float Cx, Cy;              // TEXCOORD3：quad 内归一化角（径向填充求值域）
        }

        private static readonly VertexAttributeDescriptor[] VertexLayout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.UInt32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.Float32, 2),
        };

        private sealed class SegmentNode
        {
            internal GameObject Go = null!;
            internal Mesh Mesh = null!;
            internal MeshRenderer Renderer = null!;
            internal Material Material = null!;
        }

        private sealed class UStream
        {
            internal uint Gen;
            internal bool Alive;
            internal string? DebugName;
            internal GameObject Root = null!;

            internal QuadInstance[] Quads = Array.Empty<QuadInstance>();
            internal int QuadCount;
            internal ClipEntry[] Clips = Array.Empty<ClipEntry>();
            internal int ClipCount;
            internal SlotEntry[] Slots = Array.Empty<SlotEntry>();
            internal int SlotCount;
            internal SegmentDesc[] Segments = Array.Empty<SegmentDesc>();
            internal RunOrder[] Runs = Array.Empty<RunOrder>();
            internal readonly List<SegmentNode> Nodes = new List<SegmentNode>();

            internal int DirtyMin = int.MaxValue;
            internal int DirtyMax = -1;
            internal bool SegTableDirty;
            internal bool RunsDirty;
            internal bool UniformsDirty;

            internal MaterialPropertyBlock Props = null!;
            internal Vector4[] SlotArray = null!;
            internal Vector4[] SlotInvArray = null!;
            internal Vector4[] ClipArray = null!;
        }

        private readonly List<UStream> _streams = new List<UStream>();
        private readonly List<(GameObject Go, ulong Frame)> _graveyard = new List<(GameObject, ulong)>();
        private readonly List<(int Stream, ulong Frame)> _fencePending = new List<(int, ulong)>();
        private readonly List<IslandRecordSlot> _islands = new List<IslandRecordSlot>();
        private readonly List<string> _violations = new List<string>();
        private readonly Dictionary<uint, Texture> _textures = new Dictionary<uint, Texture>();

        private sealed class IslandRecordSlot
        {
            internal uint Gen;
            internal bool Alive;
            internal IslandDesc Desc;
            internal IslandSync Sync;
        }

        private readonly Transform? _parent;
        private readonly Shader _shader;
        private bool _inFrame;
        private ulong _frameId;
        private bool _mainBound;
        private int _drawsThisFrame;
        private int _uploadBytesThisFrame;
        private ulong _ticks, _presents;
        private bool _sortingClampReported;

        /// <summary>建后端。找不到 shader "FairyNext/Stream" 即 throw——没有着色器的后端是花屏机。</summary>
        /// <param name="parent">流根挂接点（null = 场景根）。</param>
        public UnityVertexStreamBackend(Transform? parent = null)
        {
            _parent = parent;
            _shader = Shader.Find("FairyNext/Stream")
                ?? throw new InvalidOperationException("找不到 shader FairyNext/Stream（unity/Assets/FairyNext/Shaders）");
        }

        // ── 诊断面 ──────────────────────────────────────────────────────────

        /// <summary>协议违约记录（与 mock 同一纪律：release 也记）。</summary>
        public IReadOnlyList<string> Violations => _violations;

        /// <summary>fence pending 深度（≤ <see cref="Abi.GpuFenceDepth"/> 为健康）。</summary>
        public int FencePendingDepth => _fencePending.Count;

        /// <summary>登记一张纹理（<see cref="TexId"/> 是资产侧身份，原生对象归后端私有）。</summary>
        public void RegisterTexture(TexId id, Texture texture)
        {
            if (id.IsNone) { Violate("RegisterTexture 收到 TexId.None（纯色槽由后端绑 1×1 白）"); return; }
            _textures[id.Value] = texture;
        }

        /// <summary>
        /// **上传字节神谕的 Unity 侧等价物**（mock 侧见 <c>MockBackend.MirrorProbe</c>）：
        /// 把后端累积的实例前缀与 CPU 镜像逐字节对拍。返回 null = 全等；否则返回首差异说明。
        /// 编辑器验证脚本每帧调用；无 GPU 回读，比的是「后端收到并保存的字节」。
        /// </summary>
        public string? ValidateMirror(RenderStream mirror)
        {
            UStream? s = Resolve(mirror.Handle, nameof(ValidateMirror));
            if (s == null) return "句柄无效或流已销毁";
            ReadOnlySpan<QuadInstance> mq = mirror.Quads;
            if (s.QuadCount < mq.Length) return $"镜像 {mq.Length} 实例、后端只累积到 {s.QuadCount}——有区间从未被上传";
            ReadOnlySpan<byte> got = MemoryMarshal.AsBytes(new ReadOnlySpan<QuadInstance>(s.Quads, 0, mq.Length));
            ReadOnlySpan<byte> want = MemoryMarshal.AsBytes(mq);
            for (int i = 0; i < want.Length; i++)
            {
                if (got[i] != want[i])
                    return $"quad {i / Abi.QuadInstanceSize} 内偏移 {i % Abi.QuadInstanceSize} 与镜像不符";
            }
            return null;
        }

        // ── IRenderBackend：身份与能力 ──────────────────────────────────────

        /// <inheritdoc/>
        public string Name => "unity-vertex";

        /// <inheritdoc/>
        public int ShaderAbiVersion => Abi.ShaderAbiVersion;

        /// <inheritdoc/>
        public BackendCaps Caps => new BackendCaps
        {
            SupportsOffscreen = false,        // M2-12 才交付
            SupportsFence = true,             // 帧计数 fence（GpuFenceDepth 帧后释放）
            FenceQueueDepth = Abi.GpuFenceDepth,
            SelfDrawn = false,                // Unity 相机驱动绘制：零脏帧短路不适用（不变量 13 原文）
            MaxTextureSlots = Abi.SegmentMaxTextures,
        };

        // ── 帧括号 ──────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public void BeginFrame(ulong frameId)
        {
            if (_inFrame) Violate($"BeginFrame 重入（f{_frameId} 未收帧就开 f{frameId}）");
            _inFrame = true;
            _frameId = frameId;
            _mainBound = false;
            _drawsThisFrame = 0;
            _uploadBytesThisFrame = 0;

            // fence 到期：入队满 GpuFenceDepth 帧的原生对象此刻才真销毁（不变量 11）。
            for (int i = _graveyard.Count - 1; i >= 0; i--)
            {
                if (frameId < _graveyard[i].Frame + (ulong)Abi.GpuFenceDepth) continue;
                DestroyObject(_graveyard[i].Go);
                _graveyard.RemoveAt(i);
            }
            for (int i = _fencePending.Count - 1; i >= 0; i--)
            {
                if (frameId >= _fencePending[i].Frame + (ulong)Abi.GpuFenceDepth) _fencePending.RemoveAt(i);
            }
        }

        /// <inheritdoc/>
        public FrameReceipt EndFrame(in FrameStats stats)
        {
            if (!_inFrame) Violate("EndFrame 于帧括号外");
            if (stats.FrameId != 0 && stats.FrameId != _frameId)
                Violate($"EndFrame 的 stats.FrameId={stats.FrameId} 与 BeginFrame 的 {_frameId} 不一致");

            for (int i = 0; i < _streams.Count; i++)
            {
                if (_streams[i].Alive) ApplyPending(_streams[i]);
            }

            _ticks++;
            bool presented = stats.Dirty && _drawsThisFrame > 0;
            if (presented) _presents++;
            var receipt = new FrameReceipt(stats.FrameId != 0 ? stats.FrameId : _frameId,
                presented, _ticks, _presents, _uploadBytesThisFrame);
            _inFrame = false;
            return receipt;
        }

        // ── 流生命周期 ──────────────────────────────────────────────────────

        /// <inheritdoc/>
        public StreamHandle CreateStream(in StreamDesc desc)
        {
            var s = new UStream
            {
                Gen = 1,
                Alive = true,
                DebugName = desc.DebugName,
                Props = new MaterialPropertyBlock(),
                SlotArray = new Vector4[Abi.TransformSlotBudget * 2],
                SlotInvArray = new Vector4[Abi.TransformSlotBudget * 2],
                ClipArray = new Vector4[Abi.ClipEntryBudget * 3],
            };
            // 槽 0 = identity（含逆）：流建出来那一刻数组就是自洽的，不等第一次 WriteSlots。
            s.SlotArray[0] = new Vector4(1f, 0f, 0f, 1f);
            s.SlotInvArray[0] = new Vector4(1f, 0f, 0f, 1f);

            s.Root = new GameObject("FairyNextStream:" + (desc.DebugName ?? "stream"));
            if (_parent != null) s.Root.transform.SetParent(_parent, false);
            // ★ y 翻转唯一点：全链路只此一处（架构机制⑪）。
            s.Root.transform.localScale = new Vector3(1f, -1f, 1f);

            _streams.Add(s);
            return new StreamHandle(_streams.Count, s.Gen);
        }

        /// <inheritdoc/>
        public void DestroyStream(StreamHandle stream)
        {
            UStream? s = Resolve(stream, nameof(DestroyStream));
            if (s == null) return;
            s.Alive = false;
            s.Root.SetActive(false);                       // 同帧 Destroy 不生效：先熄灯再入坟场
            _graveyard.Add((s.Root, _frameId));
            _fencePending.Add((stream.Index, _frameId));
            if (_fencePending.Count > Abi.GpuFenceDepth)
                Violate($"fence pending 深度 {_fencePending.Count} 超 GpuFenceDepth={Abi.GpuFenceDepth}");
        }

        // ── 上传（CPU 累积 + 脏账；网格重建统一在 EndFrame）────────────────

        /// <inheritdoc/>
        public void UploadInstances(StreamHandle stream, int firstQuad, ReadOnlySpan<QuadInstance> quads)
        {
            RequireFrame(nameof(UploadInstances));
            UStream? s = Resolve(stream, nameof(UploadInstances));
            if (s == null) return;
            if (firstQuad < 0) { Violate($"UploadInstances 起点为负（{firstQuad}）"); return; }

            Ensure(ref s.Quads, firstQuad + quads.Length);
            quads.CopyTo(new Span<QuadInstance>(s.Quads, firstQuad, quads.Length));
            if (firstQuad + quads.Length > s.QuadCount) s.QuadCount = firstQuad + quads.Length;
            if (firstQuad < s.DirtyMin) s.DirtyMin = firstQuad;
            if (firstQuad + quads.Length - 1 > s.DirtyMax) s.DirtyMax = firstQuad + quads.Length - 1;
            _uploadBytesThisFrame += quads.Length * Abi.QuadInstanceSize;
        }

        /// <inheritdoc/>
        public void UploadClips(StreamHandle stream, int firstEntry, ReadOnlySpan<ClipEntry> entries)
        {
            RequireFrame(nameof(UploadClips));
            UStream? s = Resolve(stream, nameof(UploadClips));
            if (s == null) return;
            if (firstEntry < 0) { Violate($"UploadClips 起点为负（{firstEntry}）"); return; }

            Ensure(ref s.Clips, firstEntry + entries.Length);
            entries.CopyTo(new Span<ClipEntry>(s.Clips, firstEntry, entries.Length));
            if (firstEntry + entries.Length > s.ClipCount) s.ClipCount = firstEntry + entries.Length;
            s.UniformsDirty = true;
            _uploadBytesThisFrame += entries.Length * Abi.ClipEntrySize;
        }

        /// <inheritdoc/>
        public void WriteSlots(StreamHandle stream, int firstSlot, ReadOnlySpan<SlotEntry> slots)
        {
            RequireFrame(nameof(WriteSlots));
            UStream? s = Resolve(stream, nameof(WriteSlots));
            if (s == null) return;
            if (firstSlot < 0) { Violate($"WriteSlots 起点为负（{firstSlot}）"); return; }

            Ensure(ref s.Slots, firstSlot + slots.Length);
            for (int i = 0; i < slots.Length; i++) s.Slots[firstSlot + i] = slots[i];
            if (firstSlot + slots.Length > s.SlotCount) s.SlotCount = firstSlot + slots.Length;
            s.UniformsDirty = true;
            _uploadBytesThisFrame += slots.Length * 48;    // 3 vec4/槽（与 mock 同一账口径）
        }

        /// <inheritdoc/>
        public void SetSegments(StreamHandle stream, ReadOnlySpan<SegmentDesc> segments)
        {
            RequireFrame(nameof(SetSegments));
            UStream? s = Resolve(stream, nameof(SetSegments));
            if (s == null) return;
            s.Segments = segments.ToArray();
            s.SegTableDirty = true;
            _uploadBytesThisFrame += segments.Length * 32;
        }

        /// <inheritdoc/>
        public void SetRunOrders(StreamHandle stream, uint structEpoch, ReadOnlySpan<RunOrder> runs)
        {
            RequireFrame(nameof(SetRunOrders));
            UStream? s = Resolve(stream, nameof(SetRunOrders));
            if (s == null) return;
            _ = structEpoch;                               // 重推纪律由 mock 执法；这里只消费
            s.Runs = runs.ToArray();
            s.RunsDirty = true;
        }

        // ── 孤岛（M1-23 前只记账）────────────────────────────────────────────

        /// <inheritdoc/>
        public IslandHandle CreateIsland(StreamHandle stream, in IslandDesc desc)
        {
            _ = Resolve(stream, nameof(CreateIsland));
            var i = new IslandRecordSlot { Gen = 1, Alive = true, Desc = desc };
            _islands.Add(i);
            return new IslandHandle(_islands.Count, i.Gen);
        }

        /// <inheritdoc/>
        public void SyncIsland(IslandHandle island, in IslandSync sync)
        {
            if (island.IsNone || island.Index > _islands.Count) { Violate("SyncIsland 收到无效句柄"); return; }
            _islands[island.Index - 1].Sync = sync;
        }

        /// <inheritdoc/>
        public void DestroyIsland(IslandHandle island)
        {
            if (island.IsNone || island.Index > _islands.Count) { Violate("DestroyIsland 收到无效句柄"); return; }
            _islands[island.Index - 1].Alive = false;
        }

        // ── pass 与绘制 ─────────────────────────────────────────────────────

        /// <inheritdoc/>
        public PassHandle BeginOffscreenPass(in OffscreenPassDesc desc)
        {
            Violate($"离屏 pass（{desc.Kind}）在 M2-12 之前不支持：Caps.SupportsOffscreen = false");
            return PassHandle.None;
        }

        /// <inheritdoc/>
        public void EndOffscreenPass(PassHandle pass) => _ = pass;

        /// <inheritdoc/>
        public void BindMainSurface(in SurfaceDesc surface)
        {
            RequireFrame(nameof(BindMainSurface));
            if (_mainBound) Violate("主表面重复绑定（一帧只绑一次）");
            _mainBound = true;
            _ = surface;                                   // 主表面归宿主相机；后端不建 RT
        }

        /// <inheritdoc/>
        public void DrawStream(StreamHandle stream, PassHandle target)
        {
            RequireFrame(nameof(DrawStream));
            UStream? s = Resolve(stream, nameof(DrawStream));
            if (s == null) return;
            if (!target.IsMainSurface) { Violate("向离屏 pass 提交：本后端 M2-12 前不支持"); return; }
            if (!_mainBound) Violate("向主表面提交但主表面尚未绑定");
            if (!s.Root.activeSelf) s.Root.SetActive(true);
            _drawsThisFrame++;
        }

        /// <inheritdoc/>
        public void ReportDegrade(DegradeKind kind, string detail) =>
            Debug.LogWarning($"[FairyNext] 降级阶梯 {kind}: {detail}");

        /// <summary>销毁全部原生对象（编辑器验证脚本的收尾；不走 fence——调用方保证 GPU 已闲）。</summary>
        public void Dispose()
        {
            for (int i = 0; i < _streams.Count; i++)
            {
                if (_streams[i].Root != null) DestroyObject(_streams[i].Root);
                _streams[i].Alive = false;
            }
            for (int i = 0; i < _graveyard.Count; i++) DestroyObject(_graveyard[i].Go);
            _graveyard.Clear();
            _fencePending.Clear();
        }

        // ── 网格与材质（EndFrame 统一落地）──────────────────────────────────

        private void ApplyPending(UStream s)
        {
            if (s.SegTableDirty)
            {
                SyncSegmentNodes(s);
                for (int i = 0; i < s.Segments.Length; i++) RebuildSegmentMesh(s, i);
                s.SegTableDirty = false;
                s.RunsDirty = true;                        // 段换了，序要重刷到新 renderer 上
                s.DirtyMin = int.MaxValue;
                s.DirtyMax = -1;
            }
            else if (s.DirtyMax >= s.DirtyMin)
            {
                for (int i = 0; i < s.Segments.Length; i++)
                {
                    SegmentDesc seg = s.Segments[i];
                    if (seg.Count <= 0) continue;
                    if (seg.Start > s.DirtyMax || seg.Start + seg.Count - 1 < s.DirtyMin) continue;
                    RebuildSegmentMesh(s, i);
                }
                s.DirtyMin = int.MaxValue;
                s.DirtyMax = -1;
            }

            if (s.RunsDirty)
            {
                for (int i = 0; i < s.Segments.Length && i < s.Nodes.Count; i++)
                    s.Nodes[i].Renderer.sortingOrder = SortingOrderOf(s, i);
                s.RunsDirty = false;
            }

            if (s.UniformsDirty)
            {
                RebuildUniformArrays(s);
                for (int i = 0; i < s.Nodes.Count; i++) s.Nodes[i].Renderer.SetPropertyBlock(s.Props);
                s.UniformsDirty = false;
            }
        }

        /// <summary>段节点池与段表对齐：缺的建、多的熄（进坟场走 fence，不当场毁）。</summary>
        private void SyncSegmentNodes(UStream s)
        {
            while (s.Nodes.Count < s.Segments.Length)
            {
                var node = new SegmentNode();
                node.Go = new GameObject("seg" + s.Nodes.Count);
                node.Go.transform.SetParent(s.Root.transform, false);
                node.Mesh = new Mesh { name = "FairyNextSeg" };
                node.Mesh.MarkDynamic();
                node.Go.AddComponent<MeshFilter>().sharedMesh = node.Mesh;
                node.Renderer = node.Go.AddComponent<MeshRenderer>();
                node.Renderer.shadowCastingMode = ShadowCastingMode.Off;
                node.Renderer.receiveShadows = false;
                node.Renderer.lightProbeUsage = LightProbeUsage.Off;
                node.Material = new Material(_shader);
                node.Renderer.sharedMaterial = node.Material;
                s.Nodes.Add(node);
            }
            for (int i = 0; i < s.Nodes.Count; i++)
            {
                bool used = i < s.Segments.Length && s.Segments[i].Count > 0;
                s.Nodes[i].Go.SetActive(used);
                if (!used) continue;

                SegmentDesc seg = s.Segments[i];
                Material m = s.Nodes[i].Material;
                m.SetTexture("_FnTex0", TextureOf(seg.TexAt(0)));
                m.SetTexture("_FnTex1", TextureOf(seg.TexAt(1)));
                m.SetTexture("_FnTex2", TextureOf(seg.TexAt(2)));
                m.SetTexture("_FnTex3", TextureOf(seg.TexAt(3)));
                SetBlend(m, seg.Blend);
                s.Nodes[i].Renderer.SetPropertyBlock(s.Props);
            }
        }

        private void RebuildSegmentMesh(UStream s, int segIndex)
        {
            if (segIndex >= s.Nodes.Count) return;         // SegTableDirty 路径先 SyncSegmentNodes
            SegmentDesc seg = s.Segments[segIndex];
            Mesh mesh = s.Nodes[segIndex].Mesh;
            int quadCount = seg.Count;
            if (quadCount <= 0) { mesh.Clear(); return; }

            var verts = new StreamVertex[quadCount * 4];
            var indices = new int[quadCount * 6];
            for (int q = 0; q < quadCount; q++)
            {
                QuadInstance quad = s.Quads[seg.Start + q];
                int v = q * 4;
                // 角序：TL(0,0) TR(1,0) BL(0,1) BR(1,1)；UvA = 上两角、UvB = 下两角（按角归位）。
                WriteVertex(ref verts[v + 0], in quad, 0f, 0f, quad.UvA.x, quad.UvA.y);
                WriteVertex(ref verts[v + 1], in quad, 1f, 0f, quad.UvA.z, quad.UvA.w);
                WriteVertex(ref verts[v + 2], in quad, 0f, 1f, quad.UvB.x, quad.UvB.y);
                WriteVertex(ref verts[v + 3], in quad, 1f, 1f, quad.UvB.z, quad.UvB.w);
                int t = q * 6;
                indices[t + 0] = v + 0; indices[t + 1] = v + 1; indices[t + 2] = v + 2;
                indices[t + 3] = v + 2; indices[t + 4] = v + 1; indices[t + 5] = v + 3;
            }

            mesh.Clear();
            mesh.SetVertexBufferParams(verts.Length, VertexLayout);
            mesh.SetVertexBufferData(verts, 0, 0, verts.Length, 0, MeshUpdateFlags.DontValidateIndices);
            mesh.SetIndexBufferParams(indices.Length, IndexFormat.UInt32);
            mesh.SetIndexBufferData(indices, 0, 0, indices.Length, MeshUpdateFlags.DontValidateIndices);
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, indices.Length), MeshUpdateFlags.DontRecalculateBounds);
            // 骑槽的叶顶点是槽本地坐标、槽变换在 shader 里应用：网格包围盒不知情，给大盒防误剔除。
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(1e5f, 1e5f, 1f));
        }

        private static void WriteVertex(ref StreamVertex v, in QuadInstance q, float cx, float cy, float u, float uvY)
        {
            v.Px = q.Rect.x + cx * q.Rect.z;
            v.Py = q.Rect.y + cy * q.Rect.w;
            v.Pz = 0f;
            v.U = u;
            v.V = uvY;
            v.Color = q.Color;
            v.Route = q.Route;
            v.Flags = q.Flags;
            v.Aux = q.Aux;
            v.Ex = q.Extra.x; v.Ey = q.Extra.y; v.Ez = q.Extra.z; v.Ew = q.Extra.w;
            v.Cx = cx;
            v.Cy = cy;
        }

        private void RebuildUniformArrays(UStream s)
        {
            int slots = Math.Min(s.SlotCount, Abi.TransformSlotBudget);
            if (s.SlotCount > Abi.TransformSlotBudget)
                Violate($"槽数 {s.SlotCount} 超预算 {Abi.TransformSlotBudget}（上游该走槽荒阶梯）");
            for (int i = 0; i < slots; i++)
            {
                var m = s.Slots[i].M;
                s.SlotArray[i * 2] = new Vector4(m.m00, m.m01, m.m10, m.m11);
                s.SlotArray[i * 2 + 1] = new Vector4(m.tx, m.ty, 0f, 0f);
                if (m.TryInvert(out var inv))
                {
                    s.SlotInvArray[i * 2] = new Vector4(inv.m00, inv.m01, inv.m10, inv.m11);
                    s.SlotInvArray[i * 2 + 1] = new Vector4(inv.tx, inv.ty, 0f, 0f);
                }
                else
                {
                    s.SlotInvArray[i * 2] = Vector4.zero;      // 奇异槽（隐藏节点 scale 0）：clip 求值域收敛到原点
                    s.SlotInvArray[i * 2 + 1] = Vector4.zero;
                }
            }

            int clips = Math.Min(s.ClipCount, Abi.ClipEntryBudget);
            if (s.ClipCount > Abi.ClipEntryBudget)
                Violate($"裁剪条目 {s.ClipCount} 超预算 {Abi.ClipEntryBudget}（上游该走 ClipStarvation 阶梯）");
            for (int i = 0; i < clips; i++)
            {
                ClipEntry e = s.Clips[i];
                s.ClipArray[i * 3] = new Vector4(e.Rect.x, e.Rect.y, e.Rect.z, e.Rect.w);
                s.ClipArray[i * 3 + 1] = new Vector4(e.Soft.x, e.Soft.y, e.Slot, 0f);
                s.ClipArray[i * 3 + 2] = new Vector4(e.Radii.x, e.Radii.y, e.Radii.z, e.Radii.w);
            }

            s.Props.SetVectorArray("_FnSlots", s.SlotArray);
            s.Props.SetVectorArray("_FnSlotInv", s.SlotInvArray);
            s.Props.SetVectorArray("_FnClips", s.ClipArray);
        }

        private int SortingOrderOf(UStream s, int segIndex)
        {
            SegmentDesc seg = s.Segments[segIndex];
            int baseOrder = 0;
            for (int i = 0; i < s.Runs.Length; i++)
            {
                if (s.Runs[i].RunIndex == seg.RunIndex) { baseOrder = s.Runs[i].SortingOrder; break; }
            }
            int intra = 0;
            for (int i = 0; i < segIndex; i++)
                if (s.Segments[i].RunIndex == seg.RunIndex) intra++;
            long order = (long)baseOrder + intra;
            if (order > short.MaxValue)
            {
                order = short.MaxValue;
                if (!_sortingClampReported)
                {
                    _sortingClampReported = true;
                    Violate($"sortingOrder {baseOrder + intra} 超 Unity int16 语义上限被夹紧（巨树；WebGL2 后端无此上限，M3-03）");
                }
            }
            return (int)order;
        }

        private Texture TextureOf(TexId id)
        {
            if (id.IsNone) return Texture2D.whiteTexture;      // 纯色也占槽：绑 1×1 白（机制注释同款）
            if (_textures.TryGetValue(id.Value, out Texture t) && t != null) return t;
            Violate($"TexId#{id.Value} 未登记纹理——绑 1×1 白顶位（画错比不画难查，但缺图必须有账）");
            _textures[id.Value] = Texture2D.whiteTexture;      // 只报一次
            return Texture2D.whiteTexture;
        }

        private static void SetBlend(Material m, BlendClass blend)
        {
            BlendMode src, dst;
            switch (blend)
            {
                case BlendClass.Add: src = BlendMode.SrcAlpha; dst = BlendMode.One; break;
                case BlendClass.Multiply: src = BlendMode.DstColor; dst = BlendMode.OneMinusSrcAlpha; break;
                case BlendClass.Screen: src = BlendMode.One; dst = BlendMode.OneMinusSrcColor; break;
                case BlendClass.Erase: src = BlendMode.Zero; dst = BlendMode.OneMinusSrcAlpha; break;
                default: src = BlendMode.SrcAlpha; dst = BlendMode.OneMinusSrcAlpha; break;
            }
            m.SetFloat("_SrcBlend", (float)src);
            m.SetFloat("_DstBlend", (float)dst);
        }

        // ── 内部 ────────────────────────────────────────────────────────────

        private UStream? Resolve(StreamHandle handle, string op)
        {
            if (handle.IsNone || handle.Index > _streams.Count)
            {
                Violate($"{op} 收到无效流句柄 {handle}");
                return null;
            }
            UStream s = _streams[handle.Index - 1];
            if (s.Gen != handle.Gen || !s.Alive)
            {
                Violate($"{op} 于已销毁/陈旧的流 {handle}（fence 未到期，GPU 可能仍在读）");
                return null;
            }
            return s;
        }

        private void RequireFrame(string op)
        {
            if (!_inFrame) Violate($"{op} 于帧括号外（BeginFrame/EndFrame 之间才准提交）");
        }

        private void Violate(string message)
        {
            _violations.Add(message);
            Debug.LogError("[FairyNext] 后端协议违约：" + message);
        }

        private static void DestroyObject(GameObject go)
        {
            if (go == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(go);
            else UnityEngine.Object.DestroyImmediate(go);
        }

        private static void Ensure<T>(ref T[] array, int n)
        {
            if (array.Length >= n) return;
            int cap = array.Length == 0 ? Math.Max(16, n) : array.Length;
            while (cap < n) cap *= 2;
            Array.Resize(ref array, cap);
        }
    }
}
