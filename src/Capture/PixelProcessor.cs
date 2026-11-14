using System;

namespace HS2CameraSweep.Capture
{
    /// <summary>How the pixels read back from the GPU should be turned into the final image.</summary>
    internal enum PixelMode
    {
        /// <summary>No transparency: RGB out.</summary>
        Opaque,

        /// <summary>Already a finished RGBA image (Screencap's transparent output): copy as is.</summary>
        Straight,

        /// <summary>
        /// Built-in transparency: colour rendered over black (so edges are premultiplied by coverage) plus a
        /// separate alpha render. Colour is divided back out by alpha, so edges keep their real colour
        /// instead of a dark or background-coloured fringe.
        /// </summary>
        Premultiplied,
    }

    /// <summary>
    /// CPU side of a capture, run on a worker thread: flips the GPU's bottom-up rows, combines colour and
    /// alpha, and box-downscales whatever upsampling the GPU couldn't (odd factors). Averaging and
    /// un-premultiplying happen in linear light when the game renders in linear colour space.
    /// </summary>
    internal static class PixelProcessor
    {
        private static readonly float[] SrgbToLinear = BuildToLinear();
        private static readonly byte[] LinearToSrgb = BuildToSrgb();
        private const int ToSrgbSteps = 16383;

        /// <param name="color">RGBA32 bytes, bottom row first, srcW × srcH.</param>
        /// <param name="alpha">RGBA32 alpha render (Premultiplied mode only), same size.</param>
        /// <param name="factor">Downscale factor; srcW and srcH are multiples of it.</param>
        /// <param name="linear">The game renders in linear colour space (values are sRGB-encoded).</param>
        /// <param name="topDown">True for file encoders (top row first); false keeps Unity's bottom-up order.</param>
        /// <returns>Packed pixels, 3 bytes (Opaque) or 4 bytes per pixel.</returns>
        public static byte[] Process(byte[] color, byte[] alpha, int srcW, int srcH, int factor, PixelMode mode,
            bool linear, bool topDown, out int width, out int height, out int channels)
        {
            factor = Math.Max(1, factor);
            width = srcW / factor;
            height = srcH / factor;
            channels = mode == PixelMode.Opaque ? 3 : 4;
            var output = new byte[width * height * channels];

            if (factor == 1 && mode != PixelMode.Premultiplied)
            {
                CopyRows(color, srcW, height, channels, topDown, output);
                return output;
            }

            var toLin = linear ? SrgbToLinear : null;
            var inv = 1f / (factor * factor);

            for (var oy = 0; oy < height; oy++)
            {
                var outRow = (topDown ? height - 1 - oy : oy) * width * channels;
                for (var ox = 0; ox < width; ox++)
                {
                    var o = outRow + ox * channels;

                    if (factor == 1)
                    {
                        // Fully covered pixel: keep the rendered bytes exactly (no colour round trip).
                        var s1 = (oy * srcW + ox) * 4;
                        if (alpha[s1 + 3] == 255)
                        {
                            output[o] = color[s1];
                            output[o + 1] = color[s1 + 1];
                            output[o + 2] = color[s1 + 2];
                            output[o + 3] = 255;
                            continue;
                        }
                    }

                    float r = 0, g = 0, b = 0, a = 0;
                    for (var dy = 0; dy < factor; dy++)
                    {
                        var src = ((oy * factor + dy) * srcW + ox * factor) * 4;
                        for (var dx = 0; dx < factor; dx++, src += 4)
                        {
                            r += Lin(color[src], toLin);
                            g += Lin(color[src + 1], toLin);
                            b += Lin(color[src + 2], toLin);
                            a += mode == PixelMode.Premultiplied ? alpha[src + 3] : color[src + 3];
                        }
                    }

                    r *= inv; g *= inv; b *= inv;
                    a *= inv * (1f / 255f);

                    if (mode == PixelMode.Premultiplied)
                    {
                        if (a <= 0.5f / 255f)
                        {
                            // Fully transparent: drop whatever post-processing left on the black background.
                            output[o] = output[o + 1] = output[o + 2] = output[o + 3] = 0;
                            continue;
                        }

                        var k = 1f / a;
                        r *= k; g *= k; b *= k;
                    }

                    output[o] = Enc(r, linear);
                    output[o + 1] = Enc(g, linear);
                    output[o + 2] = Enc(b, linear);
                    if (channels == 4) output[o + 3] = (byte)(Math.Min(1f, a) * 255f + 0.5f);
                }
            }

            return output;
        }

        private static void CopyRows(byte[] rgba, int w, int h, int channels, bool topDown, byte[] output)
        {
            for (var y = 0; y < h; y++)
            {
                var src = y * w * 4;
                var dst = (topDown ? h - 1 - y : y) * w * channels;
                if (channels == 4)
                {
                    Buffer.BlockCopy(rgba, src, output, dst, w * 4);
                    continue;
                }

                for (var x = 0; x < w; x++, src += 4, dst += 3)
                {
                    output[dst] = rgba[src];
                    output[dst + 1] = rgba[src + 1];
                    output[dst + 2] = rgba[src + 2];
                }
            }
        }

        private static float Lin(byte v, float[] toLin) => toLin != null ? toLin[v] : v * (1f / 255f);

        private static byte Enc(float v, bool linear)
        {
            if (v <= 0f) return 0;
            if (v >= 1f) return 255;
            return linear ? LinearToSrgb[(int)(v * ToSrgbSteps + 0.5f)] : (byte)(v * 255f + 0.5f);
        }

        private static float[] BuildToLinear()
        {
            var t = new float[256];
            for (var i = 0; i < 256; i++)
            {
                var c = i / 255.0;
                t[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
            }
            return t;
        }

        private static byte[] BuildToSrgb()
        {
            var t = new byte[ToSrgbSteps + 1];
            for (var i = 0; i <= ToSrgbSteps; i++)
            {
                var l = i / (double)ToSrgbSteps;
                var s = l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
                t[i] = (byte)Math.Max(0, Math.Min(255, Math.Round(s * 255)));
            }
            return t;
        }
    }
}
