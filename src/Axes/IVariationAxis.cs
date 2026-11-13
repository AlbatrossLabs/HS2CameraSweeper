using System.Collections;
using System.Collections.Generic;

namespace HS2CameraSweep.Axes
{
    /// <summary>
    /// One dimension the sweep can vary: outfit, expression, time of day, pose...
    /// The runner shoots every camera for every combination of values across all enabled axes.
    ///
    /// To add a new axis (e.g. time of day):
    ///   1. Implement this interface in a new file under Axes/.
    ///   2. Add it to CameraSweepPlugin.Axes in Awake().
    /// That's it - the runner, filenames and window pick it up automatically.
    /// </summary>
    internal interface IVariationAxis
    {
        /// <summary>Shown in the window and used as a filename prefix, e.g. "outfit".</summary>
        string Name { get; }

        /// <summary>Whether the user turned this axis on.</summary>
        bool Enabled { get; set; }

        /// <summary>
        /// Called at the start of a run. Remember the scene's current state so Restore() can put it back,
        /// and build the list of values. Return an empty list to skip the axis for this run.
        /// </summary>
        IList<AxisValue> Prepare();

        /// <summary>Apply one value. Can span frames (yield return null) if the change needs time.</summary>
        IEnumerator Apply(AxisValue value);

        /// <summary>Put back whatever Prepare() remembered. Always called, including on cancel.</summary>
        void Restore();

        /// <summary>Optional settings UI inside the Camera Sweep window (IMGUI). Keep it short.</summary>
        void DrawSettings();
    }

    internal sealed class AxisValue
    {
        public AxisValue(string label, object payload)
        {
            Label = label;
            Payload = payload;
        }

        /// <summary>Filename-safe label for this value, e.g. "swimsuit" or "smile".</summary>
        public string Label { get; }

        /// <summary>Whatever the axis needs to apply the value (file path, pattern numbers...).</summary>
        public object Payload { get; }
    }
}
