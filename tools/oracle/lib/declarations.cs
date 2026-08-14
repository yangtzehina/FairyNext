// FairyNext oracle driver — 注入 `unicli eval --declarations` 的类型与执行体。
// 运行上下文：Unity 编辑器进程（Play Mode）内的临时程序集，可访问 UnityEngine / UnityEditor / FairyGUI。
// 只驱动 oracle（~/ECS/FairyGUI-unity @ d1a9d7d），不写 oracle 仓库内任何文件——产物全部落在 FairyNext 侧的 outDir。
//
// 三条确定性规则（否则 golden 会随编辑器窗口尺寸漂移）：
//   1. stage 尺寸由 Stage.HandleScreenSizeChanged(W,H,0.02) 强制钉死，不读 Screen.width/height；
//   2. 截帧不走 Game View（Screenshot.Capture 的输出尺寸取决于窗口大小与 Aspect 下拉框），自建正交相机 + RenderTexture；
//   3. 相机几何精确复刻 StageCamera：orthographicSize = H/2*upp，position = (orthoSize*W/H, -orthoSize, 0)，
//      near/far = -30/30（见 fork Assets/Scripts/Core/StageCamera.cs @ d1a9d7d）。
//
// 为什么自带 JSON 读取器而不用 UnityEngine.JsonUtility：eval 程序集由 Assembly.Load(byte[]) 载入、
// Assembly.Location 为空，Unity 原生序列化器解析不了其中的**嵌套自定义类字段**——实测 FromJson 顶层字段
// 能填、`OracleNodeDesc[] nodes` 恒为 null。也不能引用 FairyNext 的 OracleCompare 程序集：那要求把 DLL
// 拷进 oracle 工程的 Assets/，违反 oracle 只读纪律。故此处的 60 行读取器是有意的第二份实现。

public class OJson
{
    public int Kind;                                            // 0=null 1=bool 2=num 3=str 4=arr 5=obj
    public bool B; public double N; public string S;
    public System.Collections.Generic.List<OJson> A;
    public System.Collections.Generic.Dictionary<string, OJson> O;

    public OJson Get(string k) { OJson v; return O != null && O.TryGetValue(k, out v) ? v : null; }
    public string Str(string k, string d) { var v = Get(k); return v != null && v.Kind == 3 ? v.S : d; }
    public float Num(string k, float d) { var v = Get(k); return v != null && v.Kind == 2 ? (float)v.N : d; }
    public int Int(string k, int d) { var v = Get(k); return v != null && v.Kind == 2 ? (int)v.N : d; }
    public System.Collections.Generic.List<OJson> Arr(string k)
    {
        var v = Get(k);
        return v != null && v.Kind == 4 ? v.A : new System.Collections.Generic.List<OJson>();
    }

    public static OJson Parse(string s) { int i = 0; var v = P(s, ref i); Ws(s, ref i); return v; }

    static void Ws(string s, ref int i) { while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++; }

    static OJson P(string s, ref int i)
    {
        Ws(s, ref i);
        if (i >= s.Length) throw new System.Exception("oracle scene json: unexpected end");
        char c = s[i];
        if (c == '{')
        {
            i++; var o = new OJson { Kind = 5, O = new System.Collections.Generic.Dictionary<string, OJson>() };
            Ws(s, ref i);
            if (s[i] == '}') { i++; return o; }
            while (true)
            {
                Ws(s, ref i);
                string key = PStr(s, ref i);
                Ws(s, ref i);
                if (s[i] != ':') throw new System.Exception("oracle scene json: expected ':' at " + i);
                i++;
                o.O[key] = P(s, ref i);
                Ws(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return o; }
                throw new System.Exception("oracle scene json: expected ',' or '}' at " + i);
            }
        }
        if (c == '[')
        {
            i++; var a = new OJson { Kind = 4, A = new System.Collections.Generic.List<OJson>() };
            Ws(s, ref i);
            if (s[i] == ']') { i++; return a; }
            while (true)
            {
                a.A.Add(P(s, ref i));
                Ws(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return a; }
                throw new System.Exception("oracle scene json: expected ',' or ']' at " + i);
            }
        }
        if (c == '"') return new OJson { Kind = 3, S = PStr(s, ref i) };
        if (s.Length - i >= 4 && s.Substring(i, 4) == "true") { i += 4; return new OJson { Kind = 1, B = true }; }
        if (s.Length - i >= 5 && s.Substring(i, 5) == "false") { i += 5; return new OJson { Kind = 1, B = false }; }
        if (s.Length - i >= 4 && s.Substring(i, 4) == "null") { i += 4; return new OJson { Kind = 0 }; }
        int st = i;
        while (i < s.Length && "+-.eE0123456789".IndexOf(s[i]) >= 0) i++;
        return new OJson { Kind = 2, N = double.Parse(s.Substring(st, i - st), System.Globalization.CultureInfo.InvariantCulture) };
    }

    static string PStr(string s, ref int i)
    {
        if (s[i] != '"') throw new System.Exception("oracle scene json: expected string at " + i);
        i++;
        var sb = new System.Text.StringBuilder();
        while (s[i] != '"')
        {
            if (s[i] == '\\')
            {
                i++;
                char e = s[i++];
                if (e == 'n') sb.Append('\n');
                else if (e == 't') sb.Append('\t');
                else if (e == 'r') sb.Append('\r');
                else if (e == 'u') { sb.Append((char)System.Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; }
                else sb.Append(e);
            }
            else sb.Append(s[i++]);
        }
        i++;
        return sb.ToString();
    }
}

public static class FairyNextOracle
{
    public const float UnitsPerPixel = 0.02f;   // = StageCamera.DefaultUnitsPerPixel

    public static string Run(string sceneFile, string outDir, string oracleSha, string driver)
    {
        if (!UnityEngine.Application.isPlaying) return "ERROR not-in-play-mode";

        var desc = OJson.Parse(System.IO.File.ReadAllText(sceneFile));
        var nodes = desc.Arr("nodes");
        if (nodes.Count == 0) return "ERROR scene-has-no-nodes " + sceneFile;
        var tol = desc.Get("tolerance");
        if (tol == null) return "ERROR scene-missing-tolerance-block " + sceneFile;

        string id = desc.Str("id", "unnamed");
        int sw = desc.Int("stageWidth", 640), sh = desc.Int("stageHeight", 360);
        System.IO.Directory.CreateDirectory(outDir);

        foreach (var p in desc.Arr("packages"))
        {
            var shortName = p.S.Substring(p.S.LastIndexOf('/') + 1);
            if (FairyGUI.UIPackage.GetByName(shortName) == null) FairyGUI.UIPackage.AddPackage(p.S);
        }

        var root = FairyGUI.GRoot.inst;                 // 首次访问即建 Stage + StageCamera
        PinStageSize(sw, sh);
        root.RemoveChildren(0, -1, true);
        BuildScene(root, nodes);
        PumpStage(2);                                   // flush 显示列表成网格，不依赖编辑器是否在前台走帧

        int pngBytes = Capture(sw, sh, Col(desc.Get("background"), UnityEngine.Color.black),
                               System.IO.Path.Combine(outDir, "frame.png"));
        Write(System.IO.Path.Combine(outDir, "layout.json"), DumpLayout(root, id, sw, sh));
        Write(System.IO.Path.Combine(outDir, "meta.json"), DumpMeta(id, sw, sh, pngBytes, oracleSha, driver, tol));

        return "OK nodes=" + nodes.Count + " png=" + pngBytes + " stage=" + sw + "x" + sh
             + " colorSpace=" + UnityEngine.QualitySettings.activeColorSpace
             + " unity=" + UnityEngine.Application.unityVersion;
    }

    static void Write(string path, string text)
    {
        System.IO.File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
    }

    /// <summary>
    /// meta 是 golden 的**受理条件**：换了 oracle SHA / Unity 版本 / 色彩空间 / 图形 API，
    /// 像素基线就不再可比——这些字段缺一，比对器拒收（而不是默默按默认值比）。
    /// 容差参数不在 C# 侧写死默认：唯一事实源是场景描述符的 tolerance 块，经由 meta 传给比对器。
    /// </summary>
    static string DumpMeta(string id, int sw, int sh, int pngBytes, string oracleSha, string driver, OJson tol)
    {
        var sb = new System.Text.StringBuilder(1024);
        sb.Append("{\n");
        sb.Append("  \"scene\": \"").Append(id).Append("\",\n");
        sb.Append("  \"oracleSha\": \"").Append(oracleSha).Append("\",\n");
        sb.Append("  \"unityVersion\": \"").Append(UnityEngine.Application.unityVersion).Append("\",\n");
        sb.Append("  \"colorSpace\": \"").Append(UnityEngine.QualitySettings.activeColorSpace).Append("\",\n");
        sb.Append("  \"graphicsDevice\": \"").Append(UnityEngine.SystemInfo.graphicsDeviceType).Append("\",\n");
        sb.Append("  \"driver\": \"").Append(driver).Append("\",\n");
        sb.Append("  \"capturedUtc\": \"")
          .Append(System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture))
          .Append("\",\n");
        sb.Append("  \"image\": { \"width\": ").Append(sw).Append(", \"height\": ").Append(sh)
          .Append(", \"bytes\": ").Append(pngBytes).Append(" },\n");
        sb.Append("  \"tolerance\": {\n");
        sb.Append("    \"layoutEpsPx\": ").Append(F(tol.Num("layoutEpsPx", -1f))).Append(",\n");
        sb.Append("    \"layoutEpsUnitless\": ").Append(F(tol.Num("layoutEpsUnitless", -1f))).Append(",\n");
        sb.Append("    \"layoutEpsDegrees\": ").Append(F(tol.Num("layoutEpsDegrees", -1f))).Append(",\n");
        sb.Append("    \"pixelChannelDelta\": ").Append(tol.Int("pixelChannelDelta", -1)).Append(",\n");
        sb.Append("    \"pixelMaxChannelDelta\": ").Append(tol.Int("pixelMaxChannelDelta", -1)).Append(",\n");
        sb.Append("    \"pixelDiffRatio\": ").Append(F(tol.Num("pixelDiffRatio", -1f))).Append(",\n");
        sb.Append("    \"hotspotCell\": ").Append(tol.Int("hotspotCell", -1)).Append("\n");
        sb.Append("  }\n}\n");
        return sb.ToString();
    }

    static UnityEngine.Color Col(OJson j, UnityEngine.Color fallback)
    {
        if (j == null || j.Kind != 5) return fallback;
        return new UnityEngine.Color(j.Num("r", 0f), j.Num("g", 0f), j.Num("b", 0f), j.Num("a", 1f));
    }

    // ---- 场景构建 ----------------------------------------------------------

    static void BuildScene(FairyGUI.GComponent root, System.Collections.Generic.List<OJson> nodes)
    {
        foreach (var n in nodes)
        {
            string kind = n.Str("kind", "");
            float w = n.Num("width", 0f), h = n.Num("height", 0f);
            FairyGUI.GObject obj;
            if (kind == "graph")
            {
                var g = new FairyGUI.GGraph();
                g.DrawRect(w, h, n.Int("lineSize", 0),
                           Col(n.Get("line"), UnityEngine.Color.clear),
                           Col(n.Get("fill"), UnityEngine.Color.white));
                obj = g;
            }
            else if (kind == "packageItem")
            {
                obj = FairyGUI.UIPackage.CreateObject(n.Str("pkg", ""), n.Str("item", ""));
                if (obj == null) throw new System.Exception("package item not found: " + n.Str("pkg", "") + "/" + n.Str("item", ""));
            }
            else throw new System.Exception("unknown node kind: " + kind);

            obj.name = n.Str("name", "");
            obj.SetXY(n.Num("x", 0f), n.Num("y", 0f));
            obj.SetSize(w, h);
            root.AddChild(obj);
        }
    }

    static void PinStageSize(int w, int h)
    {
        // HandleScreenSizeChanged 是 internal：反射调用。它同时设 contentRect 与 stage 容器 localScale，
        // 是「屏幕尺寸 → FairyGUI 世界」的唯一入口，绕过它就得在此复刻两处换算。
        var m = typeof(FairyGUI.Stage).GetMethod("HandleScreenSizeChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (m == null) throw new System.Exception("FairyGUI.Stage.HandleScreenSizeChanged missing — oracle SHA drifted?");
        m.Invoke(FairyGUI.Stage.inst, new object[] { w, h, UnitsPerPixel });
    }

    static void PumpStage(int times)
    {
        var m = typeof(FairyGUI.Stage).GetMethod("InternalUpdate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (m == null) throw new System.Exception("FairyGUI.Stage.InternalUpdate missing — oracle SHA drifted?");
        for (int i = 0; i < times; i++) m.Invoke(FairyGUI.Stage.inst, null);
    }

    // ---- 截帧 --------------------------------------------------------------

    static int Capture(int w, int h, UnityEngine.Color background, string pngPath)
    {
        float orthoSize = h / 2f * UnitsPerPixel;
        var go = new UnityEngine.GameObject("FairyNextOracleCapture");
        var rt = UnityEngine.RenderTexture.GetTemporary(w, h, 24,
            UnityEngine.RenderTextureFormat.ARGB32, UnityEngine.RenderTextureReadWrite.Default);
        var tex = new UnityEngine.Texture2D(w, h, UnityEngine.TextureFormat.RGBA32, false);
        var prevActive = UnityEngine.RenderTexture.active;
        try
        {
            var cam = go.AddComponent<UnityEngine.Camera>();
            cam.orthographic = true;
            cam.orthographicSize = orthoSize;
            cam.nearClipPlane = -30f;
            cam.farClipPlane = 30f;
            cam.cullingMask = 1 << FairyGUI.Stage.inst.gameObject.layer;
            cam.clearFlags = UnityEngine.CameraClearFlags.SolidColor;
            cam.backgroundColor = background;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.useOcclusionCulling = false;
            cam.stereoTargetEye = UnityEngine.StereoTargetEyeMask.None;
            go.transform.position = new UnityEngine.Vector3(orthoSize * w / (float)h, -orthoSize, 0f);

            cam.targetTexture = rt;
            cam.Render();
            UnityEngine.RenderTexture.active = rt;
            tex.ReadPixels(new UnityEngine.Rect(0, 0, w, h), 0, 0, false);
            tex.Apply(false);

            var png = UnityEngine.ImageConversion.EncodeToPNG(tex);
            System.IO.File.WriteAllBytes(pngPath, png);
            return png.Length;
        }
        finally
        {
            UnityEngine.RenderTexture.active = prevActive;
            UnityEngine.RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    // ---- 布局导出 ----------------------------------------------------------

    static string F(float v)
    {
        // 固定小数位而非 "R"：golden 是要被人 review 的文本，指数记法与 17 位尾数只制造噪声 diff。
        // 6 位小数远细于 0.5px 容差，不损失判定力。
        return v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
    }

    static string DumpLayout(FairyGUI.GComponent root, string id, int sw, int sh)
    {
        var sb = new System.Text.StringBuilder(4096);
        sb.Append("{\n");
        sb.Append("  \"scene\": \"").Append(id).Append("\",\n");
        sb.Append("  \"stage\": { \"width\": ").Append(sw).Append(", \"height\": ").Append(sh)
          .Append(", \"unitsPerPixel\": ").Append(F(UnitsPerPixel)).Append(" },\n");
        sb.Append("  \"nodes\": [\n");
        int count = 0;
        Walk(root, "", sb, ref count);
        if (count > 0) sb.Length -= 2;               // 去掉最后一条的 ",\n"
        sb.Append("\n  ]\n}\n");
        return sb.ToString();
    }

    static void Walk(FairyGUI.GComponent parent, string prefix, System.Text.StringBuilder sb, ref int count)
    {
        for (int i = 0; i < parent.numChildren; i++)
        {
            var c = parent.GetChildAt(i);
            var path = prefix + "/" + (string.IsNullOrEmpty(c.name) ? "#" + i : c.name);
            sb.Append("    { \"path\": \"").Append(path).Append("\"")
              .Append(", \"name\": \"").Append(c.name).Append("\"")
              .Append(", \"type\": \"").Append(c.GetType().Name).Append("\"")
              .Append(", \"x\": ").Append(F(c.x))
              .Append(", \"y\": ").Append(F(c.y))
              .Append(", \"width\": ").Append(F(c.width))
              .Append(", \"height\": ").Append(F(c.height))
              .Append(", \"scaleX\": ").Append(F(c.scaleX))
              .Append(", \"scaleY\": ").Append(F(c.scaleY))
              .Append(", \"rotation\": ").Append(F(c.rotation))
              .Append(", \"alpha\": ").Append(F(c.alpha))
              .Append(", \"visible\": ").Append(c.visible ? "true" : "false")
              .Append(" },\n");
            count++;
            var comp = c as FairyGUI.GComponent;
            if (comp != null) Walk(comp, path, sb, ref count);
        }
    }
}
