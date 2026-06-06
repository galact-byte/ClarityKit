using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using ClarityKit.Shared;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace ClarityKit.IL2CPP
{
    /// <summary>
    /// ClarityKit 通用去码引擎（IL2CPP / BepInEx 6 版）。
    ///
    /// 与 Mono 版同思路：不 hook 游戏专属类，只用 UnityEngine 通用类型主动扫描。
    /// 因 IL2CPP 的 interop 程序集与游戏二进制/Unity 版本绑定，本项目由 build-il2cpp.ps1
    /// 引用目标游戏自己的 interop 现场编译。
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    public class ClarityPlugin : BasePlugin
    {
        public const string GUID = "com.clarity.kit.il2cpp";
        public const string NAME = "ClarityKit (IL2CPP)";
        public const string VERSION = "0.1.0";

        internal static ManualLogSource Logger;
        internal static Settings Cfg;

        public override void Load()
        {
            Logger = Log;
            Cfg = new Settings(Config);

            // IL2CPP 需先注册自定义 MonoBehaviour 类型，再挂到常驻物体上
            ClassInjector.RegisterTypeInIl2Cpp<ClarityRunner>();
            GameObject host = new GameObject("ClarityKitRunner");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<ClarityRunner>();

            Logger.LogInfo(NAME + " v" + VERSION + " loaded.");
        }
    }

    /// <summary>配置载体（BepInEx ConfigFile）。</summary>
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
            HideByName = config.Bind("Strategies", "HideByName", true,
                "策略A：隐藏名字含马赛克关键词的物体");
            PatchShader = config.Bind("Strategies", "PatchShader", true,
                "策略B：把马赛克着色器的像素化参数清零");
            Diagnostic = config.Bind("Diagnostics", "DumpScene", false,
                "导出场景内 shader/material/物体名到 BepInEx/ClarityKit_dump.txt");
            RescanInterval = config.Bind("Performance", "RescanIntervalSeconds", 2f,
                "全场景重扫间隔（秒），<=0 视为 2");

            ConfigEntry<string> kw = config.Bind("Keywords", "MosaicKeywords",
                string.Join(",", Shared.Keywords.DefaultMosaicKeywords),
                "马赛克关键词（逗号分隔，匹配物体名/材质名/着色器名）");
            ConfigEntry<string> pp = config.Bind("Keywords", "PixelationParams",
                string.Join(",", Shared.Keywords.DefaultPixelationParams),
                "着色器像素化参数名（逗号分隔）");
            ConfigEntry<string> dn = config.Bind("Keywords", "DenyKeywords",
                string.Join(",", Shared.Keywords.DefaultDenyKeywords),
                "黑名单关键词（逗号分隔），命中则跳过");

            Keywords = Shared.Keywords.SplitCsv(kw.Value);
            PixelParams = Shared.Keywords.SplitCsv(pp.Value);
            Deny = Shared.Keywords.SplitCsv(dn.Value);
        }
    }

    /// <summary>常驻运行器：周期性扫描场景并执行去码策略。</summary>
    internal class ClarityRunner : MonoBehaviour
    {
        // IL2CPP 注入类型必须提供 IntPtr 构造函数
        public ClarityRunner(IntPtr ptr) : base(ptr) { }

        // 用 InstanceID 去重，规避 IL2CPP 包装对象引用比较的不确定性
        private readonly Dictionary<int, GameObject> _hidden = new Dictionary<int, GameObject>();
        private float _timer;
        private bool _started;
        private bool _diagDone;

        // 用 LateUpdate（而非 Update）压制：游戏多在 LateUpdate 重激活滤镜层，
        // 运行时注入的组件执行顺序通常靠后，可压在游戏逻辑之后、渲染之前。
        private void LateUpdate()
        {
            Settings cfg = ClarityPlugin.Cfg;
            if (cfg == null) return;

            // 高频压制：被游戏重新激活的滤镜层再次隐藏
            if (cfg.HideByName.Value && _hidden.Count > 0)
            {
                foreach (KeyValuePair<int, GameObject> kv in _hidden)
                {
                    GameObject go = kv.Value;
                    if (go != null && go.activeSelf) go.SetActive(false);
                }
            }

            // 定时全扫（首次立即执行）
            _timer -= Time.unscaledDeltaTime;
            if (!_started || _timer <= 0f)
            {
                _started = true;
                float interval = cfg.RescanInterval.Value > 0f ? cfg.RescanInterval.Value : 2f;
                _timer = interval;
                ScanAll("tick");
            }
        }

        private void ScanAll(string reason)
        {
            Settings cfg = ClarityPlugin.Cfg;
            try
            {
                if (cfg.Diagnostic.Value && !_diagDone) { Dump(cfg); _diagDone = true; }

                int hid = 0;
                int patched = 0;

                if (cfg.HideByName.Value)
                {
                    foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
                    {
                        if (t == null) continue;
                        GameObject go = t.gameObject;
                        if (go == null) continue;
                        int id = go.GetInstanceID();
                        if (_hidden.ContainsKey(id)) continue;

                        string n = go.name;
                        if (Shared.Keywords.ContainsAny(n, cfg.Keywords) && !Shared.Keywords.ContainsAny(n, cfg.Deny))
                        {
                            _hidden[id] = go;
                            if (go.activeSelf) go.SetActive(false);
                            hid++;
                        }
                    }
                }

                if (cfg.PatchShader.Value)
                {
                    foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
                    {
                        if (r == null) continue;
                        var mats = r.sharedMaterials;
                        if (mats == null) continue;
                        foreach (var m in mats)
                        {
                            if (m == null) continue;
                            string mn = m.name;
                            string sn = m.shader != null ? m.shader.name : string.Empty;
                            if ((Shared.Keywords.ContainsAny(mn, cfg.Keywords) || Shared.Keywords.ContainsAny(sn, cfg.Keywords))
                                && !Shared.Keywords.ContainsAny(mn, cfg.Deny))
                            {
                                foreach (string p in cfg.PixelParams)
                                {
                                    if (m.HasProperty(p)) { m.SetFloat(p, 1e-07f); patched++; }
                                }
                            }
                        }
                    }
                }

                if (hid > 0 || patched > 0)
                    ClarityPlugin.Logger.LogInfo("[" + reason + "] hidden+=" + hid + " (total " + _hidden.Count + "), patched=" + patched);
            }
            catch (Exception e)
            {
                ClarityPlugin.Logger.LogError("Scan error: " + e);
            }
        }

        private void Dump(Settings cfg)
        {
            try
            {
                HashSet<string> shaders = new HashSet<string>();
                HashSet<string> suspectMats = new HashSet<string>();
                HashSet<string> suspectObjs = new HashSet<string>();

                foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
                {
                    if (r == null) continue;
                    var mats = r.sharedMaterials;
                    if (mats == null) continue;
                    foreach (var m in mats)
                    {
                        if (m == null) continue;
                        if (m.shader != null) shaders.Add(m.shader.name);
                        if (Shared.Keywords.ContainsAny(m.name, cfg.Keywords)) suspectMats.Add(m.name);
                    }
                }
                foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
                {
                    if (t == null) continue;
                    if (Shared.Keywords.ContainsAny(t.name, cfg.Keywords)) suspectObjs.Add(t.name);
                }

                string path = Path.Combine(Paths.BepInExRootPath, "ClarityKit_dump.txt");
                using (StreamWriter w = new StreamWriter(path, false))
                {
                    w.WriteLine("=== ClarityKit Diagnostics (IL2CPP) ===");
                    w.WriteLine("--- All shader names (" + shaders.Count + ") ---");
                    foreach (string s in shaders) w.WriteLine(s);
                    w.WriteLine();
                    w.WriteLine("--- Suspect material names (" + suspectMats.Count + ") ---");
                    foreach (string s in suspectMats) w.WriteLine(s);
                    w.WriteLine();
                    w.WriteLine("--- Suspect object names (" + suspectObjs.Count + ") ---");
                    foreach (string s in suspectObjs) w.WriteLine(s);
                }
                ClarityPlugin.Logger.LogInfo("Diagnostics dumped to " + path);
            }
            catch (Exception e)
            {
                ClarityPlugin.Logger.LogError("Dump error: " + e);
            }
        }
    }
}
