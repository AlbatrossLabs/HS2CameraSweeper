using System.Collections.Generic;
using System.Linq;
using Studio;
using UnityEngine;
using CameraData = Studio.CameraControl.CameraData;

namespace HS2CameraSweep.Sweep
{
    /// <summary>One camera position to shoot from.</summary>
    internal abstract class CameraShot
    {
        /// <summary>Short, filename-safe label, e.g. "slot03" or "cam_Closeup".</summary>
        public abstract string Label { get; }

        /// <summary>Point the Studio view at this camera.</summary>
        public abstract void Apply(Studio.Studio studio);
    }

    /// <summary>One of the 10 numbered camera buttons (SceneInfo.cameraData).</summary>
    internal sealed class SavedSlotShot : CameraShot
    {
        private readonly int _index;
        private readonly CameraData _data;

        public SavedSlotShot(int index, CameraData data)
        {
            _index = index;
            _data = data;
        }

        public int Index => _index;

        public override string Label => $"slot{_index + 1:00}";

        public override void Apply(Studio.Studio studio)
        {
            // A saved slot only applies to the free camera, so make sure no camera object is driving the view.
            if (studio.ociCamera != null) studio.ChangeCamera(null, false, true);
            studio.cameraCtrl.Import(_data);
        }
    }

    /// <summary>A camera object added with Add &gt; Camera (OCICamera).</summary>
    internal sealed class CameraObjectShot : CameraShot
    {
        private readonly OCICamera _camera;
        private readonly string _label;

        public CameraObjectShot(OCICamera camera, int ordinal)
        {
            _camera = camera;
            var name = string.IsNullOrEmpty(camera.name) ? ordinal.ToString("00") : FileNames.Sanitize(camera.name);
            _label = "cam_" + name;
        }

        public OCICamera Camera => _camera;

        public override string Label => _label;

        public override void Apply(Studio.Studio studio)
        {
            studio.ChangeCamera(_camera, true);
        }
    }

    internal static class CameraShotCollector
    {
        public static List<CameraShot> Collect(Studio.Studio studio, CameraSource source, bool skipUnset)
        {
            var shots = new List<CameraShot>();

            if (source == CameraSource.SavedSlots || source == CameraSource.Both)
                shots.AddRange(CollectSlots(studio, skipUnset));

            if (source == CameraSource.CameraObjects || source == CameraSource.Both)
                shots.AddRange(CollectObjects(studio));

            return shots;
        }

        private static IEnumerable<CameraShot> CollectSlots(Studio.Studio studio, bool skipUnset)
        {
            // Vanilla slots 1-10, then MoarCamz's extra slots (11+) if it's installed.
            var all = new List<KeyValuePair<int, CameraData>>();
            var slots = studio.sceneInfo.cameraData;
            if (slots != null)
                for (var i = 0; i < slots.Length; i++)
                    all.Add(new KeyValuePair<int, CameraData>(i, slots[i]));
            all.AddRange(MoarCamzSlots.ReadExtraSlots());

            // Studio fills never-saved slots with the reset view (SceneInfo.Init -> ExportResetData),
            // so "equal to the reset view" is how we detect an unused slot. MoarCamz clears slots to zeros.
            var resetView = studio.cameraCtrl.ExportResetData();
            var seen = new List<CameraData>();

            foreach (var kv in all)
            {
                var data = kv.Value;
                if (data == null) continue;

                if (skipUnset)
                {
                    if (SameView(data, resetView) || IsCleared(data)) continue;
                    if (seen.Any(s => SameView(s, data))) continue;
                }

                seen.Add(data);
                yield return new SavedSlotShot(kv.Key, data);
            }
        }

        private static bool IsCleared(CameraData d) =>
            d.pos == Vector3.zero && d.rotate == Vector3.zero && d.distance == Vector3.zero;

        private static IEnumerable<CameraShot> CollectObjects(Studio.Studio studio)
        {
            // dicObjectCtrl is keyed by object id; ordering by key keeps the order stable between runs.
            var cameras = studio.dicObjectCtrl
                .OrderBy(kv => kv.Key)
                .Select(kv => kv.Value)
                .OfType<OCICamera>()
                .ToList();

            for (var i = 0; i < cameras.Count; i++)
                yield return new CameraObjectShot(cameras[i], i + 1);
        }

        private static bool SameView(CameraData a, CameraData b)
        {
            return Approximately(a.pos, b.pos)
                   && Approximately(a.rotate, b.rotate)
                   && Approximately(a.distance, b.distance)
                   && Mathf.Approximately(a.parse, b.parse);
        }

        private static bool Approximately(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 1e-6f;
    }
}
