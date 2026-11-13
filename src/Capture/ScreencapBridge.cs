using System;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace HS2CameraSweep.Capture
{
    /// <summary>
    /// Talks to BepisPlugins' Screencap (Screencap.ScreenshotManager) by reflection, so this plugin has
    /// no compile-time dependency on it and works across Screencap versions:
    ///
    ///   r19.x (BetterRepack R15 ships 19.5):
    ///     instance  RenderTexture Capture(int width, int height, int downscaling, bool transparent)
    ///               (fires OnPreCapture/OnPostCapture itself)
    ///     settings  private ConfigEntry props CaptureWidth / CaptureHeight (instance), Downscaling (static)
    ///
    ///   r20+:
    ///     static    RenderTexture CaptureRender(int? w, int? h, int? downscaling, AlphaMode? alpha)
    ///     static    FirePreCapture() / FirePostCapture()  (caller must bracket the captures)
    ///
    /// Either way we get a RenderTexture back and write the file ourselves (ShotWriter).
    /// </summary>
    internal static class ScreencapBridge
    {
        public const string ScreencapGuid = "com.bepis.bepinex.screenshotmanager";

        private enum ApiKind
        {
            None,
            Legacy, // r19.x
            Modern, // r20+
        }

        private static bool _resolved;
        private static ApiKind _kind;
        private static Type _manager;
        private static object _instance;

        // Modern
        private static MethodInfo _captureRender;
        private static Type _alphaModeType;
        private static MethodInfo _firePre;
        private static MethodInfo _firePost;

        // Legacy
        private static MethodInfo _capture;

        public static bool Available
        {
            get
            {
                Resolve();
                return _kind != ApiKind.None;
            }
        }

        public static string Description
        {
            get
            {
                Resolve();
                switch (_kind)
                {
                    case ApiKind.Legacy: return "Screencap (r19)";
                    case ApiKind.Modern: return "Screencap (r20+)";
                    default: return "built-in renderer";
                }
            }
        }

        /// <summary>r20+ needs this once before a batch of captures. No-op on r19 (it fires per capture).</summary>
        public static void FirePreCapture()
        {
            Resolve();
            if (_kind == ApiKind.Modern) SafeInvoke(_firePre);
        }

        /// <summary>r20+ needs this once after a batch. No-op on r19.</summary>
        public static void FirePostCapture()
        {
            Resolve();
            if (_kind == ApiKind.Modern) SafeInvoke(_firePost);
        }

        /// <summary>
        /// Returns a temporary RenderTexture (caller must RenderTexture.ReleaseTemporary it) or null on failure.
        /// Pass 0 for width/height/upsampling to use Screencap's own configured values.
        /// </summary>
        public static RenderTexture CaptureRender(int width, int height, int upsampling, bool transparent)
        {
            Resolve();
            try
            {
                switch (_kind)
                {
                    case ApiKind.Modern:
                    {
                        object alpha = _alphaModeType != null
                            ? Enum.ToObject(_alphaModeType, transparent ? 1 : 0) // 0 = None, 1 = composite
                            : null;
                        return _captureRender.Invoke(null, new[]
                        {
                            width > 0 ? (object)width : null,
                            height > 0 ? (object)height : null,
                            upsampling > 0 ? (object)upsampling : null,
                            alpha,
                        }) as RenderTexture;
                    }

                    case ApiKind.Legacy:
                    {
                        var w = width > 0 ? width : ReadInt("CaptureWidth", Screen.width);
                        var h = height > 0 ? height : ReadInt("CaptureHeight", Screen.height);
                        var ds = upsampling > 0 ? upsampling : ReadInt("Downscaling", 1);
                        return _capture.Invoke(_instance, new object[] { w, h, Mathf.Max(1, ds), transparent }) as RenderTexture;
                    }

                    default:
                        return null;
                }
            }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogError("Screencap capture failed: " + (ex.InnerException ?? ex));
                return null;
            }
        }

        /// <summary>The render size and upsampling Screencap is set to (what a capture with 0/0/0 would use).</summary>
        public static void GetRenderSettings(out int width, out int height, out int upsampling)
        {
            Resolve();
            switch (_kind)
            {
                case ApiKind.Modern:
                    width = ReadInt("ResolutionX", Screen.width);
                    height = ReadInt("ResolutionY", Screen.height);
                    upsampling = ReadInt("DownscalingRate", 1);
                    break;
                case ApiKind.Legacy:
                    width = ReadInt("CaptureWidth", Screen.width);
                    height = ReadInt("CaptureHeight", Screen.height);
                    upsampling = ReadInt("Downscaling", 1);
                    break;
                default:
                    width = Screen.width;
                    height = Screen.height;
                    upsampling = 1;
                    break;
            }
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;

            _manager = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => SafeGetType(a, "Screencap.ScreenshotManager"))
                .FirstOrDefault(t => t != null);

            if (_manager == null)
            {
                CameraSweepPlugin.Logger.LogWarning("Screencap not found - using built-in renderer.");
                return;
            }

            const BindingFlags pubStatic = BindingFlags.Public | BindingFlags.Static;

            // r20+
            _captureRender = _manager.GetMethods(pubStatic)
                .FirstOrDefault(m => m.Name == "CaptureRender" && m.GetParameters().Length == 4);
            if (_captureRender != null)
            {
                _alphaModeType = _manager.Assembly.GetType("Screencap.AlphaMode");
                _firePre = _manager.GetMethod("FirePreCapture", pubStatic, null, Type.EmptyTypes, null);
                _firePost = _manager.GetMethod("FirePostCapture", pubStatic, null, Type.EmptyTypes, null);
                _kind = ApiKind.Modern;
            }
            else
            {
                // r19.x
                _capture = _manager.GetMethod("Capture", BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(int), typeof(int), typeof(int), typeof(bool) }, null);
                _instance = FindInstance();
                if (_capture != null && _instance != null) _kind = ApiKind.Legacy;
            }

            if (_kind == ApiKind.None)
                CameraSweepPlugin.Logger.LogWarning("Screencap found but its capture API wasn't recognised - using built-in renderer.");
            else
                CameraSweepPlugin.Logger.LogInfo("Capturing through " + Description);
        }

        private static object FindInstance()
        {
            if (Chainloader.PluginInfos.TryGetValue(ScreencapGuid, out var info) && info.Instance != null)
                return info.Instance;
            return UnityEngine.Object.FindObjectOfType(_manager);
        }

        /// <summary>Reads one of Screencap's private ConfigEntry&lt;int&gt; properties (static or instance).</summary>
        private static int ReadInt(string property, int fallback)
        {
            try
            {
                var prop = _manager.GetProperty(property,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                if (prop == null) return fallback;

                var target = prop.GetGetMethod(true).IsStatic ? null : _instance;
                if (prop.GetValue(target, null) is ConfigEntryBase entry && entry.BoxedValue is int value && value > 0)
                    return value;
            }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogDebug($"Could not read Screencap {property}: {ex.Message}");
            }

            return fallback;
        }

        private static Type SafeGetType(Assembly a, string name)
        {
            try { return a.GetType(name, false); }
            catch { return null; }
        }

        private static void SafeInvoke(MethodInfo m)
        {
            if (m == null) return;
            try { m.Invoke(null, null); }
            catch (Exception ex) { CameraSweepPlugin.Logger.LogWarning($"{m.Name} threw: {ex.InnerException ?? ex}"); }
        }
    }
}
