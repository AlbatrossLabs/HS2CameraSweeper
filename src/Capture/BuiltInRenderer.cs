using System;
using UnityEngine;

namespace HS2CameraSweep.Capture
{
    /// <summary>
    /// Camera Sweep's own renderer, the replacement for Screencap's render shot.
    ///
    /// Renders Studio's main camera into an offscreen texture at size × upsampling (no UI), with:
    ///   - depth of field blur scaled to the render size, as Screencap does;
    ///   - ambient occlusion optionally off (it bands when upsampled);
    ///   - transparency from two renders: one with all effects over a black background (colour) and one
    ///     without post-processing or DOF over transparent black (alpha). PixelProcessor divides the
    ///     colour by the alpha, so edges keep their own colour with no background fringe.
    /// Upsampling is undone on the GPU by exact 2× halvings (bilinear at half size = 2×2 average);
    /// an odd factor left over (3×) is averaged on the CPU.
    /// </summary>
    internal static class BuiltInRenderer
    {
        public sealed class Result
        {
            public RenderTexture Color;
            public RenderTexture Alpha;   // only when transparent
            public int CpuFactor = 1;     // remaining downscale for the CPU
            public bool Premultiplied;
        }

        public static Result Render(int width, int height, int upsampling, bool transparent, AmbientOcclusionSetting ao)
        {
            var cam = Camera.main;
            if (cam == null)
            {
                CameraSweepPlugin.Logger.LogError("No main camera to capture from.");
                return null;
            }

            var up = Mathf.Max(1, upsampling);
            var max = SystemInfo.maxTextureSize;
            while (up > 1 && (width * up > max || height * up > max)) up--;
            if (up != Mathf.Max(1, upsampling))
                CameraSweepPlugin.Logger.LogWarning($"Upsampling lowered to {up}× - {width}×{height} × {upsampling} is beyond the GPU's {max}px limit.");

            var w = width * up;
            var h = height * up;

            var dof = RenderOverrides.DepthOfField(cam);
            var blur = RenderOverrides.GetMaxBlur(dof);
            var aoChanged = ao == AmbientOcclusionSetting.Disable || (ao == AmbientOcclusionSetting.DisableWhenUpsampling && up > 1)
                ? RenderOverrides.DisableAO()
                : null;

            var result = new Result { Premultiplied = transparent };
            try
            {
                // Blur is measured in screen pixels; keep it the same share of the picture at any size.
                if (dof != null) RenderOverrides.SetMaxBlur(dof, blur * (Screen.height / (float)h) * up);

                result.Color = RenderCamera(cam, w, h, transparent);

                if (transparent)
                {
                    if (dof != null) RenderOverrides.SetMaxBlur(dof, blur);
                    var dofWasOn = dof != null && dof.enabled;
                    var pp = RenderOverrides.PostProcessLayer(cam);
                    var ppWasOn = pp != null && pp.enabled;
                    try
                    {
                        if (dofWasOn) dof.enabled = false;
                        if (ppWasOn) pp.enabled = false;
                        result.Alpha = RenderCamera(cam, w, h, true);
                    }
                    finally
                    {
                        if (ppWasOn) pp.enabled = true;
                        if (dofWasOn) dof.enabled = true;
                    }
                }
            }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogError("Built-in render failed: " + ex);
                Release(result);
                return null;
            }
            finally
            {
                if (dof != null) RenderOverrides.SetMaxBlur(dof, blur);
                RenderOverrides.RestoreAO(aoChanged);
            }

            var factor = up;
            while (factor % 2 == 0)
            {
                result.Color = Halve(result.Color);
                if (result.Alpha != null) result.Alpha = Halve(result.Alpha);
                factor /= 2;
            }

            result.CpuFactor = factor;
            return result;
        }

        public static void Release(Result r)
        {
            if (r == null) return;
            if (r.Color != null) RenderTexture.ReleaseTemporary(r.Color);
            if (r.Alpha != null) RenderTexture.ReleaseTemporary(r.Alpha);
            r.Color = r.Alpha = null;
        }

        private static RenderTexture RenderCamera(Camera cam, int w, int h, bool clearTransparent)
        {
            var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);

            var flags = cam.clearFlags;
            var background = cam.backgroundColor;
            var target = cam.targetTexture;
            var current = Camera.current;
            var currentTarget = current != null ? current.targetTexture : null;
            try
            {
                if (clearTransparent)
                {
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                }

                cam.targetTexture = rt;
                cam.Render();
            }
            finally
            {
                cam.clearFlags = flags;
                cam.backgroundColor = background;
                cam.targetTexture = target;
                if (current != null) current.targetTexture = currentTarget;
            }

            return rt;
        }

        /// <summary>Exact 2× downscale: a bilinear sample at the shared corner of each 2×2 block averages it.</summary>
        private static RenderTexture Halve(RenderTexture src)
        {
            var dst = RenderTexture.GetTemporary(src.width / 2, src.height / 2, 0, src.format, RenderTextureReadWrite.Default);
            src.filterMode = FilterMode.Bilinear;
            Graphics.Blit(src, dst);
            RenderTexture.ReleaseTemporary(src);
            return dst;
        }
    }
}
