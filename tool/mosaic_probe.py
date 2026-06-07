"""ClarityKit · 马赛克机制静态探针

不启动游戏，仅静态扫描，判定目标游戏的"打码/马赛克"是否属于 ClarityKit 能处理的
"运行时叠加层 / 着色器 / Live2D 滤镜层"类型，还是"烧进 CG 贴图"（本工具无法去除）。

用途：装插件前给用户明确预期，避免"装了没效果"的困惑（这是早期 Taimashi 上踩过的坑）。

判定依据（强特征，尽量低误伤）：
  - 资源里出现名字含 mosaic/censor/pixelate 的着色器/材质  → 运行时着色器型（可去）
  - 存在 Live2D Cubism（滤镜层惯以 *ForFilter 命名）        → Live2D 型（可去）
  - Assembly-CSharp 代码含 mosaic/モザイク/修正/censor 等   → 运行时代码型（大概率可去）
  - 以上都没有                                              → 烧进贴图/无码（去不了）

可独立运行：
    python mosaic_probe.py "X:\\game\\root"
"""

import os
import re
import glob

# 运行时马赛克的"着色器/材质名"强特征（小写 ascii 子串匹配）；
# 故意不含 "pixel"/"blur"——它们在 URP 内置后期里太常见，会误伤。
SHADER_HINTS = ["mosaic", "mozaiku", "censor", "pixelate", "pixelation"]
CODE_HINTS_ASCII = ["mosaic", "mozaiku", "censor", "pixelate", "pixelation", "forfilter"]
CODE_HINTS_JP = ["モザイク", "修正", "ぼかし"]  # 马赛克 / 修正(打码) / 模糊
MAX_ASSET_BYTES = 400 * 1024 * 1024  # 扫描 .assets 的总字节上限，避免超大游戏卡顿


def _data_dir(game_dir):
    for d in glob.glob(os.path.join(game_dir, "*_Data")):
        if os.path.isdir(d):
            return d
    return None


def _scan_bytes(b, ascii_keys, jp_keys=None):
    hits = {}
    a = b.decode("latin-1", "ignore").lower()
    for k in ascii_keys:
        c = a.count(k)
        if c:
            hits[k] = hits.get(k, 0) + c
    if jp_keys:
        u16 = b.decode("utf-16-le", "ignore")
        u8 = b.decode("utf-8", "ignore")
        for k in jp_keys:
            c = u16.count(k) + u8.count(k)
            if c:
                hits[k] = hits.get(k, 0) + c
    return hits


def _matching_strings(b, keys, cap=40):
    out = []
    seen = set()
    for m in re.findall(rb"[ -~]{5,}", b):
        t = m.decode("ascii", "ignore")
        tl = t.lower()
        if len(t) <= 90 and t not in seen and any(k in tl for k in keys):
            seen.add(t)
            out.append(t)
            if len(out) >= cap:
                break
    return out


def probe(game_dir):
    """静态探测马赛克机制，返回结构化结果 dict。"""
    game_dir = os.path.abspath(game_dir)
    r = {
        "game_dir": game_dir,
        "verdict": "inconclusive",   # runtime_shader | live2d | runtime_code | baked_or_none | inconclusive
        "removable": None,           # True / False / None
        "evidence": [],
        "shaders": [],
        "hint": "",
    }
    data = _data_dir(game_dir)
    if not data:
        r["hint"] = "未找到 *_Data 目录（可能不是 Unity 游戏）。"
        return r
    managed = os.path.join(data, "Managed")

    code_hits = {}
    live2d = False
    shader_names = []

    # 1) 代码侧：Assembly-CSharp(+firstpass)
    for name in ("Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll"):
        p = os.path.join(managed, name)
        if os.path.exists(p):
            try:
                b = open(p, "rb").read()
            except Exception:
                continue
            for k, v in _scan_bytes(b, CODE_HINTS_ASCII, CODE_HINTS_JP).items():
                code_hits[k] = code_hits.get(k, 0) + v

    # Live2D Cubism 痕迹
    if os.path.isdir(managed):
        for f in os.listdir(managed):
            if "Cubism" in f or "Live2D" in f:
                live2d = True
                break

    # 2) 资源侧：globalgamemanagers + *.assets（跳过 .resS/.resource，命中即止，带总量上限）
    scan_files = []
    gg = os.path.join(data, "globalgamemanagers")
    if os.path.exists(gg):
        scan_files.append(gg)
    scan_files += sorted(glob.glob(os.path.join(data, "*.assets")))
    budget = MAX_ASSET_BYTES
    for p in scan_files:
        if budget <= 0:
            break
        try:
            sz = os.path.getsize(p)
            b = open(p, "rb").read(min(sz, budget))
        except Exception:
            continue
        budget -= len(b)
        for n in _matching_strings(b, SHADER_HINTS):
            if n not in shader_names:
                shader_names.append(n)
        if shader_names:
            break  # 命中一处即可，避免继续扫超大 .assets

    # 判定（按可信度从强到弱）
    if shader_names:
        r["verdict"] = "runtime_shader"
        r["removable"] = True
        r["shaders"] = shader_names[:20]
        r["evidence"].append("发现疑似马赛克着色器/材质名: " + ", ".join(shader_names[:6]))
        r["hint"] = ("运行时着色器型 → ClarityKit 可处理。命中的着色器关键词默认已在 "
                     "MosaicKeywords 内（mosaic/censor/pixelate）；如未命中可手动补进配置。")
    elif live2d:
        r["verdict"] = "live2d"
        r["removable"] = True
        r["evidence"].append("检测到 Live2D Cubism（滤镜层常以 *ForFilter 命名）")
        if code_hits:
            r["evidence"].append("代码关键词: " + ", ".join(sorted(code_hits)))
        r["hint"] = "Live2D 型 → 通常可隐藏 *ForFilter 滤镜层去除；开 DumpScene 确认命名后调关键词。"
    elif code_hits:
        r["verdict"] = "runtime_code"
        r["removable"] = True
        r["evidence"].append("代码含马赛克关键词: " + ", ".join(sorted(code_hits)))
        r["hint"] = "代码侧有马赛克逻辑 → 大概率运行时可处理；开 DumpScene 取真实着色器/物体名。"
    else:
        r["verdict"] = "baked_or_none"
        r["removable"] = False
        r["evidence"].append("代码无马赛克关键词、资源无马赛克着色器名")
        r["hint"] = ("未发现运行时马赛克机制 → 打码很可能烧进 CG 贴图（或本就无码）。"
                     "ClarityKit 无法去除烧进贴图的打码（需 AI 重绘，超出本工具范围）。")
    return r


def summarize(r):
    return {
        "runtime_shader": "运行时着色器型 · 可去除",
        "live2d": "Live2D 滤镜层型 · 可去除",
        "runtime_code": "运行时(代码)型 · 大概率可去除",
        "baked_or_none": "无运行时马赛克 · 烧进贴图/无码 · 去不了",
        "inconclusive": "无法判定",
    }.get(r["verdict"], r["verdict"])


if __name__ == "__main__":
    import sys
    import json
    if len(sys.argv) < 2:
        print("用法: python mosaic_probe.py <游戏目录>")
        sys.exit(0)
    res = probe(sys.argv[1])
    print("== 判定: " + summarize(res) + " ==")
    print(json.dumps(res, ensure_ascii=False, indent=2))
