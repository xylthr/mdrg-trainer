# MDRG Trainer: Install Guide

An in-game trainer / creative menu for **My Dystopian Robot Girlfriend**. It is a **BepInEx plugin**, so
**BepInEx is required**: it is what loads the trainer into the game. BepInEx is a separate download
(steps below), and this download contains only the trainer plugin.

- **Tested on:** game version 0.97.12, Unity 6000.3.8f1, Windows 64-bit, BepInEx 6.0.0-be.793 (IL2CPP, x64)
- **Single-player only.** Back up your saves before using it (see "Back up your saves" below).
- A game update can break the trainer. If something stops working after an update, wait for a new version.
- **BepInEx only, not MelonLoader.** Some other external mods for this game use MelonLoader, which is a separate system.
  Running both loaders in the same game can cause conflicts, so back up your game folder before mixing them.

---

## 1. Do I already have BepInEx?

Open your game folder (the one with `My Dystopian Robot Girlfriend.exe`).

- You see a **`BepInEx` folder and a `winhttp.dll`** next to the `.exe`: you probably have it. Open
  `BepInEx/LogOutput.log`; the first lines say which version it is. You need **BepInEx 6 IL2CPP**. Go to step 3.
- You see **neither**: do step 2 first.
- BepInEx **5 / Mono** does not work. This game is IL2CPP, so you need BepInEx 6 IL2CPP.

---

## 2. Install BepInEx (skip if you already have it)

1. **Close the game.**
2. Download **`BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793+dbc82f0.zip`** from the official BepInEx builds site:
   [direct link](https://builds.bepinex.dev/projects/bepinex_be/793/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793%2Bdbc82f0.zip)
   (all builds: https://builds.bepinex.dev/projects/bepinex_be).
3. Extract the zip **into your game folder**, next to the `.exe`. Say yes to merging folders.
4. You should now see `winhttp.dll`, `doorstop_config.ini`, a `dotnet` folder and a `BepInEx` folder next to the `.exe`.

---

## 3. Install the trainer

1. Close the game.
2. Copy **`MdrgTrainer.dll`** from this download into the **`BepInEx/plugins/`** folder inside your game folder.
   If the `plugins` folder isn't there yet, create it.
3. **Start the game normally** (the same `.exe` you always use). There is no separate launcher.

```
My Dystopian Robot Girlfriend/
├─ My Dystopian Robot Girlfriend.exe
├─ winhttp.dll              <- BepInEx loader
├─ doorstop_config.ini      <- BepInEx loader settings
├─ .doorstop_version
├─ dotnet/                  <- runtime BepInEx uses
└─ BepInEx/
   ├─ core/
   └─ plugins/
      └─ MdrgTrainer.dll    <- the trainer
```

### The first launch is slow
If BepInEx was just installed, its first launch builds files for your copy of the game (this takes **about a minute**
and the game may look frozen on a black screen). That is normal. **Don't close it.**
Later launches start at the normal speed.

---

## 4. Check that it works

1. Start the game and load a save.
2. You should see **"Trainer - F10 menu"** in the top-left corner.
3. Press **F10**: the trainer window opens.

You can also open `BepInEx/LogOutput.log` and look for:

```
[Info   :MDRG Trainer] MDRG Trainer loaded: F10 opens the menu
```

The menu only shows controls once a save is loaded. On the title screen it says "Load a save to use the trainer."

---

## 5. Using it

| Key | Action |
|---|---|
| **F10** | Open / close the menu |
| F1 | +100,000 money |
| F2 | Refill all needs |
| F3 | Freeze all needs at max (toggle) |
| F4 | +10 casino tokens |
| F5 | Infinite stamina (toggle) |
| F6 | Climax lock (toggle). Switching it off restores your original values |

- Windows can be **dragged** by the title bar and **resized** with the corner grip. Click a window to bring it to the front.
- **Item browser:** search and add any item (mod items included, tagged `[mod]`). Pick an amount (x1, x5, x10, x50).
  Turn on **Custom** color to choose a color for clothing and other colorable items before adding them.
- **Inventory view:** remove items you own, one at a time, a whole stack, or everything the current filter shows.
  **Keep equipped** (on by default) protects whatever she is wearing.
- While the menu is open, the game itself does not react to clicks or keys.

---

## 6. Back up your saves

Editing money, items and stats can't be undone from the menu. Copy this folder somewhere safe first:

```
C:\Users\<you>\AppData\LocalLow\IncontinentCell\My Dystopian Robot Girlfriend\Saves
```

---

## 7. Troubleshooting

**No "Trainer - F10 menu" text**
- Open `BepInEx/LogOutput.log`. If there is no line starting with `[Info   :   BepInEx] Loading [MDRG Trainer`, the plugin is not in `BepInEx/plugins/`.
- If the log doesn't exist at all, BepInEx isn't loading. Check that `winhttp.dll` and `doorstop_config.ini` are next to the game's `.exe` (not in a sub-folder).
- Make sure you started the game with the normal `.exe`, from the folder you installed into.

**Black screen for a long time on first launch**
- Wait about a minute. BepInEx generates files on its first run only.

**Antivirus flags `winhttp.dll`**
- It is BepInEx's loader (Unity Doorstop), which works by proxying that DLL, and some antivirus tools flag that. It comes from the official BepInEx project. You can add the game folder to your exclusions.

**It worked, then stopped after a game update**
- The trainer reads the game's internals, so updates can break it. Remove `MdrgTrainer.dll` from `BepInEx/plugins/` and wait for an updated version. If the game won't start, also delete the folder `BepInEx/interop/` so it is rebuilt on the next launch.

**Item list is empty or short at the title screen**
- Normal. The full list fills in after you load a save (reopen the browser or press **Reload**).

**Something else**
- Send `BepInEx/LogOutput.log` when asking for help. It shows exactly what the trainer did.

---

## 8. Uninstall

- **Just the trainer:** delete `BepInEx/plugins/MdrgTrainer.dll`.
- **Turn off all BepInEx mods without deleting anything:** rename `winhttp.dll` to `winhttp.dll.off`.
- **Remove BepInEx completely** (only if no other mod needs it): delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `changelog.txt`, the `dotnet/` folder and the `BepInEx/` folder.

---

## Credits

- **BepInEx** (https://github.com/BepInEx/BepInEx): the mod loader, downloaded separately from https://builds.bepinex.dev/projects/bepinex_be.
- Uses HarmonyX and Il2CppInterop, which are part of BepInEx.
- Trainer by **Xyl'thr**. Not affiliated with the game's developer.
