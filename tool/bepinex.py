"""ClarityKit · BepInEx 安装模块

策略「二者结合」：
  1) 先检测游戏现有 BepInEx（是否已装、Mono5/IL2CPP6、是否与后端匹配）
  2) 缺失或不匹配 → 安装；优先用内置模板（templates/），缺模板再经代理下载
"""

import os
import shutil

THIS = os.path.dirname(os.path.abspath(__file__))
TEMPLATES = os.path.join(THIS, "templates")


def inspect_bepinex(game_dir):
    """检测游戏现有 BepInEx 状态。"""
    r = {"installed": False, "flavor": None, "doorstop": None}
    core = os.path.join(game_dir, "BepInEx", "core")
    if not os.path.isdir(core):
        return r
    r["installed"] = True
    try:
        files = set(os.listdir(core))
    except OSError:
        files = set()
    if "BepInEx.Unity.IL2CPP.dll" in files:
        r["flavor"] = "IL2CPP"   # BepInEx 6
    elif "BepInEx.dll" in files:
        r["flavor"] = "Mono"     # BepInEx 5
    dv = os.path.join(game_dir, ".doorstop_version")
    if os.path.exists(dv):
        try:
            with open(dv, encoding="utf-8", errors="ignore") as f:
                r["doorstop"] = f.read().strip()
        except Exception:
            pass
    return r


def needs_install(game_dir, backend):
    """判断是否需要（重新）安装 BepInEx，返回 (need: bool, reason: str)。"""
    cur = inspect_bepinex(game_dir)
    if not cur["installed"]:
        return True, "未安装 BepInEx"
    if cur["flavor"] and cur["flavor"] != backend:
        return True, "已装 BepInEx 为 %s 版，与游戏后端 %s 不匹配" % (cur["flavor"], backend)
    return False, "已安装匹配的 BepInEx（%s）" % cur["flavor"]


def _merge_copy(src, dst, log):
    count = 0
    for root, _dirs, files in os.walk(src):
        rel = os.path.relpath(root, src)
        target = dst if rel == "." else os.path.join(dst, rel)
        os.makedirs(target, exist_ok=True)
        for f in files:
            shutil.copy2(os.path.join(root, f), os.path.join(target, f))
            count += 1
    log("  已复制 %d 个文件" % count)


def install(game_dir, backend, log=print, proxy="127.0.0.1:7890"):
    """安装 BepInEx 到游戏目录。backend: 'Mono' | 'IL2CPP'。返回是否成功。"""
    if backend not in ("Mono", "IL2CPP"):
        log("× 未知后端: %s" % backend)
        return False

    name = "bepinex_mono" if backend == "Mono" else "bepinex_il2cpp"
    tpl = os.path.join(TEMPLATES, name)
    if os.path.isdir(os.path.join(tpl, "BepInEx", "core")):
        log("使用内置模板安装 BepInEx（%s）..." % backend)
        _merge_copy(tpl, game_dir, log)
        log("√ BepInEx 安装完成（内置模板）。")
        if backend == "IL2CPP":
            log("  提示：IL2CPP 需先启动一次游戏，BepInEx 才会生成 interop 程序集。")
        return True

    log("内置模板缺失，尝试经代理 %s 下载 ..." % proxy)
    return _download_install(game_dir, backend, log, proxy)


def _download_install(game_dir, backend, log, proxy):
    """联网下载回退（内置模板未覆盖的架构/版本时）。"""
    # TODO: 经代理从 GitHub releases 下载对应 BepInEx 并解压。
    # 内置模板已覆盖 x64 的 Mono(BE5) 与 IL2CPP(BE6)；此分支留待支持 x86/特殊版本。
    log("× 联网下载尚未实现：当前内置模板覆盖 x64 的 Mono / IL2CPP。")
    log("  其他架构/版本请先手动安装 BepInEx，再用本工具装去码插件。")
    return False


if __name__ == "__main__":
    import sys
    import json
    if len(sys.argv) >= 2:
        gd = sys.argv[1]
        print("inspect:", json.dumps(inspect_bepinex(gd), ensure_ascii=False))
        if len(sys.argv) >= 3:
            print("needs_install:", needs_install(gd, sys.argv[2]))
