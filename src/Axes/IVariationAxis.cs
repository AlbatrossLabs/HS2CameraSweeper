using System.Collections;
using System.Collections.Generic;
using Studio;

namespace HS2CameraSweep.Axes
{
    /// <summary>
    /// One thing the sweep can vary for one character: outfit, expression, clothing state...
    /// Every character added to the sweep gets its own instance (see CharacterPlan), with settings in
    /// an AxisStore saved with the scene. The runner shoots every camera for every combination of values
    /// across all enabled variations of all characters.
    ///
    /// To add a new kind: implement this interface (constructor takes the OCIChar and its AxisStore),
    /// then add it to CharacterPlan.Kinds and CharacterPlan.Create.
    /// </summary>
    internal interface IVariationAxis
    {
        /// <summary>File-name token and prefix, e.g. "outfit".</summary>
        string Name { get; }

        /// <summary>Shown in the window, e.g. "Outfits".</summary>
        string DisplayName { get; }

        /// <summary>One line under the name when it's off, saying what it does.</summary>
        string Description { get; }

        /// <summary>Whether the user turned this axis on.</summary>
        bool Enabled { get; set; }

        /// <summary>How many values a run would cycle through right now (for the window's estimate). Must be cheap.</summary>
        int Count { get; }

        /// <summary>Label of the first value, for the file-name preview in the window. Null if none.</summary>
        string PreviewLabel { get; }

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

    /// <summary>
    /// Counts character reloads (a character swap loads a whole new card), so a variation that runs after
    /// the swap knows the character it changed earlier has been replaced: e.g. the outfit variation's
    /// "wearing now" shot is then simply the new card's own outfit.
    /// </summary>
    internal static class CharacterReloads
    {
        private static readonly Dictionary<OCIChar, int> Counts = new Dictionary<OCIChar, int>();

        public static int Of(OCIChar c) => c != null && Counts.TryGetValue(c, out var n) ? n : 0;

        public static void Bump(OCIChar c)
        {
            if (c != null) Counts[c] = Of(c) + 1;
        }
    }
}
