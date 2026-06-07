# ClarityKit

A configurable BepInEx plugin engine that removes real-time overlay **filter layers** and **pixelation shader effects** in Unity games — for both the **Mono** and **IL2CPP** scripting backends.

ClarityKit does not modify any game asset. It works purely at runtime: it scans the live scene for objects/materials matching configurable keywords, then either hides those objects or neutralizes their pixelation shader parameters. It relies only on stable `UnityEngine` core types, so the same approach generalizes across many games.

> **Scope**: effective only when the effect is a **runtime overlay or shader** (no image reconstruction needed). Effects baked directly into source textures are out of scope.

## Compatibility

**Works when:**
- The game is built with **Unity** — either the **Mono** or **IL2CPP** scripting backend
- The effect is applied at **runtime** as a separate overlay layer or a pixelation **shader** (rendered on top of the underlying content)

**Won't help when:**
- The effect is **baked into the source textures** — recovering that would need AI inpainting, which this tool does not do
- The game isn't Unity, or its code is heavily obfuscated / its assets are encrypted
- *(IL2CPP only)* you can't run the game at least once to let BepInEx generate the `interop` assemblies

## First-time setup — generate BepInEx templates

This repo does **not** bundle BepInEx itself (it would be large, and should come from your own games rather than be redistributed). Before the GUI's **"Install BepInEx"** step can work, generate the templates **once** from your own reference games:

```powershell
python tool/make_templates.py "<a Mono game root>" "<an IL2CPP game root>"
```

The Mono reference game must already have **BepInEx 5** installed, the IL2CPP one **BepInEx 6**. This produces `tool/templates/` locally (git-ignored, never published).

> Alternatively, just install BepInEx into your target game manually and use only the plugin-install step — templates are needed *only* for the automated BepInEx installer.

## Two flavors

| Backend | Project | BepInEx | Target |
|---|---|---|---|
| Mono | `src/ClarityKit.Mono` | 5.x | `netstandard2.1` — one dll works across Mono games |
| IL2CPP | `src/ClarityKit.IL2CPP` | 6.x | `net6.0` — compiled per target game against its own `interop` |

IL2CPP `interop` assemblies are specific to each game build and Unity version, so the IL2CPP flavor is **compiled on demand** against the target game. Run the game once first so BepInEx generates `BepInEx/interop`.

## Strategies (all configurable)

- **A — Hide by name**: GameObjects whose name matches a keyword are set inactive.
- **B — Patch shader**: materials/shaders whose name matches a keyword get their pixelation parameters (`_Pixelation`, `_MosaicSize`, ...) set to a near-zero value.
- **C — Diagnostics**: dumps all shader names plus suspect material/object names to `BepInEx/ClarityKit_dump.txt`, to help discover the right keywords for a new game.

## Build & install

Mono game:

```powershell
pwsh build/build-mono.ps1 -GameDir "X:\path\to\MonoGameRoot"
```

IL2CPP game (launch the game once first to generate interop):

```powershell
pwsh build/build-il2cpp.ps1 -GameDir "X:\path\to\Il2CppGameRoot"
```

Each script compiles the plugin against the target game and copies the dll into its `BepInEx/plugins`.

## Configuration

After the first launch, edit `BepInEx/config/com.clarity.kit.*.cfg`:

- toggle strategies A / B / C
- adjust the keyword and pixelation-parameter lists
- set the deny-list to avoid false positives
- tune the rescan interval

## Adapting to a new game

1. Enable `Diagnostics > DumpScene`, launch, reach the relevant scene.
2. Inspect `ClarityKit_dump.txt` for the effect's shader / material / object names.
3. Add the discovered keywords to the config and verify.

## Requirements

- .NET SDK (for `dotnet build`)
- BepInEx already installed in the target game (5.x for Mono, 6.x for IL2CPP)
