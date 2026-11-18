#include "ort_shim.h"

#include <math.h>
#include <stdlib.h>
#include <string.h>
#include <onnxruntime/onnxruntime_c_api.h>

struct YYOrtZipVoice {
    const OrtApi *api;
    OrtEnv *env;
    OrtSessionOptions *opts;
    OrtSession *enc;
    OrtSession *dec;
    OrtSession *voc;
    OrtMemoryInfo *mem;
};

static int check_ok(const OrtApi *api, OrtStatus *st) {
    if (st != NULL) {
        api->ReleaseStatus(st);
        return 0;
    }
    return 1;
}

static uint64_t splitmix64(uint64_t *state) {
    *state += 0x9E3779B97F4A7C15ULL;
    uint64_t z = *state;
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
    return z ^ (z >> 31);
}

static void fill_standard_normal(float *buf, size_t n, uint64_t seed) {
    uint64_t st = seed ^ 0x9E3779B97F4A7C15ULL;
    size_t i = 0;
    while (i < n) {
        double u1 = (((double)(splitmix64(&st) >> 11)) + 0.5) * (1.0 / 9007199254740992.0);
        double u2 = (((double)(splitmix64(&st) >> 11)) + 0.5) * (1.0 / 9007199254740992.0);
        if (u1 < 1e-12) u1 = 1e-12;
        double r = sqrt(-2.0 * log(u1));
        double th = 6.28318530717958647692 * u2;
        buf[i++] = (float)(r * cos(th));
        if (i < n) buf[i++] = (float)(r * sin(th));
    }
}

YYOrtZipVoice *yy_ort_open(const char *encoder_path,
                           const char *decoder_path,
                           const char *vocos_path,
                           int32_t num_threads) {
    const OrtApiBase *base = OrtGetApiBase();
    if (!base) return NULL;
    const OrtApi *api = base->GetApi(ORT_API_VERSION);
    if (!api) return NULL;

    YYOrtZipVoice *h = (YYOrtZipVoice *)calloc(1, sizeof(YYOrtZipVoice));
    if (!h) return NULL;
    h->api = api;

    if (!check_ok(api, api->CreateEnv(ORT_LOGGING_LEVEL_ERROR, "yuyue-live", &h->env))) {
        yy_ort_close(h);
        return NULL;
    }
    if (!check_ok(api, api->CreateSessionOptions(&h->opts))) {
        yy_ort_close(h);
        return NULL;
    }
    int32_t threads = num_threads > 0 ? num_threads : 2;
    (void)check_ok(api, api->SetIntraOpNumThreads(h->opts, threads));
    (void)check_ok(api, api->SetInterOpNumThreads(h->opts, 1));
    (void)check_ok(api, api->DisableCpuMemArena(h->opts));
    (void)check_ok(api, api->DisableMemPattern(h->opts));
    (void)check_ok(api, api->SetSessionGraphOptimizationLevel(h->opts, ORT_ENABLE_ALL));

    if (!check_ok(api, api->CreateCpuMemoryInfo(OrtDeviceAllocator, OrtMemTypeDefault, &h->mem)) ||
        !check_ok(api, api->CreateSession(h->env, encoder_path, h->opts, &h->enc)) ||
        !check_ok(api, api->CreateSession(h->env, decoder_path, h->opts, &h->dec)) ||
        !check_ok(api, api->CreateSession(h->env, vocos_path, h->opts, &h->voc))) {
        yy_ort_close(h);
        return NULL;
    }
    return h;
}

void yy_ort_close(YYOrtZipVoice *h) {
    if (!h) return;
    const OrtApi *api = h->api;
    if (api) {
        if (h->voc) api->ReleaseSession(h->voc);
        if (h->dec) api->ReleaseSession(h->dec);
        if (h->enc) api->ReleaseSession(h->enc);
        if (h->mem) api->ReleaseMemoryInfo(h->mem);
        if (h->opts) api->ReleaseSessionOptions(h->opts);
        if (h->env) api->ReleaseEnv(h->env);
    }
    free(h);
}

int yy_ort_synth(YYOrtZipVoice *h,
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
                 YYVocosSpec *out) {
    if (!h || !out || n_tokens <= 0 || n_prompt_tokens <= 0 || prompt_frames <= 0 || num_steps <= 0) {
        return -1;
    }
    memset(out, 0, sizeof(YYVocosSpec));
    const OrtApi *api = h->api;
    int rc = -1;

    OrtValue *v_tok = NULL, *v_ptok = NULL, *v_plen = NULL, *v_spd = NULL;
    OrtValue *v_tc = NULL, *v_x = NULL, *v_sc = NULL, *v_gs = NULL;
    OrtValue *v_mels = NULL;
    OrtValue *voc_outs[3] = {NULL, NULL, NULL};
    float *x_buf = NULL, *sc_buf = NULL, *mel_buf = NULL;

    int64_t tok_shape[2] = {1, n_tokens};
    int64_t ptok_shape[2] = {1, n_prompt_tokens};
    int64_t scalar_shape[1] = {1};
    int64_t pframes_val = prompt_frames;
    float speed_val = speed > 0.0f ? speed : 1.0f;
    float gs_val = guidance_scale > 0.0f ? guidance_scale : 3.0f;

    if (!check_ok(api, api->CreateTensorWithDataAsOrtValue(h->mem, (void *)tokens, (size_t)n_tokens * sizeof(int64_t), tok_shape, 2, ONNX_TENSOR_ELEMENT_DATA_TYPE_INT64, &v_tok)) ||
        !check_ok(api, api->CreateTensorWithDataAsOrtValue(h->mem, (void *)prompt_tokens, (size_t)n_prompt_tokens * sizeof(int64_t), ptok_shape, 2, ONNX_TENSOR_ELEMENT_DATA_TYPE_INT64, &v_ptok)) ||
        !check_ok(api, api->CreateTensorWithDataAsOrtValue(h->mem, &pframes_val, sizeof(int64_t), scalar_shape, 0, ONNX_TENSOR_ELEMENT_DATA_TYPE_INT64, &v_plen)) ||
        !check_ok(api, api->CreateTensorWithDataAsOrtValue(h->mem, &speed_val, sizeof(float), scalar_shape, 0, ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &v_spd))) {
        goto cleanup;
    }

    const char *enc_in_names[4] = {"tokens", "prompt_tokens", "prompt_features_len", "speed"};
    const OrtValue *enc_ins[4] = {v_tok, v_ptok, v_plen, v_spd};
    const char *enc_out_names[1] = {"text_condition"};
    if (!check_ok(api, api->Run(h->enc, NULL, enc_in_names, enc_ins, 4, enc_out_names, 1, &v_tc))) {
        goto cleanup;
    }

    OrtTensorTypeAndShapeInfo *tc_info = NULL;
    if (!check_ok(api, api->GetTensorTypeAndShape(v_tc, &tc_info))) goto cleanup;
    int64_t tc_dims[3] = {0, 0, 0};
    (void)check_ok(api, api->GetDimensions(tc_info, tc_dims, 3));
    api->ReleaseTensorTypeAndShapeInfo(tc_info);

    int64_t total_frames = tc_dims[1];
    const int64_t feat_dim = 100;
    if (total_frames <= prompt_frames || tc_dims[2] != feat_dim) {
        goto cleanup;
    }

    size_t total_elems = (size_t)total_frames * (size_t)feat_dim;
    x_buf = (float *)malloc(total_elems * sizeof(float));
    sc_buf = (float *)calloc(total_elems, sizeof(float));
    if (!x_buf || !sc_buf) goto cleanup;

    fill_standard_normal(x_buf, total_elems, seed);
    memcpy(sc_buf, prompt_features, (size_t)prompt_frames * (size_t)feat_dim * sizeof(float));

    int64_t dec_shape[3] = {1, total_frames, feat_dim};
    if (!check_ok(api, api->CreateTensorWithDataAsOrtValue(h->mem, x_buf, total_elems * sizeof(float), dec_shape, 3, ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &v_x)) ||
        !check_ok(api, api->CreateTensorWithDataAsOrtValue(h->mem, sc_buf, total_elems * sizeof(float), dec_shape, 3, ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &v_sc)) ||
        !check_ok(api, api->CreateTensorWithDataAsOrtValue(h->mem, &gs_val, sizeof(float), scalar_shape, 0, ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &v_gs))) {
        goto cleanup;
    }

    const char *dec_in_names[5] = {"t", "x", "text_condition", "speech_condition", "guidance_scale"};
    const char *dec_out_names[1] = {"v"};
    for (int32_t step = 0; step < num_steps; ++step) {
        float t_val = timesteps[step];
        float dt = timesteps[step + 1] - timesteps[step];
        OrtValue *v_t = NULL, *v_vel = NULL;
        if (!check_ok(api, api->CreateTensorWithDataAsOrtValue(h->mem, &t_val, sizeof(float), scalar_shape, 0, ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &v_t))) {
            goto cleanup;
        }
        const OrtValue *dec_ins[5] = {v_t, v_x, v_tc, v_sc, v_gs};
        int ok = check_ok(api, api->Run(h->dec, NULL, dec_in_names, dec_ins, 5, dec_out_names, 1, &v_vel));
        api->ReleaseValue(v_t);
        if (!ok) {
            if (v_vel) api->ReleaseValue(v_vel);
            goto cleanup;
        }
        float *vel_ptr = NULL;
        if (!check_ok(api, api->GetTensorMutableData(v_vel, (void **)&vel_ptr))) {
            api->ReleaseValue(v_vel);
            goto cleanup;
        }
        for (size_t i = 0; i < total_elems; ++i) {
            x_buf[i] += vel_ptr[i] * dt;
        }
        api->ReleaseValue(v_vel);
    }

    int64_t kept = total_frames - prompt_frames;
    mel_buf = (float *)malloc((size_t)feat_dim * (size_t)kept * sizeof(float));
    if (!mel_buf) goto cleanup;
    const float *gen_start = x_buf + (size_t)prompt_frames * (size_t)feat_dim;
    for (int64_t t = 0; t < kept; ++t) {
        for (int64_t c = 0; c < feat_dim; ++c) {
            mel_buf[c * kept + t] = gen_start[t * feat_dim + c] * 10.0f; // 1.0 / feat_scale (0.1)
        }
    }

    int64_t voc_shape[3] = {1, feat_dim, kept};
    if (!check_ok(api, api->CreateTensorWithDataAsOrtValue(h->mem, mel_buf, (size_t)feat_dim * (size_t)kept * sizeof(float), voc_shape, 3, ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &v_mels))) {
        goto cleanup;
    }
    const char *voc_in_names[1] = {"mels"};
    const OrtValue *voc_ins[1] = {v_mels};
    const char *voc_out_names[3] = {"mag", "x", "y"};
    if (!check_ok(api, api->Run(h->voc, NULL, voc_in_names, voc_ins, 1, voc_out_names, 3, voc_outs))) {
        goto cleanup;
    }

    OrtTensorTypeAndShapeInfo *mag_info = NULL;
    if (!check_ok(api, api->GetTensorTypeAndShape(voc_outs[0], &mag_info))) goto cleanup;
    int64_t mag_dims[3] = {0, 0, 0};
    (void)check_ok(api, api->GetDimensions(mag_info, mag_dims, 3));
    api->ReleaseTensorTypeAndShapeInfo(mag_info);

    int64_t bins = mag_dims[1];
    int64_t voc_frames = mag_dims[2];
    if (bins != 513 || voc_frames <= 0) goto cleanup;

    size_t spec_bytes = (size_t)bins * (size_t)voc_frames * sizeof(float);
    out->mag = (float *)malloc(spec_bytes);
    out->x_cos = (float *)malloc(spec_bytes);
    out->y_sin = (float *)malloc(spec_bytes);
    if (!out->mag || !out->x_cos || !out->y_sin) {
        yy_ort_free_spec(out);
        goto cleanup;
    }
    void *p0 = NULL, *p1 = NULL, *p2 = NULL;
    if (!check_ok(api, api->GetTensorMutableData(voc_outs[0], &p0)) ||
        !check_ok(api, api->GetTensorMutableData(voc_outs[1], &p1)) ||
        !check_ok(api, api->GetTensorMutableData(voc_outs[2], &p2))) {
        yy_ort_free_spec(out);
        goto cleanup;
    }
    memcpy(out->mag, p0, spec_bytes);
    memcpy(out->x_cos, p1, spec_bytes);
    memcpy(out->y_sin, p2, spec_bytes);
    out->frames = voc_frames;
    rc = 0;

cleanup:
    for (int i = 0; i < 3; ++i) {
        if (voc_outs[i]) api->ReleaseValue(voc_outs[i]);
    }
    if (v_mels) api->ReleaseValue(v_mels);
    if (v_gs) api->ReleaseValue(v_gs);
    if (v_sc) api->ReleaseValue(v_sc);
    if (v_x) api->ReleaseValue(v_x);
    if (v_tc) api->ReleaseValue(v_tc);
    if (v_spd) api->ReleaseValue(v_spd);
    if (v_plen) api->ReleaseValue(v_plen);
    if (v_ptok) api->ReleaseValue(v_ptok);
    if (v_tok) api->ReleaseValue(v_tok);
    free(mel_buf);
    free(sc_buf);
    free(x_buf);
    return rc;
}

void yy_ort_free_spec(YYVocosSpec *spec) {
    if (!spec) return;
    free(spec->mag);
    free(spec->x_cos);
    free(spec->y_sin);
    memset(spec, 0, sizeof(YYVocosSpec));
}
