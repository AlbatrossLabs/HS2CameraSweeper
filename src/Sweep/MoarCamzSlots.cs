using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using CameraData = Studio.CameraControl.CameraData;

namespace HS2CameraSweep.Sweep
{
    /// <summary>
    /// Reads the extra camera slots (11+) added by MoarCamz, by reflection so MoarCamz stays optional.
    /// MoarCamz keeps one MoarCamzData per slot in MoarCamzPlugin.Instance.MoarCamz; entries 0-9 mirror
    /// the vanilla slots, 10+ are its own. Each has Position / Rotation / Distance (Vector3) and FOV (float).
    ///
    /// Slots are rebuilt as plain CameraData and applied with cameraCtrl.Import, so a slot with MoarCamz
    /// lock-on is shot from its saved position without re-engaging the lock-on (no MoarCamz state is touched).
    /// </summary>
    internal static class MoarCamzSlots
    {
        private const int VanillaSlotCount = 10;

        public static bool Installed => FindPluginType() != null;

        /// <summary>Returns (slotIndex, data) for every MoarCamz slot past the vanilla 10.</summary>
        public static List<KeyValuePair<int, CameraData>> ReadExtraSlots()
        {
            var result = new List<KeyValuePair<int, CameraData>>();

            try
            {
                var pluginType = FindPluginType();
                if (pluginType == null) return result;

                var instance = pluginType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
                if (instance == null) return result;

                if (!(pluginType.GetProperty("MoarCamz", BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance, null) is IList list))
                    return result;

                for (var i = VanillaSlotCount; i < list.Count; i++)
                {
                    var entry = list[i];
                    if (entry == null) continue;

                    var t = entry.GetType();
                    var data = new CameraData
                    {
                        pos = Get<Vector3>(t, entry, "Position"),
                        rotate = Get<Vector3>(t, entry, "Rotation"),
                        distance = Get<Vector3>(t, entry, "Distance"),
                        parse = Get<float>(t, entry, "FOV"),
                    };
                    result.Add(new KeyValuePair<int, CameraData>(i, data));
                }
            }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogWarning("Could not read MoarCamz slots: " + ex.Message);
            }

            return result;
        }

        private static T Get<T>(Type t, object o, string name)
        {
            var value = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(o, null);
            return value is T typed ? typed : default(T);
        }

        private static Type FindPluginType()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(a =>
                {
                    try { return a.GetType("MoarCamz.MoarCamzPlugin", false); }
                    catch { return null; }
                })
                .FirstOrDefault(t => t != null);
        }
    }
}
