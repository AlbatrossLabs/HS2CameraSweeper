using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HS2CameraSweep.Capture
{
    public enum ShadowCascadeSetting
    {
        Keep,
        One,
        Two,
        Four,
    }

    public enum AmbientOcclusionSetting
    {
        Keep,
        DisableWhenUpsampling,
        Disable,
    }

    /// <summary>
    /// Temporary render tweaks for captures, the same ones Screencap applies to its own F11 shots:
    /// sharper shadows (higher shadow map resolution, cascade count) and switching ambient occlusion off
    /// when upsampling (it bands). Post-processing and the old image-effects DOF are reached by
    /// reflection so there's no hard dependency on either.
    /// </summary>
    internal static class RenderOverrides
    {
        // ---------------------------------------------------------------- shadows (whole batch)

        private static int _savedCascades = -1;
        private static readonly List<KeyValuePair<Light, int>> SavedLights = new List<KeyValuePair<Light, int>>();

        public static void ApplyShadows(int resolution, ShadowCascadeSetting cascades)
        {
            RestoreShadows();
            try
            {
                if (cascades != ShadowCascadeSetting.Keep)
                {
                    _savedCascades = QualitySettings.shadowCascades;
                    QualitySettings.shadowCascades = cascades == ShadowCascadeSetting.One ? 1 : cascades == ShadowCascadeSetting.Two ? 2 : 4;
                }

                if (resolution > 0)
                {
                    foreach (var light in UnityEngine.Object.FindObjectsOfType<Light>())
                    {
                        SavedLights.Add(new KeyValuePair<Light, int>(light, light.shadowCustomResolution));
                        light.shadowCustomResolution = resolution;
                    }
                }
            }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogWarning("Could not apply shadow settings: " + ex.Message);
            }
        }

        public static void RestoreShadows()
        {
            try
            {
                if (_savedCascades >= 0) QualitySettings.shadowCascades = _savedCascades;
                foreach (var kv in SavedLights)
                    if (kv.Key != null) kv.Key.shadowCustomResolution = kv.Value;
            }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogWarning("Could not restore shadow settings: " + ex.Message);
            }

            _savedCascades = -1;
            SavedLights.Clear();
        }

        // ---------------------------------------------------------------- ambient occlusion (per capture)

        private static bool _ppResolved;
        private static Type _volumeType;
        private static Type _layerType;

        private static void ResolvePostProcessing()
        {
            if (_ppResolved) return;
            _ppResolved = true;
            _volumeType = AccessTools.TypeByName("UnityEngine.Rendering.PostProcessing.PostProcessVolume");
            _layerType = AccessTools.TypeByName("UnityEngine.Rendering.PostProcessing.PostProcessLayer");
        }

        /// <summary>Turns ambient occlusion off in every post-processing volume. Returns what to pass to RestoreAO.</summary>
        public static List<object> DisableAO()
        {
            var changed = new List<object>();
            ResolvePostProcessing();
            if (_volumeType == null) return changed;

            try
            {
                var profileProp = _volumeType.GetProperty("profile");
                foreach (var volume in UnityEngine.Object.FindObjectsOfType(_volumeType))
                {
                    var profile = profileProp?.GetValue(volume, null);
                    if (!(profile?.GetType().GetField("settings")?.GetValue(profile) is IList settings)) continue;

                    foreach (var effect in settings)
                    {
                        if (effect == null || effect.GetType().Name != "AmbientOcclusion") continue;
                        var param = effect.GetType().GetField("enabled")?.GetValue(effect);
                        var value = param?.GetType().GetField("value");
                        if (value == null || !(value.GetValue(param) is bool on) || !on) continue;
                        value.SetValue(param, false);
                        changed.Add(param);
                    }
                }
            }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogDebug("Could not disable ambient occlusion: " + ex.Message);
            }

            return changed;
        }

        public static void RestoreAO(List<object> changed)
        {
            if (changed == null) return;
            foreach (var param in changed)
            {
                try { param.GetType().GetField("value").SetValue(param, true); }
                catch (Exception ex) { CameraSweepPlugin.Logger.LogDebug("Could not restore ambient occlusion: " + ex.Message); }
            }
        }

        /// <summary>The camera's post-processing layer (bloom, colour grading, AO...), or null.</summary>
        public static Behaviour PostProcessLayer(Camera cam)
        {
            ResolvePostProcessing();
            return _layerType == null ? null : cam.GetComponent(_layerType) as Behaviour;
        }

        // ---------------------------------------------------------------- old image-effects depth of field

        private static bool _dofResolved;
        private static Type _dofType;
        private static FieldInfo _dofMaxBlur;

        public static Behaviour DepthOfField(Camera cam)
        {
            if (!_dofResolved)
            {
                _dofResolved = true;
                _dofType = AccessTools.TypeByName("UnityStandardAssets.ImageEffects.DepthOfField");
                _dofMaxBlur = _dofType?.GetField("maxBlurSize");
            }

            return _dofType == null ? null : cam.GetComponent(_dofType) as Behaviour;
        }

        public static float GetMaxBlur(Behaviour dof) => dof != null && _dofMaxBlur != null ? (float)_dofMaxBlur.GetValue(dof) : 0f;

        public static void SetMaxBlur(Behaviour dof, float value)
        {
            if (dof != null && _dofMaxBlur != null) _dofMaxBlur.SetValue(dof, value);
        }
    }
}
