/* Small wrapper around libwebp's advanced encoder for Camera Sweep (P/Invoke friendly). */
#include <stdint.h>
#include <stddef.h>
#include "src/webp/encode.h"

#define API __declspec(dllexport)

API int cs_webp_version(void) { return WebPGetEncoderVersion(); }

/* pixels: top row first, 3 (RGB) or 4 (RGBA) bytes per pixel.
   lossless: 1 = lossless, effort 0 (fast) .. 9 (smallest). 0 = lossy at quality 0..100, effort = method 0..6.
   Returns the encoded size and sets *output (free with cs_webp_free), or 0 on failure. */
API size_t cs_webp_encode(const uint8_t* pixels, int width, int height, int stride, int channels,
                          int lossless, float quality, int effort, uint8_t** output)
{
    WebPConfig config;
    WebPPicture pic;
    WebPMemoryWriter writer;
    int ok;

    *output = NULL;
    if (!WebPConfigInit(&config) || !WebPPictureInit(&pic)) return 0;

    if (lossless) {
        if (effort < 0) effort = 0;
        if (effort > 9) effort = 9;
        if (!WebPConfigLosslessPreset(&config, effort)) return 0;
    } else {
        config.quality = quality;
        config.method = effort < 0 ? 0 : (effort > 6 ? 6 : effort);
        config.alpha_quality = 100;
        config.use_sharp_yuv = 1; /* cleaner colour edges at a small speed cost */
    }
    config.thread_level = 1;
    if (!WebPValidateConfig(&config)) return 0;

    pic.use_argb = lossless;
    pic.width = width;
    pic.height = height;
    ok = channels == 4 ? WebPPictureImportRGBA(&pic, pixels, stride)
                       : WebPPictureImportRGB(&pic, pixels, stride);
    if (!ok) { WebPPictureFree(&pic); return 0; }

    WebPMemoryWriterInit(&writer);
    pic.writer = WebPMemoryWrite;
    pic.custom_ptr = &writer;

    ok = WebPEncode(&config, &pic);
    WebPPictureFree(&pic);
    if (!ok) { WebPMemoryWriterClear(&writer); return 0; }

    *output = writer.mem;
    return writer.size;
}

API void cs_webp_free(void* p) { WebPFree(p); }
