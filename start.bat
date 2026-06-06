@echo off
chcp 65001 >nul 2>&1
title ClarityKit
python "%~dp0tool\launch.py"
if errorlevel 1 pause
