using System;
using System.Runtime.CompilerServices;
using BepInEx;
using KKAPI.Studio.UI.Toolbars;
using UnityEngine;

namespace HS2CameraSweep.UI
{
    /// <summary>
    /// Camera Sweep's button on Studio's left toolbar. It asks for the bottom row (row 0 is the bottom;
    /// KKAPI moves it right to the first free spot), so it doesn't take the spots higher up that other
    /// plugins' buttons default to. If you drag it somewhere else in KKAPI's toolbar edit mode, that wins.
    /// Older HS2API builds without the toolbar manager get the plain button.
    /// </summary>
    internal static class ToolbarButton
    {
        private const string ButtonId = "camera_sweep";

        public static void Add(BaseUnityPlugin owner, Texture2D icon, Action onClick)
        {
            try
            {
                AddPositioned(owner, icon, onClick);
                return;
            }
            catch (Exception ex) when (ex is TypeLoadException || ex is MissingMethodException || ex is MissingFieldException)
            {
                CameraSweepPlugin.Logger.LogDebug("Toolbar manager not available, using the plain toolbar button: " + ex.Message);
            }

#pragma warning disable CS0618
            KKAPI.Studio.UI.CustomToolbarButtons.AddLeftToolbarButton(icon, onClick);
#pragma warning restore CS0618
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AddPositioned(BaseUnityPlugin owner, Texture2D icon, Action onClick)
        {
            var button = new SimpleToolbarButton(ButtonId, "Camera Sweep", () => icon, owner, _ => onClick());
            button.SetDesiredPosition(0, 0);
            ToolbarManager.AddLeftToolbarControl(button);
        }
    }
}
