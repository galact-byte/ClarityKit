# ClarityKit

A configurable BepInEx plugin engine that removes real-time overlay **filter layers** and **pixelation shader effects** in Unity games — for both the **Mono** and **IL2CPP** scripting backends.

ClarityKit does not modify any game asset. It works purely at runtime: it scans the live scene for objects/materials matching configurable keywords, then either hides those objects or neutralizes their pixelation shader parameters. It relies only on stable `UnityEngine` core types, so the same approach generalizes across many games.

> **Scope**: effective only when the effect is a **runtime overlay or shader** (no image reconstruction needed). Effects baked directly into source textures are out of scope.

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
