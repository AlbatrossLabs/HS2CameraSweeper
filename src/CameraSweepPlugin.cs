using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HS2CameraSweep.Axes;
using HS2CameraSweep.Capture;
using HS2CameraSweep.Sweep;
using HS2CameraSweep.UI;
using KKAPI;
using KKAPI.Studio;
using UnityEngine;

namespace HS2CameraSweep
{
    public enum CameraSource
    {
        SavedSlots,
        CameraObjects,
        Both,
    }

    public enum OutputMode
    {
        Images,
        Video,
    }

    public enum CaptureEngine
    {
        Screencap,
        BuiltIn,
    }

    public enum ImageFormat
    {
        Png,
        WebP,
        WebPLossy,
        Jpg,
    }

    [BepInPlugin(Guid, PluginName, Version)]
    [BepInProcess("StudioNEOV2")]
    [BepInDependency(KoikatuAPI.GUID)]
    [BepInDependency(ScreencapBridge.ScreencapGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(VideoExportBridge.VideoExportGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class CameraSweepPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.albatrosslabs.hs2.camerasweep";
        public const string PluginName = "Camera Sweep";
        public const string Version = "0.9.6";

        internal static new ManualLogSource Logger;
        internal static CameraSweepPlugin Instance;

        // General
        internal ConfigEntry<KeyboardShortcut> RunHotkey;
        internal ConfigEntry<KeyboardShortcut> CancelHotkey;
        internal ConfigEntry<KeyboardShortcut> WindowHotkey;
        internal ConfigEntry<CameraSource> Source;
        internal ConfigEntry<bool> SkipUnsetSlots;
        internal ConfigEntry<int> SettleFrames;
        internal ConfigEntry<bool> CoverScreen;
        internal ConfigEntry<bool> PauseMotion;

        // Output
        internal ConfigEntry<string> OutputFolder;
        internal ConfigEntry<string> FilePrefix;
        internal ConfigEntry<int> Width;
        internal ConfigEntry<int> Height;
        internal ConfigEntry<int> Upsampling;
        internal ConfigEntry<bool> Transparent;
        internal ConfigEntry<string> NameTemplateText;
        internal ConfigEntry<bool> FolderPerRun;
        internal ConfigEntry<ImageFormat> Format;
        internal ConfigEntry<int> Quality;
        internal ConfigEntry<int> WebpEffort;

        // Render
        internal ConfigEntry<CaptureEngine> Engine;
        internal ConfigEntry<int> ShadowResolution;
        internal ConfigEntry<ShadowCascadeSetting> ShadowCascades;
        internal ConfigEntry<AmbientOcclusionSetting> AmbientOcclusion;

        // Video
        internal ConfigEntry<OutputMode> Mode;
        internal ConfigEntry<float> VideoCycles;
        internal ConfigEntry<int> VideoPrewarm;

        // Measured by each sweep; used by the window's size/time estimate.
        internal ConfigEntry<float> StatBytesPerPixel;
        internal ConfigEntry<float> StatBytesPerPixelAlpha;
        internal ConfigEntry<string> StatBytesPerPixelOther;
        internal ConfigEntry<float> StatSecondsPerShot;
        internal ConfigEntry<float> StatWorkMegapixels;
        internal ConfigEntry<float> StatVideoBytesPerCycle;
        internal ConfigEntry<float> StatVideoSecondsPerCycle;

        /// <summary>Character whose animation drives video length. Null = use the selection when the sweep starts.</summary>
        internal Studio.OCIChar VideoTarget;

        internal SweepRunner Runner;
        private SweepWindow _window;

        private void Awake()
        {
            Logger = base.Logger;
            Instance = this;

            RunHotkey = Config.Bind("General", "Run sweep", new KeyboardShortcut(KeyCode.F10, KeyCode.LeftControl),
                "Capture every camera (and every enabled variation) in the current scene.");
            CancelHotkey = Config.Bind("General", "Cancel sweep", new KeyboardShortcut(KeyCode.Escape),
                "Stops a running sweep after the current shot and restores the scene.");
            WindowHotkey = Config.Bind("General", "Toggle window", new KeyboardShortcut(KeyCode.F10, KeyCode.LeftShift),
                "Shows the Camera Sweep window. The camera icon on Studio's left toolbar does the same.");
            Source = Config.Bind("General", "Camera source", CameraSource.SavedSlots,
                "SavedSlots = the 10 numbered camera buttons. CameraObjects = cameras added with Add > Camera. Both = all of them.");
            SkipUnsetSlots = Config.Bind("General", "Skip unset slots", true,
                "Skip numbered camera slots that still hold the default view, and slots that duplicate an earlier one.");
            SettleFrames = Config.Bind("General", "Settle frames", 3,
                new ConfigDescription("Frames to wait after changing the camera/outfit/expression before capturing.",
                    new AcceptableValueRange<int>(1, 120)));

            CoverScreen = Config.Bind("General", "Cover the screen while capturing", false,
                "Studio has to switch to each camera to shoot it. On = hide the switching behind a full-screen progress screen. " +
                "Off = just a progress bar at the bottom of the screen. Neither is in the images. Not used for video.");
            PauseMotion = Config.Bind("General", "Pause blinking and breathing for stills", true,
                "Turn blinking and breathing (AdvIK) off on every character while still images are shot, and back on afterwards, " +
                "so no shot catches half-closed eyes or a chest mid-breath. Not used for video.");
            OutputFolder = Config.Bind("Output", "Folder", "UserData/cap/CameraSweep",
                "Absolute path, or relative to the game folder. Each run gets its own timestamped subfolder.");
            FilePrefix = Config.Bind("Output", "File prefix", "",
                "Optional text put at the start of every filename, e.g. a scene name.");
            Width = Config.Bind("Output", "Width", 0, "Image width in pixels. 0 = Screencap's render resolution (or the screen's if Screencap isn't installed).");
            Height = Config.Bind("Output", "Height", 0, "Image height in pixels. 0 = Screencap's render resolution (or the screen's if Screencap isn't installed).");
            Upsampling = Config.Bind("Output", "Upsampling", 0,
                new ConfigDescription("Render this many times larger and shrink it back down: smoother edges and fine detail, slower. 0 = Screencap's setting (or 1 without it).",
                    new AcceptableValueRange<int>(0, 4)));
            Transparent = Config.Bind("Output", "Transparent background", false,
                "Save with a transparent background (only where nothing 3D covers it, e.g. no map). Not possible with JPG.");
            Format = Config.Bind("Output", "Image format", ImageFormat.Png,
                "PNG = lossless, works everywhere. WebP = lossless and usually 25-40% smaller than PNG, with transparency. " +
                "WebP lossy = far smaller, keeps transparency (Quality sets how much detail). JPG = small, no transparency.");
            Quality = Config.Bind("Output", "Quality (WebP lossy / JPG)", 90,
                new ConfigDescription("Higher = more detail, bigger files.", new AcceptableValueRange<int>(1, 100)));
            WebpEffort = Config.Bind("Output", "WebP effort", 4,
                new ConfigDescription("How hard WebP works to shrink files. 0 = fastest. Lossless goes to 9, lossy uses up to 6. Higher values save a few % at a big speed cost.",
                    new AcceptableValueRange<int>(0, 9)));

            Engine = Config.Bind("Render", "Capture engine", CaptureEngine.BuiltIn,
                "BuiltIn = Camera Sweep's own renderer (no Screencap needed; cleaner hair edges with a transparent background). " +
                "Screencap = use Screencap's render shot (needs Screencap installed).");
            ShadowResolution = Config.Bind("Render", "Shadow resolution override", 8192,
                new ConfigDescription("Sharper shadows in captures (both engines; Screencap only does this for its own F11 shots). 0 = leave the game's setting.",
                    new AcceptableValueList<int>(0, 1024, 2048, 4096, 8192, 16384)));
            ShadowCascades = Config.Bind("Render", "Shadow cascades override", ShadowCascadeSetting.Four,
                "Shadow cascade count during captures (both engines). Keep = leave the game's setting.");
            AmbientOcclusion = Config.Bind("Render", "Ambient occlusion", AmbientOcclusionSetting.DisableWhenUpsampling,
                "Built-in engine only (Screencap uses its own setting). Ambient occlusion bands when upsampled, so by default it's turned off for upsampled captures.");

            Mode = Config.Bind("Video", "Output mode", OutputMode.Images,
                "Images = one still per camera. Video = one video per camera, recorded through VideoExport for a set number of animation cycles of the chosen character.");
            VideoCycles = Config.Bind("Video", "Animation cycles", 1f,
                new ConfigDescription("How many loops of the character's current animation each video lasts. Fractions allowed (0.5 = half a loop).",
                    new AcceptableValueRange<float>(0.05f, 100f)));
            VideoPrewarm = Config.Bind("Video", "Prewarm cycles", 1,
                new ConfigDescription("Loops to play before recording starts, so physics settles and the video starts exactly on a loop boundary.",
                    new AcceptableValueRange<int>(0, 20)));

            const string stats = "Stats (measured)";
            const string statsNote = "Filled in automatically after each sweep and used for the size/time estimate in the window. 0 = not measured yet.";
            StatBytesPerPixel = Config.Bind(stats, "PNG bytes per pixel", 0f, statsNote);
            StatBytesPerPixelAlpha = Config.Bind(stats, "PNG bytes per pixel (transparent)", 0f, statsNote);
            StatBytesPerPixelOther = Config.Bind(stats, "Other formats bytes per pixel", "", statsNote + " Stored as format=value pairs.");
            StatSecondsPerShot = Config.Bind(stats, "Seconds per image", 0f, statsNote);
            StatWorkMegapixels = Config.Bind(stats, "Rendered megapixels per image", 0f, statsNote + " (resolution × upsampling², for scaling the time)");
            StatVideoBytesPerCycle = Config.Bind(stats, "Video bytes per cycle", 0f, statsNote);
            StatVideoSecondsPerCycle = Config.Bind(stats, "Video seconds per cycle", 0f, statsNote);

            NameTemplateText = Config.Bind("Output", "File name template", NameTemplate.Default,
                "Tokens: {prefix} {scene} {camera} {n} {date} {time} {character} {axes} {swap} {outfit} {clothes} {face} {extras}. " +
                "A token for a variation that's off is left out. Use / for subfolders, e.g. {scene}/{character}_{outfit}_{face}_{camera}");
            FolderPerRun = Config.Bind("Output", "Folder per run", true,
                "Put each run in its own date-time folder inside the output folder. Off = straight into the output folder (existing files are never overwritten).");

            MigrateSettings();

            // Starting presets/folders for characters added to the sweep. Each character's own variations
            // live in Sweep.CharacterPlan and are saved with the scene.
            AxisDefaults.Bind(Config);

            // Per-scene camera on/off, characters and their variations are saved inside the scene file.
            KKAPI.Studio.SaveLoad.StudioSaveLoadApi.RegisterExtraBehaviour<SweepSceneController>(Guid);

            // Remembers the scene file name for the {scene} token.
            var harmony = HarmonyLib.Harmony.CreateAndPatchAll(typeof(SceneName), Guid);

            // Lets Studio's own character / costume / pose lists be used to pick files (see UI.PickMode).
            PickMode.Install(harmony);

            // Saving the original outfit with every plugin's data (see Axes.CoordinateSaver).
            CoordinateSaver.Install(harmony);

            WebpEncoder.Load();

            Runner = new SweepRunner(this);
            _window = new SweepWindow(this);

        }

        /// <summary>One-time changes to settings whose defaults changed, for people who never edited them.</summary>
        private void MigrateSettings()
        {
            var version = Config.Bind("Internal", "Settings version", 0,
                new ConfigDescription("Used to update old defaults. Don't edit.", null, new ConfigurationManagerAttributes { Browsable = false }));
            if (version.Value < 1)
            {
                // 0.9.4: Camera Sweep's renderer became the default (it matched Screencap in testing and does hair
                // edges better with transparency), and the full-screen cover became opt-in.
                if (Engine.Value == CaptureEngine.Screencap) Engine.Value = CaptureEngine.BuiltIn;
                CoverScreen.Value = false;
                version.Value = 1;
            }
        }

        private void Start()
        {
            // Left toolbar button in Studio opens the window (which has the Run button).
            ToolbarButton.Add(this, ToolbarIcon.Create(), () => _window.Visible = !_window.Visible);
        }

        internal void StartSweep()
        {
            if (!StudioAPI.StudioLoaded || Runner.IsRunning) return;
            StartCoroutine(Runner.Run());
        }

        /// <summary>One image of the current view with the current image settings, to check them quickly.</summary>
        internal void StartTestShot(bool compareEngines = false)
        {
            if (!StudioAPI.StudioLoaded || Runner.IsRunning) return;
            StartCoroutine(Runner.TestShot(compareEngines));
        }

        private void Update()
        {
            EncodeQueue.Pump();
            if (!StudioAPI.StudioLoaded) return;

            if (PickMode.Active && Input.GetKeyDown(KeyCode.Escape))
            {
                PickMode.Stop();
                return;
            }

            if (Runner.IsRunning)
            {
                if (CancelHotkey.Value.IsDown()) Runner.Cancel();
                return;
            }

            if (RunHotkey.Value.IsDown()) StartSweep();
            else if (WindowHotkey.Value.IsDown()) _window.Visible = !_window.Visible;
        }

        private void OnGUI()
        {
            _window.Draw();
        }
    }

    /// <summary>Read by BepInEx's Configuration Manager (matched by name); hides internal settings.</summary>
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? Browsable;
    }
}
