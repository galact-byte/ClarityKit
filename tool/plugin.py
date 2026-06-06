"""ClarityKit · 去码插件安装模块

  Mono   : 复制随工具自带的预编译 ClarityKit.Mono.dll（跨 Mono 游戏通用）
  IL2CPP : 用自带源码 + 目标游戏 interop 现场 dotnet build
           （IL2CPP 程序集与游戏/Unity 版本绑定，无法预编译通吃）
"""

import os
import shutil
import subprocess

THIS = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(THIS, "assets")
IL2_SRC = os.path.join(ASSETS, "il2cpp_src")
MONO_DLL = os.path.join(ASSETS, "ClarityKit.Mono.dll")


def has_dotnet():
    """检测 dotnet SDK 是否可用，返回 (ok, version)。"""
    try:
        p = subprocess.run(["dotnet", "--version"], capture_output=True, text=True, timeout=30)
        return (p.returncode == 0), (p.stdout or "").strip()
    except Exception:
        return False, ""


def install_plugin(game_dir, backend, log=print, interop_ready=True):
    """安装去码插件到游戏。backend: 'Mono' | 'IL2CPP'。返回是否成功。"""
    plugins = os.path.join(game_dir, "BepInEx", "plugins")
    os.makedirs(plugins, exist_ok=True)

    if backend == "Mono":
        if not os.path.exists(MONO_DLL):
            log("× 缺少预编译 ClarityKit.Mono.dll（工具资源不完整）")
            return False
        shutil.copy2(MONO_DLL, os.path.join(plugins, "ClarityKit.Mono.dll"))
        log("√ 已安装 ClarityKit.Mono.dll")
        return True

    if backend == "IL2CPP":
        if not interop_ready:
            log("× IL2CPP interop 未生成：请先启动一次游戏让 BepInEx 生成 interop，再安装插件。")
            return False
        ok, ver = has_dotnet()
        if not ok:
            log("× 未检测到 dotnet SDK；IL2CPP 插件需现场编译，请先安装 .NET SDK 后重试。")
            return False
        log("检测到 dotnet %s，开始按目标游戏 interop 现场编译 IL2CPP 插件 ..." % ver)
        return _compile_il2cpp(game_dir, plugins, log)

    log("× 未知后端: %s" % str(backend))
    return False


def _compile_il2cpp(game_dir, plugins, log):
    csproj = os.path.join(IL2_SRC, "ClarityKit.IL2CPP.csproj")
    if not os.path.exists(csproj):
        log("× 缺少 IL2CPP 源码工程（工具资源不完整）")
        return False

    cmd = ["dotnet", "build", csproj, "-c", "Release", "--nologo", "-p:GameDir=" + game_dir]
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, timeout=600)
    except Exception as e:
        log("× 编译异常: " + str(e))
        return False

    if p.returncode != 0:
        log("× 编译失败（末尾输出）:")
        tail = [ln for ln in (p.stdout or "").splitlines() if ln.strip()][-10:]
        for ln in tail:
            log("  " + ln)
        return False

    dll = os.path.join(IL2_SRC, "bin", "Release", "ClarityKit.IL2CPP.dll")
    if not os.path.exists(dll):
        log("× 编译完成但未找到产物 dll")
        return False

    shutil.copy2(dll, os.path.join(plugins, "ClarityKit.IL2CPP.dll"))
    log("√ 已编译并安装 ClarityKit.IL2CPP.dll")
    return True


if __name__ == "__main__":
    import sys
    if len(sys.argv) >= 3:
        gd, backend = sys.argv[1], sys.argv[2]
        ok = install_plugin(gd, backend, interop_ready=True)
        print("install_plugin ->", ok)
    else:
        print("用法: python plugin.py <游戏目录> <Mono|IL2CPP>")
        print("dotnet:", has_dotnet())
