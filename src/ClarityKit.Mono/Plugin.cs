using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ClarityKit.Shared;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClarityKit.Mono
{
    /// <summary>
    /// ClarityKit 通用去码引擎（Mono / BepInEx 5 版）。
    ///
    /// 关键教训：本环境下直接用 BaseUnityPlugin 自身的 Unity 消息（Update/LateUpdate/渲染事件）
    /// 全都不触发——只有 Awake 跑。故改为新建独立 GameObject 挂运行器 ClarityRunner。
    /// Runner 用 LateUpdate 驱动扫描、渲染前回调做压制。
    ///
    /// v0.5.0：补上"够不着后期材质"的结构性盲区。原先只扫 Renderer.sharedMaterials，
    /// 对 HDRP/URP 的「全屏后期型」马赛克无效——这类效果的材质挂在 CustomPassVolume 的全屏 pass 上、
    /// 不绑定任何 Renderer，且常常没有 Shader.Find 入口 / 没有可供 Harmony patch 的控制器类。新增：
    ///   - 策略 B+（ScanAllMaterials）：扫描 Resources.FindObjectsOfTypeAll&lt;Material&gt;()（含无 Renderer 的材质）
    ///   - 策略 B2（HideMosaicRenderers）：着色器名命中关键词的 Renderer 直接 enabled=false（贴片网格型）
    ///   - 策略 D （DisableCustomPasses）：反射禁用使用马赛克材质的 CustomPassVolume 全屏 pass（不硬依赖 HDRP）
    ///   - 诊断升级：dump 全部 shader/material/CustomPass，并对命中材质逐属性导出名值
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    public class ClarityPlugin : BaseUnityPlugin
    {
        public const string GUID = "com.clarity.kit.mono";
        public const string NAME = "ClarityKit (Mono)";
        public const string VERSION = "0.5.0";

        internal static ManualLogSource Log;
        internal static Settings Cfg;

        private void Awake()
        {
            Log = Logger;
            try
            {
                Cfg = new Settings(Config);

                GameObject host = new GameObject("ClarityKitRunner");
                DontDestroyOnLoad(host);
                host.hideFlags = HideFlags.HideAndDontSave;
                host.AddComponent<ClarityRunner>();

                Log.LogInfo(NAME + " v" + VERSION + " loaded, runner attached. keywords=[" +
                            string.Join(",", Cfg.Keywords) + "]");
            }
            catch (Exception e)
            {
                if (Log != null) Log.LogError("Awake failed: " + e);
            }
        }
    }

    /// <summary>配置载体。</summary>
    internal class Settings
    {
        public readonly ConfigEntry<bool> HideByName;
        public readonly ConfigEntry<bool> PatchShader;
        public readonly ConfigEntry<bool> ScanAllMaterials;
        public readonly ConfigEntry<bool> HideMosaicRenderers;
        public readonly ConfigEntry<bool> DisableCustomPasses;
        public readonly ConfigEntry<bool> Diagnostic;
        public readonly ConfigEntry<float> RescanInterval;
        public readonly string[] Keywords;
        public readonly string[] PixelParams;
        public readonly string[] Deny;

        public Settings(ConfigFile config)
        {
            HideByName = config.Bind("Strategies", "HideByName", true, "策略A：隐藏名字含马赛克关键词的物体");
            PatchShader = config.Bind("Strategies", "PatchShader", true, "策略B：把命中关键词的材质的像素化参数清零");
            ScanAllMaterials = config.Bind("Strategies", "ScanAllMaterials", true, "策略B+：扫描全部已加载材质（含后期/CustomPass 持有、不挂 Renderer 的材质）");
            HideMosaicRenderers = config.Bind("Strategies", "HideMosaicRenderers", false, "策略B2：着色器名命中关键词的 Renderer 直接关闭（默认关——这类网格会随游戏运行累积，逐帧重压制会拖慢；仅在共享材质清零无效的贴片网格场景手动开）");
            DisableCustomPasses = config.Bind("Strategies", "DisableCustomPasses", true, "策略D：反射禁用使用马赛克材质的 HDRP/URP CustomPassVolume 全屏 pass");
            Diagnostic = config.Bind("Diagnostics", "DumpScene", false, "导出场景内 shader/材质/Renderer/CustomPass 清单到 BepInEx/ClarityKit_dump.txt");
            RescanInterval = config.Bind("Performance", "RescanIntervalSeconds", 2f, "全场景重扫间隔（秒）；<=0 视为 2");
            ConfigEntry<string> kw = config.Bind("Keywords", "MosaicKeywords", string.Join(",", Shared.Keywords.DefaultMosaicKeywords), "马赛克关键词（逗号分隔，匹配物体名/材质名/着色器名）");
            ConfigEntry<string> pp = config.Bind("Keywords", "PixelationParams", string.Join(",", Shared.Keywords.DefaultPixelationParams), "着色器像素化参数名（逗号分隔）");
            ConfigEntry<string> dn = config.Bind("Keywords", "DenyKeywords", string.Join(",", Shared.Keywords.DefaultDenyKeywords), "黑名单关键词（逗号分隔），命中则跳过");
            Keywords = Shared.Keywords.SplitCsv(kw.Value);
            PixelParams = Shared.Keywords.SplitCsv(pp.Value);
            Deny = Shared.Keywords.SplitCsv(dn.Value);
        }
    }

    /// <summary>独立运行器：LateUpdate 驱动扫描，渲染前回调做压制。</summary>
    internal class ClarityRunner : MonoBehaviour
    {
        private readonly HashSet<GameObject> _hidden = new HashSet<GameObject>();
        private readonly HashSet<Renderer> _hiddenRenderers = new HashSet<Renderer>();
        private readonly HashSet<Material> _patched = new HashSet<Material>();
        private readonly List<object> _disabledPasses = new List<object>();
        private float _lastScan = -999f;
        private bool _renderSeen;
        private bool _lateSeen;

        // HDRP/URP CustomPass 反射句柄（只解析一次；不存在则保持 null，引擎照常工作）
        private Type _tVolume;
        private FieldInfo _fCustomPasses;
        private bool _hdrpResolved;

        private void Start()
        {
            Camera.onPreCull += OnRenderTick;                              // 内置渲染管线
            RenderPipelineManager.beginCameraRendering += OnSrpRenderTick; // URP/HDRP
            ResolveHdrp();
            ClarityPlugin.Log.LogInfo("Runner.Start: render hooks registered (hdrpCustomPass=" + (_tVolume != null) + "). first scan...");
            ScanAll("start");
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= OnRenderTick;
            RenderPipelineManager.beginCameraRendering -= OnSrpRenderTick;
        }

        private void ResolveHdrp()
        {
            if (_hdrpResolved) return;
            _hdrpResolved = true;
            try
            {
                _tVolume = AccessTools.TypeByName("UnityEngine.Rendering.HighDefinition.CustomPassVolume");
                if (_tVolume != null)
                    _fCustomPasses = AccessTools.Field(_tVolume, "customPasses");
            }
            catch { _tVolume = null; _fCustomPasses = null; }
        }

        private void OnRenderTick(Camera cam) => Suppress(true);
        private void OnSrpRenderTick(ScriptableRenderContext ctx, Camera cam) => Suppress(true);

        private void LateUpdate()
        {
            if (!_lateSeen) { _lateSeen = true; ClarityPlugin.Log.LogInfo("Runner.LateUpdate ALIVE."); }
            Suppress(false);

            float now = Time.unscaledTime;
            float interval = ClarityPlugin.Cfg.RescanInterval.Value > 0f ? ClarityPlugin.Cfg.RescanInterval.Value : 2f;
            if (now - _lastScan >= interval) { _lastScan = now; ScanAll("tick"); }
        }

        /// <summary>压制命中缓存：重新隐藏/禁用被重激活的物体、Renderer、CustomPass，并重设材质参数。</summary>
        private void Suppress(bool fromRender)
        {
            if (fromRender && !_renderSeen) { _renderSeen = true; ClarityPlugin.Log.LogInfo("Render callback ALIVE."); }

            Settings cfg = ClarityPlugin.Cfg;

            if (_hidden.Count > 0)
                foreach (GameObject go in _hidden)
                    if (go != null && go.activeSelf) go.SetActive(false);

            if (_hiddenRenderers.Count > 0)
                foreach (Renderer r in _hiddenRenderers)
                    if (r != null && r.enabled) r.enabled = false;

            if (_patched.Count > 0)
                foreach (Material m in _patched)
                {
                    if (m == null) continue;
                    NeutralizeMaterial(m, cfg);
                }

            if (_disabledPasses.Count > 0)
                for (int i = 0; i < _disabledPasses.Count; i++)
                    SetPassEnabled(_disabledPasses[i], false);
        }

        private void ScanAll(string reason)
        {
            Settings cfg = ClarityPlugin.Cfg;
            try
            {
                int hidNew = 0, patchedNew = 0, rendHidNew = 0, passNew = 0;

                // 策略 A：按物体名隐藏
                if (cfg.HideByName.Value)
                {
                    Transform[] all = Resources.FindObjectsOfTypeAll<Transform>();
                    foreach (Transform t in all)
                    {
                        if (t == null) continue;
                        GameObject go = t.gameObject;
                        if (go == null || _hidden.Contains(go)) continue;
                        string n = go.name;
                        if (Shared.Keywords.ContainsAny(n, cfg.Keywords) && !Shared.Keywords.ContainsAny(n, cfg.Deny))
                        {
                            _hidden.Add(go);
                            if (go.activeSelf) go.SetActive(false);
                            hidNew++;
                        }
                    }
                }

                // 策略 B / B2：Renderer 上的材质（命中则清参；可选直接关 Renderer）
                if (cfg.PatchShader.Value || cfg.HideMosaicRenderers.Value)
                {
                    Renderer[] renderers = Resources.FindObjectsOfTypeAll<Renderer>();
                    foreach (Renderer r in renderers)
                    {
                        if (r == null) continue;
                        Material[] mats = r.sharedMaterials;
                        if (mats == null) continue;
                        bool matchHere = false;
                        foreach (Material m in mats)
                        {
                            if (m == null) continue;
                            if (MaterialMatches(m, cfg))
                            {
                                matchHere = true;
                                if (cfg.PatchShader.Value && _patched.Add(m)) { NeutralizeMaterial(m, cfg); patchedNew++; }
                            }
                        }
                        if (matchHere && cfg.HideMosaicRenderers.Value && _hiddenRenderers.Add(r))
                        {
                            if (r.enabled) r.enabled = false;
                            rendHidNew++;
                        }
                    }
                }

                // 策略 B+：扫描全部已加载材质（含不挂任何 Renderer 的后期/CustomPass 材质）
                if (cfg.ScanAllMaterials.Value && cfg.PatchShader.Value)
                {
                    Material[] allMats = Resources.FindObjectsOfTypeAll<Material>();
                    foreach (Material m in allMats)
                    {
                        if (m == null || _patched.Contains(m)) continue;
                        if (MaterialMatches(m, cfg)) { _patched.Add(m); NeutralizeMaterial(m, cfg); patchedNew++; }
                    }
                }

                // 策略 D：反射禁用使用马赛克材质的 CustomPassVolume 全屏 pass
                if (cfg.DisableCustomPasses.Value && _tVolume != null && _fCustomPasses != null)
                    passNew = ScanCustomPasses(cfg);

                if (cfg.Diagnostic.Value) DumpDiagnostics();

                if (hidNew > 0 || patchedNew > 0 || rendHidNew > 0 || passNew > 0)
                    ClarityPlugin.Log.LogInfo("[" + reason + "] hidObj+=" + hidNew + "(tot " + _hidden.Count +
                        ") patchedMat+=" + patchedNew + "(tot " + _patched.Count +
                        ") hidRend+=" + rendHidNew + "(tot " + _hiddenRenderers.Count +
                        ") disabledPass+=" + passNew + "(tot " + _disabledPasses.Count + ")");
            }
            catch (Exception e)
            {
                ClarityPlugin.Log.LogError("Scan error: " + e);
            }
        }

        private static bool MaterialMatches(Material m, Settings cfg)
        {
            string mn = m.name;
            string sn = m.shader != null ? m.shader.name : string.Empty;
            if (Shared.Keywords.ContainsAny(mn, cfg.Deny) || Shared.Keywords.ContainsAny(sn, cfg.Deny)) return false;
            return Shared.Keywords.ContainsAny(mn, cfg.Keywords) || Shared.Keywords.ContainsAny(sn, cfg.Keywords);
        }

        private static void NeutralizeMaterial(Material m, Settings cfg)
        {
            for (int i = 0; i < cfg.PixelParams.Length; i++)
                if (m.HasProperty(cfg.PixelParams[i])) m.SetFloat(cfg.PixelParams[i], 1e-07f);
        }

        // ---- CustomPass（HDRP/URP）反射处理 ----

        private int ScanCustomPasses(Settings cfg)
        {
            int n = 0;
            UnityEngine.Object[] vols;
            try { vols = Resources.FindObjectsOfTypeAll(_tVolume); }
            catch { return 0; }

            foreach (UnityEngine.Object vo in vols)
            {
                if (vo == null) continue;
                IList passes = null;
                try { passes = _fCustomPasses.GetValue(vo) as IList; } catch { }
                if (passes == null) continue;
                foreach (object pass in passes)
                {
                    if (pass == null) continue;
                    Material mat = GetPassMaterial(pass);
                    if (mat == null || mat.shader == null) continue;
                    if (Shared.Keywords.ContainsAny(mat.shader.name, cfg.Keywords) &&
                        !Shared.Keywords.ContainsAny(mat.name, cfg.Deny))
                    {
                        bool already = _disabledPasses.Contains(pass);
                        SetPassEnabled(pass, false);
                        if (!already) { _disabledPasses.Add(pass); n++; }
                    }
                }
            }
            return n;
        }

        private static Material GetPassMaterial(object pass)
        {
            Type t = pass.GetType();
            FieldInfo f = AccessTools.Field(t, "fullscreenPassMaterial")
                       ?? AccessTools.Field(t, "material")
                       ?? AccessTools.Field(t, "m_Material");
            if (f == null) return null;
            try { return f.GetValue(pass) as Material; } catch { return null; }
        }

        private static void SetPassEnabled(object pass, bool value)
        {
            if (pass == null) return;
            try
            {
                Type t = pass.GetType();
                FieldInfo f = AccessTools.Field(t, "enabled");
                if (f != null && f.FieldType == typeof(bool)) { f.SetValue(pass, value); return; }
                PropertyInfo p = AccessTools.Property(t, "enabled");
                if (p != null && p.CanWrite) p.SetValue(pass, value, null);
            }
            catch { }
        }

        // ---- 诊断 dump（升级版：含全部 shader/material/CustomPass + 命中材质逐属性）----

        private void DumpDiagnostics()
        {
            try
            {
                Settings cfg = ClarityPlugin.Cfg;
                SortedSet<string> shaders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                SortedSet<string> matRows = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                SortedSet<string> rendRows = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                List<string> censorDetail = new List<string>();

                foreach (Shader sh in Resources.FindObjectsOfTypeAll<Shader>())
                    if (sh != null) shaders.Add(sh.name);

                foreach (Material m in Resources.FindObjectsOfTypeAll<Material>())
                {
                    if (m == null) continue;
                    string sn = m.shader != null ? m.shader.name : "?";
                    matRows.Add(m.name + " | " + sn);
                    if (m.shader != null && Shared.Keywords.ContainsAny(m.shader.name, cfg.Keywords))
                        censorDetail.Add(DescribeMaterial(m));
                }

                foreach (Renderer r in Resources.FindObjectsOfTypeAll<Renderer>())
                {
                    if (r == null || r.gameObject == null) continue;
                    string flag = r.gameObject.activeInHierarchy ? "[on ] " : "[off] ";
                    Material[] mats = r.sharedMaterials;
                    if (mats == null || mats.Length == 0) { rendRows.Add(flag + r.gameObject.name + " | (no material)"); continue; }
                    foreach (Material m in mats)
                    {
                        string sh = (m != null && m.shader != null) ? m.shader.name : "?";
                        string mn = m != null ? m.name : "(null)";
                        rendRows.Add(flag + r.gameObject.name + " | " + mn + " | " + sh);
                    }
                }

                List<string> passRows = new List<string>();
                if (_tVolume != null && _fCustomPasses != null)
                {
                    foreach (UnityEngine.Object vo in Resources.FindObjectsOfTypeAll(_tVolume))
                    {
                        if (vo == null) continue;
                        IList passes = null;
                        try { passes = _fCustomPasses.GetValue(vo) as IList; } catch { }
                        if (passes == null) continue;
                        foreach (object pass in passes)
                        {
                            if (pass == null) continue;
                            Material mat = GetPassMaterial(pass);
                            string sn = (mat != null && mat.shader != null) ? mat.shader.name : "(no material)";
                            passRows.Add(vo.name + " :: " + pass.GetType().Name + " -> " + sn);
                        }
                    }
                }

                string path = Path.Combine(Paths.BepInExRootPath, "ClarityKit_dump.txt");
                using (StreamWriter w = new StreamWriter(path, false))
                {
                    w.WriteLine("=== ClarityKit Diagnostics (Mono) v" + ClarityPlugin.VERSION + " ===");
                    w.WriteLine("keywords: " + string.Join(",", cfg.Keywords));
                    w.WriteLine("hdrp CustomPass available: " + (_tVolume != null));
                    w.WriteLine("counts: shaders=" + shaders.Count + " materials=" + matRows.Count +
                                " rendererRows=" + rendRows.Count + " customPassRows=" + passRows.Count);
                    w.WriteLine();
                    w.WriteLine("--- CENSOR-MATCHED material details (" + censorDetail.Count + ") ---");
                    foreach (string s in censorDetail) w.WriteLine(s);
                    w.WriteLine();
                    w.WriteLine("--- CustomPass volumes (" + passRows.Count + ") ---");
                    foreach (string s in passRows) w.WriteLine(s);
                    w.WriteLine();
                    w.WriteLine("--- All shaders (" + shaders.Count + ") ---");
                    foreach (string s in shaders) w.WriteLine(s);
                    w.WriteLine();
                    w.WriteLine("--- All materials: name | shader (" + matRows.Count + ") ---");
                    foreach (string s in matRows) w.WriteLine(s);
                    w.WriteLine();
                    w.WriteLine("--- Renderers: [active] object | material | shader ---");
                    foreach (string s in rendRows) w.WriteLine(s);
                }
                ClarityPlugin.Log.LogInfo("Diagnostics dumped -> " + path + " (shaders=" + shaders.Count +
                    ", mats=" + matRows.Count + ", censorMats=" + censorDetail.Count + ", passes=" + passRows.Count + ")");
            }
            catch (Exception e)
            {
                ClarityPlugin.Log.LogError("Dump error: " + e);
            }
        }

        private static string DescribeMaterial(Material m)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("MAT '").Append(m.name).Append("' shader '").Append(m.shader != null ? m.shader.name : "?").Append("'");
            try
            {
                Shader sh = m.shader;
                if (sh != null)
                {
                    int cnt = sh.GetPropertyCount();
                    for (int i = 0; i < cnt; i++)
                    {
                        string pn = sh.GetPropertyName(i);
                        ShaderPropertyType pt = sh.GetPropertyType(i);
                        string val = string.Empty;
                        try
                        {
                            if (pt == ShaderPropertyType.Float || pt == ShaderPropertyType.Range)
                                val = "=" + m.GetFloat(pn).ToString("0.###");
                            else if (pt == ShaderPropertyType.Vector)
                                val = "=" + m.GetVector(pn).ToString();
                        }
                        catch { }
                        sb.Append("\n    ").Append(pt).Append(" ").Append(pn).Append(val);
                    }
                }
            }
            catch (Exception e)
            {
                sb.Append("  (prop dump failed: ").Append(e.Message).Append(")");
            }
            return sb.ToString();
        }
    }
}
