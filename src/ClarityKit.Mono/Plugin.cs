using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ClarityKit.Shared;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClarityKit.Mono
{
    /// <summary>
    /// ClarityKit 通用去码引擎（Mono / BepInEx 5 版）。
    ///
    /// 关键教训：本环境下直接用 BaseUnityPlugin 自身的 Unity 消息（Update/LateUpdate/渲染事件）
    /// 全都不触发——只有 Awake 跑。故改为新建独立 GameObject 挂运行器 ClarityRunner，
    /// 与已验证可用的 IL2CPP 版同模式。Runner 用 LateUpdate 驱动扫描、渲染前回调做压制。
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    public class ClarityPlugin : BaseUnityPlugin
    {
        public const string GUID = "com.clarity.kit.mono";
        public const string NAME = "ClarityKit (Mono)";
        public const string VERSION = "0.4.0";

        internal static ManualLogSource Log;
        internal static Settings Cfg;

        private void Awake()
        {
            Log = Logger;
            Cfg = new Settings(Config);

            GameObject host = new GameObject("ClarityKitRunner");
            DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<ClarityRunner>();

            Log.LogInfo(NAME + " v" + VERSION + " loaded, runner attached. keywords=[" +
                        string.Join(",", Cfg.Keywords) + "]");
        }
    }

    /// <summary>配置载体。</summary>
    internal class Settings
    {
        public readonly ConfigEntry<bool> HideByName;
        public readonly ConfigEntry<bool> PatchShader;
        public readonly ConfigEntry<bool> Diagnostic;
        public readonly ConfigEntry<float> RescanInterval;
        public readonly string[] Keywords;
        public readonly string[] PixelParams;
        public readonly string[] Deny;

        public Settings(ConfigFile config)
        {
            HideByName = config.Bind("Strategies", "HideByName", true, "策略A：隐藏名字含马赛克关键词的物体");
            PatchShader = config.Bind("Strategies", "PatchShader", true, "策略B：把马赛克着色器的像素化参数清零");
            Diagnostic = config.Bind("Diagnostics", "DumpScene", false, "导出场景内 shader 名与全部 Renderer 清单到 BepInEx/ClarityKit_dump.txt");
            RescanInterval = config.Bind("Performance", "RescanIntervalSeconds", 2f, "全场景重扫间隔（秒）；<=0 视为 2");
            ConfigEntry<string> kw = config.Bind("Keywords", "MosaicKeywords", string.Join(",", Shared.Keywords.DefaultMosaicKeywords), "马赛克关键词（逗号分隔，匹配物体名/材质名/着色器名）");
            ConfigEntry<string> pp = config.Bind("Keywords", "PixelationParams", string.Join(",", Shared.Keywords.DefaultPixelationParams), "着色器像素化参数名（逗号分隔）");
            ConfigEntry<string> dn = config.Bind("Keywords", "DenyKeywords", string.Join(",", Shared.Keywords.DefaultDenyKeywords), "黑名单关键词（逗号分隔），命中则跳过");
            Keywords = Shared.Keywords.SplitCsv(kw.Value);
            PixelParams = Shared.Keywords.SplitCsv(pp.Value);
            Deny = Shared.Keywords.SplitCsv(dn.Value);
        }
    }

    /// <summary>独立运行器：LateUpdate 驱动扫描，渲染前回调做压制。挂在自建 GameObject 上以确保收到 Unity 消息。</summary>
    internal class ClarityRunner : MonoBehaviour
    {
        private readonly HashSet<GameObject> _hidden = new HashSet<GameObject>();
        private readonly HashSet<Material> _patched = new HashSet<Material>();
        private float _lastScan = -999f;
        private bool _renderSeen;
        private bool _lateSeen;

        private void Start()
        {
            Camera.onPreCull += OnRenderTick;                          // 内置渲染管线
            RenderPipelineManager.beginCameraRendering += OnSrpRenderTick; // URP/HDRP
            ClarityPlugin.Log.LogInfo("Runner.Start: render hooks registered, first scan...");
            ScanAll("start");
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= OnRenderTick;
            RenderPipelineManager.beginCameraRendering -= OnSrpRenderTick;
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

        /// <summary>压制命中缓存：重新隐藏被重激活的物体、重设被重置的材质参数。渲染前回调调用以盖住游戏的每帧重激活。</summary>
        private void Suppress(bool fromRender)
        {
            if (fromRender && !_renderSeen) { _renderSeen = true; ClarityPlugin.Log.LogInfo("Render callback ALIVE."); }

            Settings cfg = ClarityPlugin.Cfg;
            if (cfg.HideByName.Value && _hidden.Count > 0)
            {
                foreach (GameObject go in _hidden)
                    if (go != null && go.activeSelf) go.SetActive(false);
            }
            if (cfg.PatchShader.Value && _patched.Count > 0)
            {
                foreach (Material m in _patched)
                {
                    if (m == null) continue;
                    for (int i = 0; i < cfg.PixelParams.Length; i++)
                        if (m.HasProperty(cfg.PixelParams[i])) m.SetFloat(cfg.PixelParams[i], 1e-07f);
                }
            }
        }

        private void ScanAll(string reason)
        {
            Settings cfg = ClarityPlugin.Cfg;
            try
            {
                int hidNew = 0, patchedNew = 0, transformTotal = 0, rendererTotal = 0;

                if (cfg.HideByName.Value)
                {
                    Transform[] all = Resources.FindObjectsOfTypeAll<Transform>();
                    transformTotal = all.Length;
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

                if (cfg.PatchShader.Value)
                {
                    Renderer[] renderers = Resources.FindObjectsOfTypeAll<Renderer>();
                    rendererTotal = renderers.Length;
                    foreach (Renderer r in renderers)
                    {
                        if (r == null) continue;
                        Material[] mats = r.sharedMaterials;
                        if (mats == null) continue;
                        foreach (Material m in mats)
                        {
                            if (m == null || _patched.Contains(m)) continue;
                            string mn = m.name;
                            string sn = m.shader != null ? m.shader.name : string.Empty;
                            if ((Shared.Keywords.ContainsAny(mn, cfg.Keywords) || Shared.Keywords.ContainsAny(sn, cfg.Keywords))
                                && !Shared.Keywords.ContainsAny(mn, cfg.Deny))
                            {
                                bool any = false;
                                for (int i = 0; i < cfg.PixelParams.Length; i++)
                                    if (m.HasProperty(cfg.PixelParams[i])) { m.SetFloat(cfg.PixelParams[i], 1e-07f); any = true; }
                                if (any) { _patched.Add(m); patchedNew++; }
                            }
                        }
                    }
                }

                if (cfg.Diagnostic.Value) DumpDiagnostics();

                // 只在有新命中时记日志，避免每次定时扫描刷屏
                if (hidNew > 0 || patchedNew > 0)
                    ClarityPlugin.Log.LogInfo("[" + reason + "] transforms=" + transformTotal + " renderers=" + rendererTotal +
                                " hidden+=" + hidNew + "(tot " + _hidden.Count + ") patched+=" + patchedNew + "(tot " + _patched.Count + ")");
            }
            catch (Exception e)
            {
                ClarityPlugin.Log.LogError("Scan error: " + e);
            }
        }

        private void DumpDiagnostics()
        {
            try
            {
                SortedSet<string> rendererRows = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                SortedSet<string> shaders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (Renderer r in Resources.FindObjectsOfTypeAll<Renderer>())
                {
                    if (r == null || r.gameObject == null) continue;
                    string flag = r.gameObject.activeInHierarchy ? "[on ] " : "[off] ";
                    string objName = r.gameObject.name;
                    Material[] mats = r.sharedMaterials;
                    if (mats == null || mats.Length == 0) { rendererRows.Add(flag + objName + " | (no material)"); continue; }
                    foreach (Material m in mats)
                    {
                        if (m == null) { rendererRows.Add(flag + objName + " | (null material)"); continue; }
                        string sh = m.shader != null ? m.shader.name : "?";
                        if (m.shader != null) shaders.Add(m.shader.name);
                        rendererRows.Add(flag + objName + " | " + m.name + " | " + sh);
                    }
                }

                string path = Path.Combine(Paths.BepInExRootPath, "ClarityKit_dump.txt");
                using (StreamWriter w = new StreamWriter(path, false))
                {
                    w.WriteLine("=== ClarityKit Diagnostics (Mono) ===");
                    w.WriteLine("current keywords: " + string.Join(",", ClarityPlugin.Cfg.Keywords));
                    w.WriteLine("renderer rows: " + rendererRows.Count + ", shaders: " + shaders.Count);
                    w.WriteLine();
                    w.WriteLine("--- All shader names (" + shaders.Count + ") ---");
                    foreach (string s in shaders) w.WriteLine(s);
                    w.WriteLine();
                    w.WriteLine("--- All renderers: [active] objectName | materialName | shaderName ---");
                    foreach (string s in rendererRows) w.WriteLine(s);
                }
                ClarityPlugin.Log.LogInfo("Diagnostics dumped: renderers=" + rendererRows.Count + ", shaders=" + shaders.Count + " -> " + path);
            }
            catch (Exception e)
            {
                ClarityPlugin.Log.LogError("Dump error: " + e);
            }
        }
    }
}
