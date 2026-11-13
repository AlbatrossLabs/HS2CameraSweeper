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

    [BepInPlugin(Guid, PluginName, Version)]
    [BepInProcess("StudioNEOV2")]
    [BepInDependency(KoikatuAPI.GUID)]
    [BepInDependency(ScreencapBridge.ScreencapGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(VideoExportBridge.VideoExportGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class CameraSweepPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.albatrosslabs.hs2.camerasweep";
        public const string PluginName = "Camera Sweep";
        public const string Version = "0.2.0";

        internal static new ManualLogSource Logger;
        internal static CameraSweepPlugin Instance;

        // General
        internal ConfigEntry<KeyboardShortcut> RunHotkey;
        internal ConfigEntry<KeyboardShortcut> CancelHotkey;
        internal ConfigEntry<KeyboardShortcut> WindowHotkey;
        internal ConfigEntry<CameraSource> Source;
        internal ConfigEntry<bool> SkipUnsetSlots;
        internal ConfigEntry<int> SettleFrames;

        // Output
        internal ConfigEntry<string> OutputFolder;
        internal ConfigEntry<string> FilePrefix;
        internal ConfigEntry<int> Width;
        internal ConfigEntry<int> Height;
        internal ConfigEntry<int> Upsampling;
        internal ConfigEntry<bool> Transparent;

        // Video
        internal ConfigEntry<OutputMode> Mode;
        internal ConfigEntry<float> VideoCycles;
        internal ConfigEntry<int> VideoPrewarm;

        /// <summary>Character whose animation drives video length. Null = use the selection when the sweep starts.</summary>
        internal Studio.OCIChar VideoTarget;

        internal readonly List<IVariationAxis> Axes = new List<IVariationAxis>();
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

            OutputFolder = Config.Bind("Output", "Folder", "UserData/cap/CameraSweep",
                "Absolute path, or relative to the game folder. Each run gets its own timestamped subfolder.");
            FilePrefix = Config.Bind("Output", "File prefix", "",
                "Optional text put at the start of every filename, e.g. a scene name.");
            Width = Config.Bind("Output", "Width", 0, "Capture width in pixels. 0 = use Screencap's render resolution.");
            Height = Config.Bind("Output", "Height", 0, "Capture height in pixels. 0 = use Screencap's render resolution.");
            Upsampling = Config.Bind("Output", "Upsampling", 0,
                new ConfigDescription("Render larger and downscale (anti-aliasing). 0 = use Screencap's setting.",
                    new AcceptableValueRange<int>(0, 4)));
            Transparent = Config.Bind("Output", "Transparent background", false,
                "Capture with alpha (Screencap's composite mode). Ignored by the built-in fallback renderer.");

            Mode = Config.Bind("Video", "Output mode", OutputMode.Images,
                "Images = one still per camera. Video = one video per camera, recorded through VideoExport for a set number of animation cycles of the chosen character.");
            VideoCycles = Config.Bind("Video", "Animation cycles", 1f,
                new ConfigDescription("How many loops of the character's current animation each video lasts. Fractions allowed (0.5 = half a loop).",
                    new AcceptableValueRange<float>(0.05f, 100f)));
            VideoPrewarm = Config.Bind("Video", "Prewarm cycles", 1,
                new ConfigDescription("Loops to play before recording starts, so physics settles and the video starts exactly on a loop boundary.",
                    new AcceptableValueRange<int>(0, 20)));

            // Variation axes. Order here = nesting order (first axis is the outermost loop).
            // Cameras are always the innermost loop, so each outfit/expression is set once and then
            // every camera is shot before moving on.
            Axes.Add(new OutfitAxis(Config));
            Axes.Add(new ExpressionAxis(Config));

            Runner = new SweepRunner(this);
            _window = new SweepWindow(this);

        }

        private void Start()
        {
            // Left toolbar button in Studio opens the window (which has the Run button).
            // Uses the long-standing KKAPI overload so it works on older HS2API builds too.
#pragma warning disable CS0618
            KKAPI.Studio.UI.CustomToolbarButtons.AddLeftToolbarButton(ToolbarIcon.Create(),
                () => _window.Visible = !_window.Visible);
#pragma warning restore CS0618
        }

        internal void StartSweep()
        {
            if (!StudioAPI.StudioLoaded || Runner.IsRunning) return;
            StartCoroutine(Runner.Run());
        }

        private void Update()
        {
            if (!StudioAPI.StudioLoaded) return;

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
}
