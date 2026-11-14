using System;
using System.Collections.Generic;
using System.Linq;
using HS2CameraSweep.Axes;
using Studio;

namespace HS2CameraSweep.Sweep
{
    /// <summary>
    /// A character added to the sweep and the variations chosen for them. Each character has its own
    /// instance of every variation with its own settings. With several characters the sweep shoots
    /// every combination (Aiko's 3 faces × Kenji's 3 faces = 9).
    /// </summary>
    internal sealed class CharacterPlan
    {
        /// <summary>Variation kinds, outer to inner. Slow changes (swap, outfit) go outermost so they load least often.</summary>
        public static readonly string[] Kinds = { "swap", "outfit", "pose", "clothes", "face", "extras" };

        public readonly OCIChar Character;
        public readonly Dictionary<string, AxisStore> Stores = new Dictionary<string, AxisStore>();
        public readonly List<IVariationAxis> Axes = new List<IVariationAxis>();
        public bool Expanded = true;

        public CharacterPlan(OCIChar character, Dictionary<string, Dictionary<string, string>> saved = null)
        {
            Character = character;
            foreach (var kind in Kinds)
            {
                var store = new AxisStore(saved != null && saved.TryGetValue(kind, out var v) ? v : null);
                Stores[kind] = store;
                Axes.Add(Create(kind, character, store));
            }
        }

        public IVariationAxis Axis(string kind) => Axes[Array.IndexOf(Kinds, kind)];

        public IEnumerable<IVariationAxis> Enabled => Axes.Where(a => a.Enabled);

        /// <summary>Shots per camera from this character alone (variations with nothing to cycle are skipped).</summary>
        public int Combinations => Enabled.Aggregate(1, (n, a) => a.Count > 0 ? n * a.Count : n);

        public string Name => SweepRunner.CharName(Character);

        /// <summary>"Outfits ×6 · Expressions ×3", or "no variations".</summary>
        public string Summary
        {
            get
            {
                var parts = Enabled.Select(a => a.Count > 0 ? $"{a.DisplayName} ×{a.Count}" : $"{a.DisplayName} (none)").ToArray();
                return parts.Length == 0 ? "no variations yet" : string.Join(" · ", parts);
            }
        }

        private static IVariationAxis Create(string kind, OCIChar c, AxisStore s)
        {
            switch (kind)
            {
                case "swap": return new CharacterSwapAxis(c, s);
                case "outfit": return new OutfitAxis(c, s);
                case "pose": return new PoseAxis(c, s);
                case "clothes": return new ClothesStateAxis(c, s);
                case "face": return new ExpressionAxis(c, s);
                default: return new FaceExtrasAxis(c, s);
            }
        }
    }
}
