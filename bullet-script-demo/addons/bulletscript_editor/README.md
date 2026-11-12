# BulletScript Editor (Godot 4 .NET plugin)

A main-screen tab for editing `.bs` files with a live preview.

## Enable
1. Open `bullet-script-demo` in Godot 4 (.NET build).
2. Build once: **Build** button (hammer, top right) or `dotnet build`.
3. *Project > Project Settings > Plugins*: enable **BulletScript Editor** (already listed in `project.godot`).
4. A **BulletScript** tab appears next to 2D / 3D / Script.

## Features
- Syntax highlighting, line numbers, folding. `Ctrl+S` saves.
- **Inline sliders** on stand-alone numeric literals (`speed: 2;`, `LOOP i IN 1 TO 5`, `PHASE p AFTER 400ms`, `VAR n = 12;`).
  Duration and angle literals also get a unit dropdown (`s/ms/f`, `deg/rad`).
  **Right-click a slider** to define its min / max / step (stored in `editor_settings.cfg`).
- **Live preview** recompiled 250 ms after you stop typing. On an error the last good run keeps playing and the line is marked.
- **Pattern picker.** Patterns with parameters prompt for values (any BulletScript expression).
- **Performance monitor** (bullets, frame time, memory) and 3 **simulation presets** (right-click to rename).
- **Live Sync** menu with two toggles:
  - *Sync on save*: `Ctrl+S` / Save notifies the running game.
  - *Sync on edit*: changes that compile are written and pushed as you type.
- **Export JSON** writes the compiled program (same format as the console tool).

## Live Sync in the game
`BulletScriptRunner` has `HotReload` (on by default). When you run the scene from the editor, it polls `res://.bulletscript_sync`
and reloads when the editor touches it for the runner's `SourcePath`.

## Known limits
- One file open at a time. Not hooked into FileSystem-dock double-click.
- Sliders are placed after the end of a line, not inline in the middle of it.
- Comparisons and expressions are not slider-driven, by design.
