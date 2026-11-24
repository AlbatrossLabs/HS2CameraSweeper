using System;
using System.Reflection;

namespace HS2CameraSweep.Sweep
{
    /// <summary>
    /// Studio Theme can give each camera (a numbered view or a camera object) a Graphics plugin preset,
    /// loaded when Studio switches to it. A sweep doesn't switch cameras the way Studio's buttons do, so it
    /// asks Studio Theme for each shot's preset itself, and puts the look from before the sweep back after.
    /// Everything is by reflection: without Studio Theme (or Graphics, or any presets) this does nothing.
    /// </summary>
    internal static class ThemePresets
    {
        private const string TypeName = "HS2StudioTheme.CameraPresets";
        private static Type _type;
        private static bool _looked;

        private static Type Api
        {
            get
            {
                if (_looked) return _type;
                _looked = true;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { _type = a.GetType(TypeName, false); }
                    catch { _type = null; }
                    if (_type != null) break;
                }
                return _type;
            }
        }

        private static object Call(string method, params object[] args)
        {
            var t = Api;
            var m = t?.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            if (m == null) return null;
            try { return m.Invoke(null, args); }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogWarning("Studio Theme camera preset (" + method + "): " + (ex.InnerException ?? ex).Message);
                return null;
            }
        }

        /// <summary>Start of a sweep: a token for <see cref="End"/>, or null when there's nothing to do.</summary>
        public static object Begin() => Call("BeginSweep");

        /// <summary>This shot's camera preset, if it has one. True if a preset was loaded (give it time to settle).</summary>
        public static bool ApplyFor(CameraShot shot)
        {
            object loaded = null;
            if (shot is SavedSlotShot slot) loaded = Call("ApplyForSlot", slot.Index);
            else if (shot is CameraObjectShot cam && cam.Camera?.objectInfo != null) loaded = Call("ApplyForCamera", cam.Camera.objectInfo.dicKey);
            return loaded is bool b && b;
        }

        /// <summary>End of a sweep: the look from before it comes back.</summary>
        public static void End(object token)
        {
            if (token != null) Call("EndSweep", token);
        }
    }
}
