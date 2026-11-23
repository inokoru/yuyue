#ifndef YY_ORT_SHIM_H
#define YY_ORT_SHIM_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct YYOrtZipVoice YYOrtZipVoice;

typedef struct {
    float *mag;
    float *x_cos;
    float *y_sin;
    int64_t frames;
} YYVocosSpec;

YYOrtZipVoice *yy_ort_open(const char *encoder_path,
                           const char *decoder_path,
                           const char *vocos_path,
                           int32_t num_threads);

void yy_ort_close(YYOrtZipVoice *handle);

/// Runs ZipVoice encoder -> flow-matching decoder -> Vocos mel-to-STFT in one native pass.
/// Returns 0 on success (caller frees `out` with `yy_ort_free_spec`), non-zero on error.
int yy_ort_synth(YYOrtZipVoice *handle,
                 const int64_t *tokens,
                 int64_t n_tokens,
                 const int64_t *prompt_tokens,
                 int64_t n_prompt_tokens,
                 const float *prompt_features,
                 int64_t prompt_frames,
                 const float *timesteps,
                 int32_t num_steps,
                 float speed,
                 float guidance_scale,
                 uint64_t seed,
                 YYVocosSpec *out);

void yy_ort_free_spec(YYVocosSpec *spec);

typedef struct YYOrtKokoro YYOrtKokoro;

typedef struct {
    float *samples;
    int64_t count;
} YYKokoroAudio;

YYOrtKokoro *yy_ort_kokoro_open(const char *model_path,
                                const char *voices_path,
                                int32_t num_threads);

void yy_ort_kokoro_close(YYOrtKokoro *handle);

/// Synthesizes one Kokoro clause at 24 kHz mono (`tokens` are inner IDs without BOS/EOS `0` pad).
/// Returns 0 on success (caller frees `out` with `yy_ort_kokoro_free`), non-zero on error.
int yy_ort_kokoro_synth(YYOrtKokoro *handle,
                        const int64_t *tokens,
                        int64_t n_tokens,
                        int32_t sid,
                        float speed,
                        YYKokoroAudio *out);

void yy_ort_kokoro_free(YYKokoroAudio *audio);

#ifdef __cplusplus
}
#endif

#endif
