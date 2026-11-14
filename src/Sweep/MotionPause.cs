using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Studio;
using UnityEngine;

namespace HS2CameraSweep.Sweep
{
    /// <summary>
    /// Stops blinking and breathing on every character while still images are shot, so no shot catches
    /// half-closed eyes or a chest mid-breath, and puts both back as they were afterwards.
    ///
    /// Blinking is Studio's own per-character setting (State > Blink). Breathing comes from AdvIK
    /// (orange.spork's AdvIKPlugin), which adds an ABMX bone effect per character; with it off, the chest
    /// goes back to rest. AdvIK is optional: without it only blinking is paused.
    ///
    /// Apply() is called before every shot, because a character swap loads a new card with its own settings.
    /// </summary>
    internal sealed class MotionPause
    {
        private readonly Dictionary<OCIChar, bool> _blink = new Dictionary<OCIChar, bool>();
        private readonly Dictionary<OCIChar, bool> _breath = new Dictionary<OCIChar, bool>();

        private static bool _resolved;
        private static Type _advIkController;
        private static PropertyInfo _breathingController;
        private static PropertyInfo _breathingEnabled;

        /// <summary>Turn blinking and breathing off on every character (remembering the first value seen).</summary>
        public void Apply()
        {
            foreach (var oci in SweepSelection.AllCharacters())
            {
                var cha = oci?.charInfo;
                if (cha == null) continue;

                try
                {
                    if (!_blink.ContainsKey(oci)) _blink[oci] = cha.GetEyesBlinkFlag();
                    if (cha.GetEyesBlinkFlag()) oci.ChangeBlink(false);
                }
                catch (Exception ex) { CameraSweepPlugin.Logger.LogDebug("Blink pause: " + ex.Message); }

                var breathing = Breathing(cha);
                if (breathing == null) continue;
                try
                {
                    var on = (bool)_breathingEnabled.GetValue(breathing, null);
                    if (!_breath.ContainsKey(oci)) _breath[oci] = on;
                    if (on) _breathingEnabled.SetValue(breathing, false, null);
                }
                catch (Exception ex) { CameraSweepPlugin.Logger.LogDebug("Breathing pause: " + ex.Message); }
            }
        }

        /// <summary>Put blinking and breathing back as they were before the first Apply().</summary>
        public void Restore()
        {
            foreach (var kv in _blink)
            {
                try
                {
                    var cha = kv.Key?.charInfo;
                    if (cha != null && cha.GetEyesBlinkFlag() != kv.Value) kv.Key.ChangeBlink(kv.Value);
                }
                catch (Exception ex) { CameraSweepPlugin.Logger.LogDebug("Blink restore: " + ex.Message); }
            }

            foreach (var kv in _breath)
            {
                try
                {
                    var breathing = Breathing(kv.Key?.charInfo);
                    if (breathing != null) _breathingEnabled.SetValue(breathing, kv.Value, null);
                }
                catch (Exception ex) { CameraSweepPlugin.Logger.LogDebug("Breathing restore: " + ex.Message); }
            }

            _blink.Clear();
            _breath.Clear();
        }

        /// <summary>AdvIK's breathing effect on this character, or null (no AdvIK, or not set up yet).</summary>
        private static object Breathing(AIChara.ChaControl cha)
        {
            if (cha == null) return null;
            Resolve();
            if (_advIkController == null) return null;
            try
            {
                var controller = cha.GetComponent(_advIkController);
                return controller == null ? null : _breathingController.GetValue(controller, null);
            }
            catch
            {
                return null;
            }
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _advIkController = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => { try { return a.GetType("AdvIKPlugin.AdvIKCharaController", false); } catch { return null; } })
                    .FirstOrDefault(t => t != null);
                if (_advIkController == null) return;

                _breathingController = _advIkController.GetProperty("BreathingController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _breathingEnabled = _breathingController?.PropertyType.GetProperty("Enabled", BindingFlags.Public | BindingFlags.Instance);
                if (_breathingController == null || _breathingEnabled == null)
                {
                    CameraSweepPlugin.Logger.LogInfo("AdvIK found, but not its breathing switch - breathing won't be paused for stills.");
                    _advIkController = null;
                }
            }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogDebug("AdvIK lookup: " + ex.Message);
                _advIkController = null;
            }
        }
    }
}
