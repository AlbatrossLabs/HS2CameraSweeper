# Camera Sweep (HS2 Studio)

BepInEx plugin for Honey Select 2 Studio (StudioNEOV2). Set up a scene and cameras, press one key, and get a render of every camera. Optionally repeat the whole set for each outfit and each facial expression.

## Use

- **Ctrl+F10** runs a sweep. **Esc** cancels after the current shot; the scene is put back either way.
- **Shift+F10**, or the camera icon on Studio's left toolbar, opens the window: camera source, variations, file prefix, Run/Cancel, progress, "open last output folder".
- Every setting is also in **F1** (Configuration Manager) under *Camera Sweep*.

**Camera sources**
- *Slots*: the 10 numbered camera buttons, plus MoarCamz's extra slots (11+) when MoarCamz is installed. Slots still holding the default view, cleared slots, and duplicates of an earlier slot are skipped (toggle in the window). MoarCamz lock-on slots are shot from their saved position; lock-on is not re-engaged.
- *Objects*: cameras added with Add > Camera. Named cameras use their name in the filename.
- *Both*: all of the above.

**Turning cameras off**: the window lists every camera it found with a tick box. Untick a camera and the sweep skips it. *All on* ticks them all again. The choices are saved in the scene file, so each scene keeps its own list. Cameras are listed by label (`slot03`, `cam_Name`); naming camera objects in the workspace makes them easier to tell apart.

**Output**: `UserData/cap/CameraSweep/<yyyyMMdd-HHmmss>/` by default, one folder per run (*Folder per run* off writes straight into the output folder and never overwrites existing files).

**File names** come from a template, set in the window or F1 (*Output > File name template*). The window shows an example of the first file name as you type.

| Token | Value |
|---|---|
| `{prefix}` | the *File prefix* box |
| `{scene}` | name of the scene file last loaded or saved (`untitled` for a new scene) |
| `{camera}` | `slot03`, `cam_Name` |
| `{n}` | shot number in this run, zero-padded (`001`) |
| `{date}` `{time}` | when the run started (`20261112`, `235501`) |
| `{character}` | the swapped-in character when swapping, otherwise the first ticked character |
| `{swap}` `{outfit}` `{clothes}` `{face}` `{extras}` | the current value of that variation (card name or preset name) |
| `{axes}` | every variation in use as `outfit-casual_face-smile` |

A token for a variation that's off is left out and the separators around it are tidied (`a__b` becomes `a_b`). `/` makes subfolders. Default: `{prefix}_{axes}_{camera}`. A Ren'Py-style example: `{character}/{character}_{outfit}_{face}`. Names that would repeat get `_2`, `_3`.

Capture goes through BepisPlugins **Screencap** when present, so resolution, upsampling, DOF handling and alpha match a normal render shot (Width/Height/Upsampling = 0 means "use Screencap's settings"). Both Screencap r19.x (BetterRepack R15 ships 19.5) and r20+ are supported. Output is always PNG. Without Screencap it falls back to rendering the main camera directly.

## Video mode

Switch **Output** to *Video* in the window (or *Video > Output mode* in F1). Each camera then gets a video of one character's animation instead of a still.

- **Character**: the arrows pick a character in the scene; by default it uses whatever character is selected in the workspace when you start.
- **Cycles**: how many loops of that character's current animation each video lasts (fractions allowed).
- **Prewarm**: loops played before recording starts, so physics settles and every video starts exactly on a loop boundary. Videos loop cleanly.

Recording is done by the **VideoExport** plugin (included in BetterRepack). Camera Sweep selects the character, temporarily sets VideoExport to "Limit by: Animation" with your cycle counts, records, puts VideoExport's own settings back, and moves the finished video into the run folder as `<prefix>_<variations>_<camera>.mp4`. Framerate, format (MP4/WebM/GIF/AVI), quality and resize are VideoExport's settings: open its window with its hotkey (Ctrl+E in the current install).

VideoExport locks the game clock to the video framerate while recording, so motion is smooth no matter how slow each frame renders. The animation must be playing (not paused) when you start. Esc cancels after VideoExport stops the current recording.

Variations work in video mode too: e.g. 3 cameras x 2 outfits = 6 videos.



Loop order: character swap → outfit → clothes state → face → face extras → cameras. Each value is applied once and everything inside it is cycled before moving on.

| Axis | What it does | Settings |
|---|---|---|
| Character swap | Replaces the character picked in the window (`<` `>`) with each character card in a folder, in the same place and pose (Studio's own *Change character*). The original is saved to a temporary card first and put back afterward. Cards of the other sex are skipped | Folder: absolute, or relative to `UserData`. Default `chara/female/CameraSweep` |
| Outfit | Loads each coordinate card from a folder onto the characters ticked under *Characters* | Folder: absolute, or relative to `UserData`. Default `coordinate/female/CameraSweep` |
| Clothes state | Cycles on / half / off per clothing part on the ticked characters. Each preset starts from how they were dressed when the sweep began | `name=part:state,...; ...` Parts: top, bottom, bra, panties, gloves, pantyhose, socks, shoes, all. States: on, half, off. Default: `halfTop=top:half; halfBottom=bottom:half; topOff=top:off; bottomOff=bottom:off; topBottomOff=top:off,bottom:off` |
| Expression | Cycles named face presets | `name=eyebrow,eyes,mouth; ...` Numbers match Studio's Face dropdowns, first entry = 0. `-1` leaves that part alone |
| Face extras | Blush, tears, where the eyes and head look, eye opening, mouth opening (Studio's Anim panels) | Rows of `<` `>` steppers in the window; *+ From current* copies the first ticked character. Blush, tears and opening go 0 to 1 in 0.25 steps; eye/neck look are the position in Studio's list (first = 0). `-` = leave as it was |

**Editing presets**: clothes and face presets are edited in the window, no typing needed.
- *Clothes*: one row per shot. Click a cell to cycle `-` (leave as it was) → On → Half → Off. *+ Add shot*, *x* to delete, *Defaults* to reset, *All parts* to show gloves/pantyhose/socks/shoes.
- *Face*: one row per shot with `<` `>` steppers for eyebrows, eyes and mouth (same numbers as Studio's Face dropdowns, `-` = leave as it was). *+ From current face* copies whatever the first ticked character is showing right now, so you can set a face in Studio and capture it as a preset.
- Both are still stored as text in F1, so either place works.

**Estimate**: above the Run button the window shows how many images (or videos) the current settings make, with the breakdown (`3 cameras × 2 outfit × 5 clothes`), size per image and in total, time per image and in total, and free space on the output drive (red if it won't fit). The first numbers are rough guesses; after each sweep Camera Sweep records your real bytes-per-pixel and seconds-per-shot (F1 > *Stats (measured)*) and uses those, scaled for resolution and upsampling changes. Video estimates appear after your first video sweep.

**Characters**: when a variation is on, the window lists every character with a tick box and shows how many will be changed. Women start ticked and men unticked, since coordinate cards are made for one sex. Quick buttons: *Women*, *All*, *None*, *Selected* (whoever is selected in the workspace). Both variations use this list, and it's saved with the scene.

Total shots = cameras that are on × swap cards × outfits × clothing states × face presets × face extras (only the variations that are on). The count is logged before the run starts.

### Adding an axis (time of day, pose, lighting...)

1. Create `src/Axes/YourAxis.cs` implementing `IVariationAxis`: `Prepare()` snapshots current state and returns the values, `Apply()` sets one, `Restore()` puts the snapshot back.
2. Add `Axes.Add(new YourAxis(Config));` in `CameraSweepPlugin.Awake()`. Position in the list = nesting order.

Runner, filenames, window and restore all pick it up automatically. For time of day: open `Assembly-CSharp.dll` in dnSpy, find what the Studio map/sun panel calls when you change it, and call that from `Apply()`.

## Build

Requires the .NET SDK (6 or newer). References come from the IllusionMods/BepInEx NuGet feeds in `nuget.config`, so you don't need to point at `StudioNEOV2_Data\Managed`.

v0.6.0 is built against the updated install: BepInEx 5.4.23.5, Modding API (HS2API) 1.48, Screencap 21.1.4, VideoExport 2.1.1. It also supports Screencap 19.x.

```
dotnet build -c Release
```

Output: `bin\Release\HS2CameraSweep.dll`. The project's `HS2Dir` is set to `X:\HS2 RX15`, so every build also copies the DLL to `X:\HS2 RX15\BepInEx\plugins\HS2CameraSweep\`. Point it elsewhere with `-p:HS2Dir="D:\Other\HS2"`, or `-p:HS2Dir=` to skip the copy.

Requires HS2API (KKAPI for HS2) in the game, which BetterRepack includes. Screencap is optional.

## Dev tips

- **ScriptEngine** (BepInEx.Debug): drop the DLL in `BepInEx\scripts` and press Ctrl+Del in Studio to reload without restarting. Each reload may add another toolbar icon; the hotkeys are the reliable way to test.
- **RuntimeUnityEditor**: inspect `Studio.Studio.Instance` live (`sceneInfo.cameraData`, `dicObjectCtrl`, `ociCamera`).
- Log: `BepInEx\LogOutput.log`. On-screen messages come from `Logger.LogMessage`.

## Known limits

- **Outfit axis bypasses KKAPI's coordinate-load events.** Vanilla clothing and accessories change; data stored by other plugins on the coordinate card (overlays, MoreAccessories extras, etc.) may not. Restore puts the vanilla outfit back the same way. If you rely on those plugins, the next step is routing the load through Studio's own coordinate-load path so KKAPI fires its events.
- Coordinate cards are sex-specific; point the folder at cards for the targeted characters.
- Not every garment has a half state. A preset that asks for half on one that doesn't leaves that part as it was and logs a message, rather than letting the game jump it to off.
- One-piece outfits are linked by the game: taking the top off also takes the bottom off.
- Character swap restores the original from a card it saves at the start (in `BepInEx\cache\CameraSweep`, deleted afterward). Plugin data that isn't stored on character cards (e.g. some Studio-only per-character settings) may not survive the round trip.
- `{scene}` only knows scenes loaded or saved while Camera Sweep is running; a scene loaded before the plugin started shows as `untitled` until you load or save again.
- Studio animation or other plugins that drive the face every frame can override expression presets. Pause animation if faces don't stick.
- Built-in fallback renderer has no alpha and no upsampling.
- If Studio's camera hasn't fully settled (heavy DOF, physics), raise *Settle frames* in F1.

## Source map

```
src/CameraSweepPlugin.cs      entry point, config, hotkeys, toolbar button
src/Sweep/SweepRunner.cs      the loop: axes × cameras, snapshot + restore
src/Sweep/CameraShots.cs      saved-slot and camera-object shots, unset-slot detection
src/Sweep/MoarCamzSlots.cs    MoarCamz extra slots (reflection, optional)
src/Sweep/SweepSelection.cs   camera on/off and character choices, saved with the scene
src/Sweep/Estimate.cs         image count, size and time estimate; learns from each run
src/Sweep/NameTemplate.cs     file-name template, {scene} capture
src/Axes/CharacterSwapAxis.cs character swap
src/Axes/FaceExtrasAxis.cs    blush, tears, eye/neck look, eye/mouth opening
src/Capture/ScreencapBridge.cs  reflection bridge to Screencap r19 / r20+
src/Capture/ShotWriter.cs     capture, GPU readback, PNG write, fallback renderer
src/Capture/VideoExportBridge.cs  drives VideoExport per camera (reflection, optional)
src/Axes/*.cs                 variation axes
src/UI/*.cs                   IMGUI window, generated toolbar icon
```
