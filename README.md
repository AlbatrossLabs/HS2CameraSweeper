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

**Output**: `UserData/cap/CameraSweep/<yyyyMMdd-HHmmss>/` by default, one folder per run.
Filenames: `<prefix>_<outfit-x>_<face-y>_<slot03 | cam_Name>.png`

Capture goes through BepisPlugins **Screencap** when present, so resolution, upsampling, DOF handling and alpha match a normal render shot (Width/Height/Upsampling = 0 means "use Screencap's settings"). Both Screencap r19.x (BetterRepack R15 ships 19.5) and r20+ are supported. Output is always PNG. Without Screencap it falls back to rendering the main camera directly.

## Video mode

Switch **Output** to *Video* in the window (or *Video > Output mode* in F1). Each camera then gets a video of one character's animation instead of a still.

- **Character**: the arrows pick a character in the scene; by default it uses whatever character is selected in the workspace when you start.
- **Cycles**: how many loops of that character's current animation each video lasts (fractions allowed).
- **Prewarm**: loops played before recording starts, so physics settles and every video starts exactly on a loop boundary. Videos loop cleanly.

Recording is done by the **VideoExport** plugin (included in BetterRepack). Camera Sweep selects the character, temporarily sets VideoExport to "Limit by: Animation" with your cycle counts, records, puts VideoExport's own settings back, and moves the finished video into the run folder as `<prefix>_<variations>_<camera>.mp4`. Framerate, format (MP4/WebM/GIF/AVI), quality and resize are VideoExport's settings: open its window with Shift+V+E.

VideoExport locks the game clock to the video framerate while recording, so motion is smooth no matter how slow each frame renders. The animation must be playing (not paused) when you start. Esc cancels after VideoExport stops the current recording.

Variations work in video mode too: e.g. 3 cameras x 2 outfits = 6 videos.

<img width="1127" height="635" alt="image" src="https://github.com/user-attachments/assets/05ac5251-609f-423a-8617-b196bcef82c4" />


Loop order: outfit → expression → cameras. Each outfit is loaded once, then every expression, then every camera.

| Axis | What it does | Settings |
|---|---|---|
| Outfit | Loads each coordinate card from a folder onto the selected characters (all characters if none selected) | Folder: absolute, or relative to `UserData`. Default `coordinate/female/CameraSweep` |
| Expression | Cycles named face presets | `name=eyebrow,eyes,mouth; ...` Numbers match Studio's Face dropdowns, first entry = 0. `-1` leaves that part alone |

Total shots = cameras × outfits × presets. The count is logged before the run starts.

### Adding an axis (time of day, pose, lighting...)

1. Create `src/Axes/YourAxis.cs` implementing `IVariationAxis`: `Prepare()` snapshots current state and returns the values, `Apply()` sets one, `Restore()` puts the snapshot back.
2. Add `Axes.Add(new YourAxis(Config));` in `CameraSweepPlugin.Awake()`. Position in the list = nesting order.

Runner, filenames, window and restore all pick it up automatically. For time of day: open `Assembly-CSharp.dll` in dnSpy, find what the Studio map/sun panel calls when you change it, and call that from `Apply()`.

## Build

Requires the .NET SDK (6 or newer). References come from the IllusionMods/BepInEx NuGet feeds in `nuget.config`, so you don't need to point at `StudioNEOV2_Data\Managed`.

v0.2.0 was compiled and verified against the HS2 BetterRepack R15 install (Assembly-CSharp, HS2API, BepInEx 5).

```
dotnet build -c Release
```

Output: `bin\Release\HS2CameraSweep.dll`. Copy it to `<HS2>\BepInEx\plugins\HS2CameraSweep\`, or let the build do it:

```
dotnet build -c Release -p:HS2Dir="D:\Path\To\HoneySelect2"
```

Requires HS2API (KKAPI for HS2) in the game, which BetterRepack includes. Screencap is optional.

## Dev tips

- **ScriptEngine** (BepInEx.Debug): drop the DLL in `BepInEx\scripts` and press Ctrl+Del in Studio to reload without restarting. Each reload may add another toolbar icon; the hotkeys are the reliable way to test.
- **RuntimeUnityEditor**: inspect `Studio.Studio.Instance` live (`sceneInfo.cameraData`, `dicObjectCtrl`, `ociCamera`).
- Log: `BepInEx\LogOutput.log`. On-screen messages come from `Logger.LogMessage`.

## Known limits (v0.1)

- **Outfit axis bypasses KKAPI's coordinate-load events.** Vanilla clothing and accessories change; data stored by other plugins on the coordinate card (overlays, MoreAccessories extras, etc.) may not. Restore puts the vanilla outfit back the same way. If you rely on those plugins, the next step is routing the load through Studio's own coordinate-load path so KKAPI fires its events.
- Coordinate cards are sex-specific; point the folder at cards for the targeted characters.
- Studio animation or other plugins that drive the face every frame can override expression presets. Pause animation if faces don't stick.
- Built-in fallback renderer has no alpha and no upsampling.
- If Studio's camera hasn't fully settled (heavy DOF, physics), raise *Settle frames* in F1.

## Source map

```
src/CameraSweepPlugin.cs      entry point, config, hotkeys, toolbar button
src/Sweep/SweepRunner.cs      the loop: axes × cameras, snapshot + restore
src/Sweep/CameraShots.cs      saved-slot and camera-object shots, unset-slot detection
src/Sweep/MoarCamzSlots.cs    MoarCamz extra slots (reflection, optional)
src/Capture/ScreencapBridge.cs  reflection bridge to Screencap r19 / r20+
src/Capture/ShotWriter.cs     capture, GPU readback, PNG write, fallback renderer
src/Capture/VideoExportBridge.cs  drives VideoExport per camera (reflection, optional)
src/Axes/*.cs                 variation axes
src/UI/*.cs                   IMGUI window, generated toolbar icon
```
