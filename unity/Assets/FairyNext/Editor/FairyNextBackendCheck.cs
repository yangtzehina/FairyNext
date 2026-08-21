using System.IO;
using System.Text;
using FairyNext.Core;
using FairyNext.Core.Layout;
using FairyNext.Core.Rendering;
using UnityEditor;
using UnityEngine;

namespace FairyNext.UnityBackend.Editor
{
    /// <summary>
    /// Unity 顶点流后端的最小验收（M1-15「尽力级」）：建树 → 接管线 → Tick 两帧 → 断言
    /// 后端零违约 + 镜像对拍全等（<see cref="UnityVertexStreamBackend.ValidateMirror"/>）→
    /// 相机渲到 RT 截帧非空 → 增量帧再对拍。判定行 <c>FAIRYNEXT BACKEND CHECK: PASS|FAIL</c>
    /// 写 Console 与 <c>Logs/fairynext-backend-check.txt</c>（Temp/ 会在退出时被清空，产物一律 Logs/）。
    ///
    /// 跑法：菜单 Tools/FairyNext/Backend Check，或无头
    /// <c>Unity -batchmode -projectPath unity -executeMethod FairyNext.UnityBackend.Editor.FairyNextBackendCheck.Run</c>
    /// （渲染截帧需要图形设备：**不要**传 -nographics；batchmode 下按结果 Exit 0/1）。
    /// </summary>
    public static class FairyNextBackendCheck
    {
        [MenuItem("Tools/FairyNext/Backend Check")]
        public static void RunFromMenu() => Execute(exitAfter: false);

        public static void Run() => Execute(exitAfter: Application.isBatchMode);

        private static void Execute(bool exitAfter)
        {
            var log = new StringBuilder();
            bool pass = true;
            void Check(string name, bool ok)
            {
                pass &= ok;
                log.AppendLine((ok ? "PASS " : "FAIL ") + name);
            }

            UnityVertexStreamBackend backend = null;
            GameObject camGo = null;
            RenderTexture rt = null;
            try
            {
                var table = new NodeTable(tree: 1);
                var inval = new Invalidation(table);
                var kernel = new UiKernel(table, inval);
                var layout = new LayoutEngine(kernel);
                layout.Attach();
                var content = new ContentTable();
                var stream = new RenderStream("unity-check");
                backend = new UnityVertexStreamBackend();
                var pipe = new RenderPipeline(kernel, stream, content, backend);
                pipe.Attach();
                pipe.Surface = new SurfaceDesc { Width = 256, Height = 256, ClearColor = 0u };

                // 叶 1：纯白实心；叶 2：红色 + Radial360 顶起 65%（shader 的 FN_RADIAL_FILL_* 求值路径）。
                NodeHandle Leaf(in LeafSpec spec, float x, float y, float w, float h)
                {
                    NodeHandle n = table.CreateNode(NodeType.Image);
                    table.SetContentRef(n, content.AddLeaf(in spec));
                    table.SetPosition(n, x, y);
                    table.SetSize(n, w, h);
                    table.AddChild(table.Root, n);
                    return n;
                }
                LeafSpec white = LeafSpec.Solid(0xFFFFFFFFu);
                LeafSpec radial = LeafSpec.Solid(0xFF0000FFu);       // RGBA8：R=255 A=255
                radial.Fill = RadialFillParams.Of(FillMethod.Radial360, (byte)Origin360.Top, 0.65f);
                NodeHandle leaf1 = Leaf(in white, 20f, 20f, 60f, 60f);
                Leaf(in radial, 120f, 20f, 60f, 60f);

                FrameTime time = FrameTime.First(0.016f, 0.016f);
                void Tick() { kernel.Tick(in time); time = time.Step(0.016f, 0.016f); }
                Tick();
                Tick();

                Check("两帧走完：ticks=2、presents>=1", pipe.Ticks == 2 && pipe.Presents >= 1);
                Check("流建成：2 叶 2 quad、段>=1", stream.QuadCount == 2 && stream.SegmentCount >= 1);
                string mirror = backend.ValidateMirror(stream);
                Check("上传字节对拍（全量帧）：" + (mirror ?? "全等"), mirror == null);
                Check("后端零协议违约", backend.Violations.Count == 0);

                // 截帧：流根 y 翻转（唯一点）把 y-down 内容放到世界 y<0 半平面。
                camGo = new GameObject("FairyNextCheckCamera");
                var cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = 128f;
                cam.aspect = 1f;
                cam.transform.position = new Vector3(128f, -128f, -10f);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                rt = new RenderTexture(256, 256, 24);
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var shot = new Texture2D(256, 256, TextureFormat.RGBA32, false);
                shot.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
                shot.Apply();
                RenderTexture.active = null;
                Color32[] px = shot.GetPixels32();
                int lit = 0;
                foreach (Color32 p in px) if (p.r > 8 || p.g > 8 || p.b > 8) lit++;
                Check($"截帧非空（{lit} 亮像素 / {px.Length}）", lit > 100);
                Directory.CreateDirectory("Logs");
                File.WriteAllBytes(Path.Combine("Logs", "fairynext-backend-check.png"), shot.EncodeToPNG());
                Object.DestroyImmediate(shot);

                // 增量帧：α 半透 → Color 通道原位重写 → 对拍仍全等。
                table.SetAlpha(leaf1, 0.5f);
                Tick();
                string mirror2 = backend.ValidateMirror(stream);
                Check("上传字节对拍（增量帧）：" + (mirror2 ?? "全等"), mirror2 == null);
                Check("增量帧后仍零违约", backend.Violations.Count == 0);

                pipe.Detach();
                kernel.Detach();
            }
            catch (System.Exception e)
            {
                pass = false;
                log.AppendLine("EXCEPTION " + e);
            }
            finally
            {
                if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
                if (camGo != null) Object.DestroyImmediate(camGo);
                backend?.Dispose();
            }

            string verdict = "FAIRYNEXT BACKEND CHECK: " + (pass ? "PASS" : "FAIL");
            log.AppendLine(verdict);
            Directory.CreateDirectory("Logs");
            File.WriteAllText(Path.Combine("Logs", "fairynext-backend-check.txt"), log.ToString());
            Debug.Log(log.ToString());
            if (exitAfter) EditorApplication.Exit(pass ? 0 : 1);
        }
    }
}
