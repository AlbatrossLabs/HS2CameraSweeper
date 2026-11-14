using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace HS2CameraSweep.Axes
{
    /// <summary>
    /// One character's settings for one variation (on/off, presets, folder...). Plain strings so the whole
    /// thing can be saved inside the scene file with the character it belongs to.
    /// </summary>
    internal sealed class AxisStore
    {
        public readonly Dictionary<string, string> Values;

        public AxisStore(Dictionary<string, string> values = null)
        {
            Values = values ?? new Dictionary<string, string>();
        }

        public Setting<bool> Bool(string key, bool fallback) =>
            new Setting<bool>(
                () => Values.TryGetValue(key, out var v) ? v == "1" : fallback,
                v => Values[key] = v ? "1" : "0");

        /// <summary>A text setting. Missing keys start from the default (read once, then kept per character).</summary>
        public Setting<string> Text(string key, Func<string> fallback) =>
            new Setting<string>(
                () =>
                {
                    if (Values.TryGetValue(key, out var v)) return v;
                    var d = fallback() ?? "";
                    Values[key] = d;
                    return d;
                },
                v => Values[key] = v ?? "");
    }

    /// <summary>A value with a getter and setter, so axis code reads like the old ConfigEntry.Value.</summary>
    internal sealed class Setting<T>
    {
        private readonly Func<T> _get;
        private readonly Action<T> _set;

        public Setting(Func<T> get, Action<T> set)
        {
            _get = get;
            _set = set;
        }

        public T Value
        {
            get => _get();
            set => _set(value);
        }
    }

    /// <summary>
    /// Starting values for a character you add, kept in F1 under "Defaults: ...". "Save as default" in a
    /// variation copies that character's presets here.
    /// </summary>
    internal static class AxisDefaults
    {
        public static ConfigEntry<bool> SwapIncludeOriginal;
        public static ConfigEntry<string> ClothesPresets;
        public static ConfigEntry<string> ExpressionPresets;
        public static ConfigEntry<string> ExtrasPresets;

        // Section and key names are the pre-0.8 ones, so existing settings carry over as defaults.
        public static void Bind(ConfigFile config)
        {
            const string note = " Default for characters you add; each character keeps its own copy (saved with the scene).";
            SwapIncludeOriginal = config.Bind("Axis: Character swap", "Also shoot the original", true,
                "Shoot the character as they are (no swap) as well, first. Named \"original\" in {swap}." + note);
            ClothesPresets = config.Bind("Axis: Clothes state", "Presets", ClothesStateAxis.DefaultPresets,
                "name=part:state,part:state; ... Parts: top, bottom, bra, panties, gloves, pantyhose, socks, shoes, all. States: on, half, off." + note);
            ExpressionPresets = config.Bind("Axis: Expression", "Presets", ExpressionAxis.DefaultPresets,
                "name=eyebrow,eyes,mouth; ... Numbers match Studio's Face dropdowns (first = 0). -1 = leave as is." + note);
            // Upgrade defaults nobody edited to the newer ones.
            if (Array.IndexOf(ClothesStateAxis.OldDefaults, ClothesPresets.Value) >= 0) ClothesPresets.Value = ClothesStateAxis.DefaultPresets;
            if (Array.IndexOf(ExpressionAxis.OldDefaults, ExpressionPresets.Value) >= 0) ExpressionPresets.Value = ExpressionAxis.DefaultPresets;

            ExtrasPresets = config.Bind("Axis: Face extras", "Presets", FaceExtrasAxis.DefaultPresets,
                "name=blush,tears,eyeLook,neckLook,eyesOpen,mouthOpen; ... -1 = leave as is." + note);
        }

    }
}
