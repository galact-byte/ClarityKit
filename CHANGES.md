# Changelog — ClarityKit

## Plugin engine

### v0.4.0
Fixed the Mono build not running at all. Root cause: in this environment `BaseUnityPlugin` itself receives no Unity messages (Update/LateUpdate/render events; only Awake runs). Switched to a dedicated GameObject hosting a runner component (same pattern as the IL2CPP build). Scanning is driven by LateUpdate; suppression runs in the pre-render callback (`beginCameraRendering`/`onPreCull`), guaranteeing it runs after the game re-activates layers each frame and before rendering. Verified on the Mono sample. Logging quieted to hits-only.

### v0.3.0
Render-callback-driven scanning + probe logging (used to locate the lifecycle issue).

### v0.2.0
Suppression moved to LateUpdate (insufficient — LateUpdate wasn't firing then).

### v0.1.0
Initial skeleton: Mono + IL2CPP dual-backend overlay-filter remover; three strategies (hide-by-name / shader pixelation reset / diagnostics dump); shared keyword module; on-site build scripts.

## GUI one-click tool (complete)

Flow: pick game directory → detect → install BepInEx → install the filter-remover plugin, with a live log. tkinter GUI, zero runtime deps.

- `detector.py` — Unity backend (Mono/IL2CPP), architecture, Unity version, key paths.
- `bepinex.py` — detect existing BepInEx; install via bundled offline templates (Mono BE5 ~1.9MB / IL2CPP BE6 ~80MB); proxy download fallback planned.
- `plugin.py` — Mono: copy the prebuilt dll; IL2CPP: on-site `dotnet build` against the target game's own interop.
- `app.py` — tkinter GUI; install actions run on a worker thread with thread-safe logging.
- `make_templates.py` — extract clean BepInEx templates from reference installs.
- `launch.py` / `start.bat` — launcher with Python/tkinter checks; dotnet is optional (only the IL2CPP plugin compile needs it).

## Notes
- Bundled templates live under `tool/templates/` (git-ignored due to size); ship them with the tool or regenerate via `make_templates.py`.
- Net-new generalization (games never used as design references) is still pending real-world validation.
