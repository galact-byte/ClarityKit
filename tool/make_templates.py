"""从已装 BepInEx 的参考游戏提取干净的 BepInEx 模板，供 ClarityKit 离线安装。

剥离运行时缓存与具体插件/配置，只保留框架本体：
  Mono(BE5)   : winhttp.dll + doorstop_config.ini + .doorstop_version + BepInEx/core
  IL2CPP(BE6) : 以上 + dotnet/ + BepInEx/unity-libs（interop 首次运行游戏时自动生成，不带）

用法（两个目录分别是已装好对应 BepInEx 的参考游戏根目录）：
    python make_templates.py <mono_game_dir> <il2cpp_game_dir>
"""

import os
import sys
import shutil

EMPTY_SUBDIRS = ("plugins", "patchers", "config")
ROOT_FILES = ("winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt")


def _copy_file(src, dst):
    if os.path.exists(src):
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(src, dst)
        return True
    return False


def _copy_tree(src, dst):
    if os.path.isdir(src):
        shutil.copytree(src, dst, dirs_exist_ok=True)
        return True
    return False


def _dir_size_mb(path):
    total = 0
    for root, _, files in os.walk(path):
        for f in files:
            try:
                total += os.path.getsize(os.path.join(root, f))
            except OSError:
                pass
    return total / 1024 / 1024


def extract(src, dst, il2cpp):
    if os.path.isdir(dst):
        shutil.rmtree(dst)
    os.makedirs(dst, exist_ok=True)

    copied = []
    for f in ROOT_FILES:
        if _copy_file(os.path.join(src, f), os.path.join(dst, f)):
            copied.append(f)

    _copy_tree(os.path.join(src, "BepInEx", "core"), os.path.join(dst, "BepInEx", "core"))
    if il2cpp:
        _copy_tree(os.path.join(src, "dotnet"), os.path.join(dst, "dotnet"))
        _copy_tree(os.path.join(src, "BepInEx", "unity-libs"), os.path.join(dst, "BepInEx", "unity-libs"))

    for d in EMPTY_SUBDIRS:
        os.makedirs(os.path.join(dst, "BepInEx", d), exist_ok=True)

    return copied


if __name__ == "__main__":
    base = os.path.dirname(os.path.abspath(__file__))
    templates = os.path.join(base, "templates")

    if len(sys.argv) < 3:
        print("用法: python make_templates.py <mono_game_dir> <il2cpp_game_dir>")
        print("  从两个已分别装好 BepInEx 5 / BepInEx 6 的参考游戏提取干净模板")
        sys.exit(1)
    mono_src, il2_src = sys.argv[1], sys.argv[2]

    print("[1/2] 提取 Mono (BepInEx 5) 模板 ...")
    mono_dst = os.path.join(templates, "bepinex_mono")
    files = extract(mono_src, mono_dst, il2cpp=False)
    print("  根文件:", files)
    print("  大小: %.1f MB" % _dir_size_mb(mono_dst))

    print("[2/2] 提取 IL2CPP (BepInEx 6) 模板 ... (含 dotnet runtime，较大，请稍候)")
    il2_dst = os.path.join(templates, "bepinex_il2cpp")
    files = extract(il2_src, il2_dst, il2cpp=True)
    print("  根文件:", files)
    print("  大小: %.1f MB" % _dir_size_mb(il2_dst))

    print("\n完成。模板目录:", templates)
