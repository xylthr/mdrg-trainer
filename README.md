# MDRG Trainer

An in-game creative menu for **My Dystopian Robot Girlfriend**. Press **F10** in game.

It is a [BepInEx](https://github.com/BepInEx/BepInEx) plugin (IL2CPP), so **BepInEx is required**.

## Features

- **Item browser:** every item in the game, mod items included (tagged `[mod]`), with search, categories and an amount selector (x1, x5, x10, x50)
- **Colors:** pick a color (palette or RGB sliders, per color slot) before adding clothing and other colorable items
- **Inventory view:** remove items you own, one at a time, a whole stack, or everything the current filter shows. **Keep equipped** protects what she is wearing
- **Money, needs and stats:** money, casino tokens, health, stamina, food, mind, mood, intelligence, sympathy, followers, subs
- **Toggles:** freeze needs, infinite stamina, and a climax lock that restores your original values when switched off
- **Windows:** draggable and resizable, layered properly, with animations. The game itself ignores input while the menu is open
- Styled to match the game's own UI

## Install

This download is **only the plugin**. BepInEx is a separate download.

1. **Get BepInEx 6 IL2CPP** (skip if you already have it): download
   [`BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793+dbc82f0.zip`](https://builds.bepinex.dev/projects/bepinex_be/793/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793%2Bdbc82f0.zip)
   from the official BepInEx builds site and extract it into the game folder, next to `My Dystopian Robot Girlfriend.exe`.
2. **Get the plugin:** download `MDRG-Trainer-<version>-plugin-only.zip` from the [Releases](../../releases/latest) page and put
   `MdrgTrainer.dll` into the game's `BepInEx/plugins/` folder.
3. Start the game. The first launch after installing BepInEx takes about a minute. Press **F10** in game.

Full steps, troubleshooting and uninstall are in [docs/INSTALL.md](docs/INSTALL.md).

## Hotkeys

| Key | Action |
|---|---|
| F10 | Open / close the menu |
| F1 | +100,000 money |
| F2 | Refill all needs |
| F3 | Freeze all needs at max |
| F4 | +10 casino tokens |
| F5 | Infinite stamina |
| F6 | Climax lock |

## Compatibility

Built and tested on game version 0.97.12 (Unity 6000.3.8f1), Windows 64-bit, BepInEx 6.0.0-be.793 (IL2CPP, x64).
The plugin reads the game's internals, so a game update can break it.

**BepInEx only.** This is not a MelonLoader mod. Some other external mods for this game load through MelonLoader, which is a separate system.
Running both loaders in the same game can cause conflicts, so back up your game folder before mixing them.

## Build from source

You need the [.NET SDK](https://dotnet.microsoft.com/download) (6 or newer) and a game folder that has BepInEx 6 IL2CPP installed and has been started once.
That first launch generates `BepInEx/interop/`, which the project compiles against. Those files come from the game itself, so they are not part of this repository.

```
dotnet build src/MdrgTrainer.csproj -c Release -p:GameDir="D:\Games\My Dystopian Robot Girlfriend"
```

The result is `src/bin/MdrgTrainer.dll`. Copy it into the game's `BepInEx/plugins/` folder.

### Notes for contributors

This game's Unity build strips several IMGUI methods (`GUILayout.BeginArea`, `GUI.TextField`, `GUIStyle` skin helpers and others throw
"Method unstripping failed"), so the whole UI is drawn by hand from rectangles, labels and a couple of generated textures, with its own
click, drag and keyboard handling. Keep that in mind before reaching for the usual IMGUI helpers.

## Reporting a problem

[Open an issue](../../issues/new/choose) and attach `BepInEx/LogOutput.log`. It shows exactly what the trainer did.

Back up your saves before using it: `C:\Users\<you>\AppData\LocalLow\IncontinentCell\My Dystopian Robot Girlfriend\Saves`.

## Credits

Made by **Xyl'thr**. Built on BepInEx, HarmonyX and Il2CppInterop. Not affiliated with the game's developer.
