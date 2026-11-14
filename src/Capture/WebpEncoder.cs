using System;
using System.IO;
using System.Runtime.InteropServices;

namespace HS2CameraSweep.Capture
{
    /// <summary>
    /// WebP output through libwebp (Google's encoder, BSD licence), shipped next to this plugin as
    /// libwebp_cs.dll: libwebp 1.4.0 plus a three-function wrapper (cs_webp_encode / cs_webp_free /
    /// cs_webp_version). Loaded by full path and called through function pointers, so it doesn't depend
    /// on Windows' DLL search path. Safe to call from worker threads.
    /// </summary>
    internal static class WebpEncoder
    {
        public const string LibraryFile = "libwebp_cs.dll";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate UIntPtr EncodeFn(IntPtr pixels, int width, int height, int stride, int channels,
            int lossless, float quality, int effort, out IntPtr output);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FreeFn(IntPtr p);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int VersionFn();

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string path);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        private static readonly object Gate = new object();
        private static bool _tried;
        private static EncodeFn _encode;
        private static FreeFn _free;

        public static bool Available
        {
            get
            {
                Load();
                return _encode != null;
            }
        }

        public static string Version { get; private set; } = "";

        /// <summary>Loads the library once. Call from the main thread at startup so problems show in the log early.</summary>
        public static void Load()
        {
            lock (Gate)
            {
                if (_tried) return;
                _tried = true;

                try
                {
                    var dir = Path.GetDirectoryName(typeof(WebpEncoder).Assembly.Location) ?? "";
                    var path = Path.Combine(dir, LibraryFile);
                    if (!File.Exists(path))
                    {
                        CameraSweepPlugin.Logger.LogWarning($"WebP unavailable: {LibraryFile} not found next to the plugin ({dir}). PNG will be used instead.");
                        return;
                    }

                    var module = LoadLibraryW(path);
                    if (module == IntPtr.Zero)
                    {
                        CameraSweepPlugin.Logger.LogWarning($"WebP unavailable: could not load {path} (Windows error {Marshal.GetLastWin32Error()}).");
                        return;
                    }

                    var enc = GetProcAddress(module, "cs_webp_encode");
                    var free = GetProcAddress(module, "cs_webp_free");
                    var ver = GetProcAddress(module, "cs_webp_version");
                    if (enc == IntPtr.Zero || free == IntPtr.Zero || ver == IntPtr.Zero)
                    {
                        CameraSweepPlugin.Logger.LogWarning($"WebP unavailable: {LibraryFile} is not the build Camera Sweep expects.");
                        return;
                    }

                    var v = ((VersionFn)Marshal.GetDelegateForFunctionPointer(ver, typeof(VersionFn)))();
                    Version = $"{v >> 16}.{(v >> 8) & 0xFF}.{v & 0xFF}";
                    _free = (FreeFn)Marshal.GetDelegateForFunctionPointer(free, typeof(FreeFn));
                    _encode = (EncodeFn)Marshal.GetDelegateForFunctionPointer(enc, typeof(EncodeFn));
                    CameraSweepPlugin.Logger.LogInfo("WebP encoder ready (libwebp " + Version + ")");
                }
                catch (Exception ex)
                {
                    CameraSweepPlugin.Logger.LogWarning("WebP unavailable: " + ex.Message);
                    _encode = null;
                }
            }
        }

        /// <summary>
        /// Encodes top-row-first pixels (3 = RGB, 4 = RGBA bytes per pixel) and writes the file.
        /// lossless: effort 0 (fastest) .. 9 (smallest). Lossy: quality 0..100, effort 0..6.
        /// </summary>
        public static void Write(string path, byte[] pixels, int width, int height, int channels,
            bool lossless, float quality, int effort)
        {
            if (!Available) throw new InvalidOperationException("WebP encoder not loaded");

            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            IntPtr output = IntPtr.Zero;
            try
            {
                var size = (long)_encode(handle.AddrOfPinnedObject(), width, height, width * channels, channels,
                    lossless ? 1 : 0, quality, effort, out output).ToUInt64();
                if (size <= 0 || output == IntPtr.Zero)
                    throw new IOException($"libwebp could not encode {width}x{height} image");

                var bytes = new byte[size];
                Marshal.Copy(output, bytes, 0, (int)size);
                File.WriteAllBytes(path, bytes);
            }
            finally
            {
                if (output != IntPtr.Zero) _free(output);
                handle.Free();
            }
        }
    }
}
