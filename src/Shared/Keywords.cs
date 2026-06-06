using System;

namespace ClarityKit.Shared
{
    /// <summary>
    /// 去码引擎的共享常量与匹配逻辑。
    /// Mono 与 IL2CPP 两个项目通过 &lt;Compile Include="..\Shared\Keywords.cs" Link="Keywords.cs"/&gt;
    /// 共用本文件，保证关键词、着色器参数名、匹配规则在两个框架下完全一致。
    /// </summary>
    internal static class Keywords
    {
        /// <summary>
        /// 马赛克「物体名 / 材质名 / 着色器名」关键词（大小写不敏感子串匹配）。
        /// 取强特征词，尽量降低误伤；可在游戏的 BepInEx 配置文件中增删。
        /// </summary>
        public static readonly string[] DefaultMosaicKeywords =
        {
            "mosaic",      // 通用
            "モザイク",     // 日文：马赛克
            "mozaiku",
            "censor",      // 通用
            "forfilter",   // 部分 Live2D 游戏滤镜层的命名后缀
            "修正",         // 日文：打码
            "pixelate",
            "pixelation",
        };

        /// <summary>
        /// 马赛克着色器常见的「像素化 / 块大小」浮点参数名。
        /// 命中后设极小值即可在视觉上消除马赛克效果。
        /// </summary>
        public static readonly string[] DefaultPixelationParams =
        {
            "_Pixelation",
            "_MosaicSize",
            "_PixelSize",
            "_BlockSize",
            "_CensorSize",
            "_Size",
            "_Density",
        };

        /// <summary>
        /// 黑名单：名字命中这些词的物体 / 材质即使匹配关键词也跳过，
        /// 避免误伤正常的颜色滤镜 / 后处理等。
        /// </summary>
        public static readonly string[] DefaultDenyKeywords =
        {
            "colorfilter",
            "colorgrading",
            "postfilter",
        };

        /// <summary>判断 value 是否包含 keywords 中任意一项（大小写不敏感）。</summary>
        public static bool ContainsAny(string value, string[] keywords)
        {
            if (string.IsNullOrEmpty(value) || keywords == null) return false;
            for (int i = 0; i < keywords.Length; i++)
            {
                string k = keywords[i];
                if (string.IsNullOrEmpty(k)) continue;
                if (value.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        /// <summary>把逗号分隔的配置字符串拆成去空白的非空数组。</summary>
        public static string[] SplitCsv(string csv)
        {
            if (string.IsNullOrEmpty(csv)) return Array.Empty<string>();
            string[] parts = csv.Split(',');
            int n = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                string t = parts[i].Trim();
                if (t.Length > 0) parts[n++] = t;
            }
            string[] result = new string[n];
            Array.Copy(parts, result, n);
            return result;
        }
    }
}
