# Changelog — ClarityKit

## Packaging

### v0.5.2
- Added `build/package.ps1` to produce a clean portable zip containing the runnable GUI tool, `start.bat`, `README.md`, and required `tool/assets`.
- Added `.github/workflows/package.yml` to run Python syntax checks, upload the portable zip as a GitHub Actions artifact, and create a GitHub Release with the zip when a `v*` tag is pushed.
- Documented the portable package flow and IL2CPP first-run requirement in `README.md`.
- Added `dist/` to `.gitignore` so generated zip files are not committed.
- Added the MIT `LICENSE` file and included it in portable zip packages.
- Verified with `python` AST syntax checks and a local `build/package.ps1` package build.

## Plugin engine

### v0.5.1
Two real-device bugs fixed (both root-caused from a target game's BepInEx log):
- **Critical: `SplitCsv` crashed on Unity 2020 / older Mono.** `csv.Split(',')` compiled to the .NET Standard 2.1-only `Split(char, StringSplitOptions)` overload, which is missing at runtime on older Mono → `MissingMethodException` in the `Settings` constructor → the whole plugin's Awake died. (This also explains an earlier symptom: "plugin loaded but emitted none of its own logs" — it had been crashing here the whole time.) Switched to `Split(new char[] { ',' })` (`Split(params char[])`, present on every runtime).
- **Perf: `HideMosaicRenderers` now defaults to off.** It accumulated matched Renderers into a set re-suppressed every frame, but games keep spawning new mosaic meshes → unbounded growth → per-frame cost climbs until the game freezes. The default now relies on the O(1) shared-material param reset; enable only for sprite-plate mosaics where material reset is insufficient.

### v0.5.0
Fixed a structural blind spot: "screen-space / full-screen post" style mosaics were never removed. The old engine only scanned `Renderer.sharedMaterials`, but HDRP/URP games can drive the mosaic from a material that is held by a `CustomPassVolume` full-screen pass — or otherwise not bound to any active `Renderer` — with no `Shader.Find` entry point and no controller class to Harmony-patch, so it fell outside the scan. Added:
- Strategy B+ (ScanAllMaterials): scan `Resources.FindObjectsOfTypeAll<Material>()`, covering materials not attached to any Renderer (post/full-screen materials).
- Strategy B2 (HideMosaicRenderers): disable (`enabled=false`) any Renderer whose material shader name matches a keyword (mosaic-plate meshes).
- Strategy D (DisableCustomPasses): reflectively disable HDRP/URP CustomPassVolume full-screen passes whose material matches — pure reflection, no hard HDRP dependency, so one dll stays cross-game.
- DumpScene upgrade: dump all shaders / all materials (name|shader) / CustomPass volumes, plus per-property values for matched materials, so adapting to a new game is data-driven instead of guesswork.
- Awake wrapped in try/catch with explicit error logging; scan logs now report per-strategy hit counts.
- Extra pixelation param-name candidates in the shared keyword module.

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
- `mosaic_probe.py` — **pre-install static verdict** on the censorship mechanism: runtime shader / Live2D filter layer (removable) vs baked-into-CG / uncensored (not removable), and reports the matched shader name (e.g. `Shader Graphs/Mosaic`). Prevents the "installed but nothing happened" confusion. Verdicts validated on four real samples (two removable runtime-shader games, two baked-art games). The GUI shows a "mosaic mechanism" row after detection and logs removability + guidance.
- `bepinex.py` — detect existing BepInEx; install via bundled offline templates (Mono BE5 ~1.9MB / IL2CPP BE6 ~80MB); proxy download fallback planned.
- `plugin.py` — Mono: copy the prebuilt dll; IL2CPP: on-site `dotnet build` against the target game's own interop.
- `app.py` — tkinter GUI; install actions run on a worker thread with thread-safe logging.
- `make_templates.py` — extract clean BepInEx templates from reference installs.
- `launch.py` / `start.bat` — launcher with Python/tkinter checks; dotnet is optional (only the IL2CPP plugin compile needs it).

## Notes
- Bundled templates live under `tool/templates/` (git-ignored due to size); ship them with the tool or regenerate via `make_templates.py`.
- Net-new generalization (games never used as design references) is still pending real-world validation.
