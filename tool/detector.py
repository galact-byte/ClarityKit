"""ClarityKit · 游戏检测模块

识别一个目录是否 Unity 游戏、脚本后端（Mono / IL2CPP）、CPU 架构、Unity 版本，
并定位关键子目录（Managed / interop）。是一条龙工具的入口环节。

可独立运行测试：
    python detector.py "X:\\game\\root" ["Y:\\another"]
"""

import os
import re
import glob
import struct
import json


def _find_data_dir(game_dir):
    """返回 *_Data 目录（Unity 数据目录）。"""
    for d in glob.glob(os.path.join(game_dir, "*_Data")):
        if os.path.isdir(d):
            return d
    return None


def _pe_machine(path):
    """读取 PE 文件头的 Machine 字段，判断架构。"""
    try:
        with open(path, "rb") as f:
            if f.read(2) != b"MZ":
                return None
            f.seek(0x3C)
            pe_off = struct.unpack("<I", f.read(4))[0]
            f.seek(pe_off)
            if f.read(4) != b"PE\0\0":
                return None
            machine = struct.unpack("<H", f.read(2))[0]
        return {0x8664: "x64", 0x14C: "x86", 0xAA64: "arm64"}.get(machine, hex(machine))
    except Exception:
        return None


def _unity_version(data_dir):
    """从 globalgamemanagers / data.unity3d 头部提取 Unity 版本串（如 2022.3.46f1）。"""
    for name in ("globalgamemanagers", "data.unity3d"):
        p = os.path.join(data_dir, name)
        if os.path.exists(p):
            try:
                with open(p, "rb") as f:
                    head = f.read(8192)
                m = re.search(rb"(\d+\.\d+\.\d+[fpab]\d+)", head)
                if m:
                    return m.group(1).decode("ascii", "ignore")
            except Exception:
                pass
    return None


def detect(game_dir):
    """检测游戏目录，返回结构化结果 dict。"""
    game_dir = os.path.abspath(game_dir)
    r = {
        "game_dir": game_dir,
        "is_unity": False,
        "backend": None,          # "Mono" | "IL2CPP" | None
        "arch": None,             # "x64" | "x86" | ...
        "unity_version": None,
        "data_dir": None,
        "managed_dir": None,      # Mono：Assembly-CSharp 所在
        "interop_dir": None,      # IL2CPP：interop 程序集（首次运行游戏后生成）
        "interop_ready": False,   # IL2CPP：interop 是否已生成
        "game_assembly": None,    # IL2CPP：GameAssembly.dll
        "bepinex_installed": False,
        "notes": [],
    }

    if not os.path.isdir(game_dir):
        r["notes"].append("目录不存在")
        return r

    data_dir = _find_data_dir(game_dir)
    has_unityplayer = os.path.exists(os.path.join(game_dir, "UnityPlayer.dll"))
    if not data_dir and not has_unityplayer:
        r["notes"].append("未发现 *_Data 或 UnityPlayer.dll，可能不是 Unity 游戏")
        return r

    r["is_unity"] = True
    r["data_dir"] = data_dir

    # --- 后端判定 ---
    game_assembly = os.path.join(game_dir, "GameAssembly.dll")
    il2cpp_data = os.path.join(data_dir, "il2cpp_data") if data_dir else ""
    managed = os.path.join(data_dir, "Managed") if data_dir else ""

    if os.path.exists(game_assembly) or (il2cpp_data and os.path.isdir(il2cpp_data)):
        r["backend"] = "IL2CPP"
        r["game_assembly"] = game_assembly if os.path.exists(game_assembly) else None
        interop = os.path.join(game_dir, "BepInEx", "interop")
        r["interop_dir"] = interop
        r["interop_ready"] = os.path.isdir(interop) and any(
            f.startswith("UnityEngine") for f in (os.listdir(interop) if os.path.isdir(interop) else [])
        )
        if not r["interop_ready"]:
            r["notes"].append("IL2CPP interop 尚未生成：装好 BepInEx 后需先运行游戏一次")
    elif managed and os.path.exists(os.path.join(managed, "Assembly-CSharp.dll")):
        r["backend"] = "Mono"
        r["managed_dir"] = managed
    elif managed and os.path.isdir(managed):
        r["backend"] = "Mono"
        r["managed_dir"] = managed
        r["notes"].append("有 Managed 目录但缺 Assembly-CSharp.dll")
    else:
        r["notes"].append("无法确定 Mono / IL2CPP 后端")

    # --- 架构（PE Machine）---
    arch = None
    up = os.path.join(game_dir, "UnityPlayer.dll")
    if os.path.exists(up):
        arch = _pe_machine(up)
    if not arch and r["game_assembly"]:
        arch = _pe_machine(r["game_assembly"])
    if not arch:
        for exe in glob.glob(os.path.join(game_dir, "*.exe")):
            if "UnityCrashHandler" in os.path.basename(exe):
                continue
            arch = _pe_machine(exe)
            if arch:
                break
    r["arch"] = arch

    # --- Unity 版本 ---
    if data_dir:
        r["unity_version"] = _unity_version(data_dir)

    # --- BepInEx 是否已装 ---
    r["bepinex_installed"] = os.path.isdir(os.path.join(game_dir, "BepInEx", "core"))

    return r


if __name__ == "__main__":
    import sys
    targets = sys.argv[1:]
    if not targets:
        print("用法: python detector.py <游戏目录> [更多目录...]")
        sys.exit(0)
    for gd in targets:
        print(json.dumps(detect(gd), ensure_ascii=False, indent=2))
        print("-" * 60)
