"""ClarityKit 启动器

环境检查（Python / tkinter）后启动 GUI。dotnet SDK 为可选项：
仅 IL2CPP 游戏的去码插件需要它现场编译；Mono 游戏无需。
"""

import os
import sys
import subprocess


def run(cmd):
    return subprocess.run(cmd, capture_output=True, text=True)


def main():
    print("=" * 50)
    print("  ClarityKit")
    print("=" * 50)
    print()

    v = sys.version_info
    print("[OK] Python %d.%d.%d" % (v.major, v.minor, v.micro))
    if v < (3, 8):
        print("[X] 需要 Python 3.8+，请升级后重试")
        input("按回车退出...")
        sys.exit(1)

    try:
        import tkinter  # noqa: F401
        print("[OK] tkinter 可用")
    except ImportError:
        print("[X] 缺少 tkinter，请安装含 tkinter 的标准 Python（python.org 官方版自带）")
        input("按回车退出...")
        sys.exit(1)

    # dotnet 可选检查（仅 IL2CPP 现场编译需要）
    try:
        p = run(["dotnet", "--version"])
        if p.returncode == 0:
            print("[OK] dotnet %s（IL2CPP 现场编译可用）" % (p.stdout or "").strip())
        else:
            print("[!] 未检测到 dotnet SDK：Mono 游戏不受影响；IL2CPP 游戏装去码插件时需要它")
    except Exception:
        print("[!] 未检测到 dotnet SDK：Mono 游戏不受影响；IL2CPP 游戏装去码插件时需要它")

    print("\n[..] 启动界面 ...")
    app = os.path.join(os.path.dirname(os.path.abspath(__file__)), "app.py")
    subprocess.run([sys.executable, app])


if __name__ == "__main__":
    main()
