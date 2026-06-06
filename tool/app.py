"""ClarityKit · GUI 一条龙工具（tkinter，零依赖）

选择 Unity 游戏目录 → 自动检测 → 安装 BepInEx → 安装去码插件，带实时日志。
耗时操作（编译/复制）在子线程执行，通过 root.after 把日志安全回送到 UI。
"""

import os
import sys
import datetime
import threading
import tkinter as tk
from tkinter import ttk, filedialog, scrolledtext

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import detector
import bepinex
import plugin


class ClarityApp:
    def __init__(self, root):
        self.root = root
        self.info = None
        self.busy = False

        root.title("ClarityKit")
        root.geometry("780x580")
        root.minsize(660, 480)
        pad = {"padx": 8, "pady": 6}

        top = ttk.Frame(root)
        top.pack(fill="x", **pad)
        ttk.Label(top, text="游戏目录:").pack(side="left")
        self.dir_var = tk.StringVar()
        entry = ttk.Entry(top, textvariable=self.dir_var)
        entry.pack(side="left", fill="x", expand=True, padx=6)
        entry.bind("<Return>", lambda e: self.do_detect())  # 手动粘贴路径后按回车即检测
        ttk.Button(top, text="浏览", command=self.browse).pack(side="left")

        info = ttk.LabelFrame(root, text="检测结果")
        info.pack(fill="x", **pad)
        self.v_backend = self._info_row(info, "脚本后端", 0)
        self.v_arch = self._info_row(info, "架构", 1)
        self.v_unity = self._info_row(info, "Unity 版本", 2)
        self.v_bepinex = self._info_row(info, "BepInEx", 3)

        ops = ttk.Frame(root)
        ops.pack(fill="x", **pad)
        self.btn_bep = ttk.Button(ops, text="1. 安装 BepInEx", command=self.install_bepinex, state="disabled")
        self.btn_bep.pack(side="left")
        self.btn_plugin = ttk.Button(ops, text="2. 安装去码插件", command=self.install_plugin, state="disabled")
        self.btn_plugin.pack(side="left", padx=6)
        self.btn_all = ttk.Button(ops, text="一键去码", command=self.one_click, state="disabled")
        self.btn_all.pack(side="left")

        logf = ttk.LabelFrame(root, text="日志")
        logf.pack(fill="both", expand=True, **pad)
        self.log_box = scrolledtext.ScrolledText(logf, height=15, state="disabled",
                                                  font=("Consolas", 9), wrap="word")
        self.log_box.pack(fill="both", expand=True, padx=4, pady=4)

        self.status = tk.StringVar(value="就绪 · 请选择游戏目录")
        ttk.Label(root, textvariable=self.status, relief="sunken", anchor="w").pack(fill="x", side="bottom")

        self.log("ClarityKit 已启动。请选择一个 Unity 游戏根目录。")

    def _info_row(self, parent, name, row):
        ttk.Label(parent, text=name + ":", width=12, anchor="e").grid(row=row, column=0, sticky="e", padx=6, pady=2)
        var = tk.StringVar(value="—")
        ttk.Label(parent, textvariable=var, anchor="w").grid(row=row, column=1, sticky="w", padx=6, pady=2)
        parent.columnconfigure(1, weight=1)
        return var

    def log(self, msg):
        ts = datetime.datetime.now().strftime("%H:%M:%S")
        self.log_box.configure(state="normal")
        self.log_box.insert("end", "[" + ts + "] " + str(msg) + "\n")
        self.log_box.see("end")
        self.log_box.configure(state="disabled")

    def _tlog(self, msg):
        """线程安全日志：从子线程回送到 UI 主线程。"""
        self.root.after(0, lambda: self.log(msg))

    def browse(self):
        d = filedialog.askdirectory(title="选择 Unity 游戏根目录")
        if d:
            self.dir_var.set(os.path.normpath(d))
            self.do_detect()

    def do_detect(self):
        gd = self.dir_var.get().strip()
        if not gd:
            self.log("请先选择游戏目录。")
            return
        self.log("检测中: " + gd)
        try:
            info = detector.detect(gd)
        except Exception as e:
            self.log("检测出错: " + str(e))
            return

        self.info = info
        self.v_backend.set(info["backend"] or "未知")
        self.v_arch.set(info["arch"] or "未知")
        self.v_unity.set(info["unity_version"] or "未知")
        cur = bepinex.inspect_bepinex(gd)
        self.v_bepinex.set(("已装 · " + cur["flavor"]) if cur["installed"] and cur["flavor"] else
                           ("已装" if cur["installed"] else "未安装"))

        if not info["is_unity"]:
            self.status.set("不是 Unity 游戏")
            self.log("× 未识别为 Unity 游戏: " + "; ".join(info["notes"]))
            self._set_ops(False)
            return

        self.log("√ Unity " + str(info["unity_version"]) + " / " + str(info["backend"]) + " / " + str(info["arch"]))
        for n in info["notes"]:
            self.log("  注意: " + n)
        self.status.set(str(info["backend"]) + " · " + str(info["arch"]) + " · Unity " + str(info["unity_version"]))
        self._set_ops(True)

    def _set_ops(self, enabled):
        st = "normal" if (enabled and not self.busy) else "disabled"
        self.btn_bep.configure(state=st)
        self.btn_plugin.configure(state=st)
        self.btn_all.configure(state=st)

    def _run_bg(self, fn):
        """在子线程执行 fn，期间禁用按钮，完成后恢复并刷新检测。"""
        if self.busy or not self.info:
            return
        self.busy = True
        self._set_ops(False)

        def worker():
            try:
                fn()
            except Exception as e:
                self._tlog("× 出错: " + str(e))
            finally:
                self.busy = False
                self.root.after(0, lambda: self._set_ops(True))
                self.root.after(0, self.do_detect)
        threading.Thread(target=worker, daemon=True).start()

    def install_bepinex(self):
        info = self.info
        self._run_bg(lambda: self._do_bepinex(info))

    def _do_bepinex(self, info):
        need, reason = bepinex.needs_install(info["game_dir"], info["backend"])
        self._tlog("BepInEx: " + reason)
        if need:
            bepinex.install(info["game_dir"], info["backend"], log=self._tlog)

    def install_plugin(self):
        info = self.info
        self._run_bg(lambda: self._do_plugin(info))

    def _do_plugin(self, info):
        plugin.install_plugin(info["game_dir"], info["backend"], log=self._tlog,
                              interop_ready=info.get("interop_ready", True))

    def one_click(self):
        info = self.info
        self._run_bg(lambda: self._do_all(info))

    def _do_all(self, info):
        self._tlog("=== 一键去码开始 ===")
        need, reason = bepinex.needs_install(info["game_dir"], info["backend"])
        self._tlog("BepInEx: " + reason)
        if need:
            if not bepinex.install(info["game_dir"], info["backend"], log=self._tlog):
                self._tlog("× BepInEx 安装失败，已中止。")
                return
        # IL2CPP 装完 BepInEx 后 interop 尚未生成，需先运行一次游戏
        if info["backend"] == "IL2CPP" and not info.get("interop_ready"):
            self._tlog("⚠ IL2CPP：BepInEx 已就位，请先启动一次游戏生成 interop，再点「2. 安装去码插件」。")
            return
        ok = plugin.install_plugin(info["game_dir"], info["backend"], log=self._tlog,
                                   interop_ready=info.get("interop_ready", True))
        self._tlog("=== 完成，去码插件已就位 ===" if ok else "=== 插件安装未完成 ===")


def main():
    root = tk.Tk()
    try:
        ttk.Style().theme_use("vista")
    except Exception:
        pass
    ClarityApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
