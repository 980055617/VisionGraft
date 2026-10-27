using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

// モデルの明るさを動画の明るさに合わせる（2026-10-25 ユーザー「車が全体的に明るいけど，元元動画見たいに暗い雰囲気に合わせれる？」）。
// 全部の動画で 1 つの決まり（動画ごとの値は持たない。10/16 ユーザー「動画ごとに違う処理はよくない」）:
//   S = 表示している動画（左目）の下半分の平均の明るさ（線形の輝度。画面のシェーダーが出す値と同じ空間）
//   k = clamp((S / modelLightReferenceLuminance) ^ modelLightExponent, modelLightMinScale, modelLightMaxScale)
//   モデルを照らす光をまとめて k 倍する: Directional Light の強さ・環境光（RenderSettings.ambientProbe）・環境の映り込み
//   （RenderSettings.reflectionIntensity）。光るもの（車のランプの Emission）は変えない。動画の画面は unlit なので変わらない。
// 下半分にするのは、モデル（人・動物・車・ボール）は地面の側にいて、上半分は空や天井で照らされ方を表さないため
// （car2 は夕焼けの空が明るく地面が暗い: 画面全体 0.150・下半分 0.030）。
// 値の出どころ（Q1 の調べ、scratchpad validate/feedback_inv/Q1）: 今のモデルは 6 本とも元の物より明るく（線形の中央値で
// 風船 0.76 倍・サッカー 0.59・犬 0.73・猫 0.31・テレビ 0.36・車 0.04〜0.10 倍にすると元と同じ）、下半分の明るさとの
// 両対数の当てはめの傾きが 0.6。基準 0.32 は風船の動画の S（ユーザーが「結構いい」と言った）で、風船は k = 1 のまま。
// 車（S 0.030）とテレビ（S 0.031）は S がほぼ同じなので同じ k（0.24）になる: 車は元の車より明るさがまだ残り、
// テレビの女性は元の女性よりわずかに暗くなる。この 2 本の兼ね合いは modelLightExponent で決まる。
// 実験では残る Directional Light は ExperimentScene のもの（試行シーンの分は ExperimentController が消す）で、
// 手と controller の見た目も同じ光で照らされる → 暗い動画では手も暗くなる。条件（置換あり・なし）で同じにするため、
// モデルを出していない条件でも同じ決まりで当てる。
public partial class StreamingStereoVideoPlayer
{
    [Header("Model light follows the video (2026-10-25)")]
    // false なら何もしない（光はシーンの値のまま）。
    public bool matchModelLightToVideo = false;
    // k = 1 になる S（線形の輝度、0〜1）。これより明るい動画は modelLightMaxScale で止まる。
    public float modelLightReferenceLuminance = 0.32f;
    // S の比の何乗で光を変えるか（1 = 灰色の仮定そのまま、0 = 変えない）。
    public float modelLightExponent = 0.6f;
    public float modelLightMinScale = 0.15f;
    // 1 より大きくしない = 今より明るくはしない。
    public float modelLightMaxScale = 1f;
    // 測る範囲: 左目の画像の下から何割か（0.5 = 下半分）。
    [Range(0.05f, 1f)] public float modelLightSampleRegionHeight = 0.5f;
    // S の時定数（秒）。カットで動画の明るさが変わったとき、この速さで光が追う。
    public float modelLightSmoothingSeconds = 1f;
    public int modelLightSampleEveryNFrames = 6;
    // 確かめ用: 0 より大きいと動画を見ずに k をこの値に固定する（バッチで「光が全部 k 倍になるか」を見る）。
    public float modelLightForceScale = -1f;
    public bool logModelLight = true;

    private const int ModelLightSampleWidth = 128;
    private const int ModelLightSampleHeight = 64;

    // Light の instance ID → 最初に見た強さ。ExperimentScene の光は試行をまたいで残るので、試行ごとの Start で
    // 今の強さ（前の試行が k 倍したままかもしれない）を読み直さない。static なので試行のシーンを読み直しても残る。
    private static readonly Dictionary<int, float> modelLightBaseIntensityById = new Dictionary<int, float>();

    private readonly List<Light> modelLightLights = new List<Light>();
    private RenderTexture modelLightSampleTexture;
    private bool modelLightReadbackPending;
    private int modelLightFramesSinceSample = int.MaxValue / 2;
    private float modelLightLuminanceTarget = -1f;
    private float modelLightLuminance = -1f;
    private float modelLightScale = 1f;
    private float modelLightLoggedScale = -1f;
    private float modelLightNextLogTime;
    private bool modelLightApplied;
    private bool modelLightLightsRebuilt;
    private bool modelLightRenderSettingsCaptured;
    private SphericalHarmonicsL2 modelLightBaseAmbientProbe;
    private float modelLightBaseReflectionIntensity = 1f;

    // UpdateRuntimePlaybackTick から毎フレーム（ApplyVideoFrameTexture の後）。
    private void UpdateModelLightFromVideo()
    {
        if (!matchModelLightToVideo)
        {
            if (modelLightApplied)
            {
                RestoreModelLight();
            }

            return;
        }

        float k;
        if (modelLightForceScale > 0f)
        {
            k = modelLightForceScale;
        }
        else
        {
            RequestModelLightSample();
            if (modelLightLuminanceTarget < 0f)
            {
                // まだ 1 回も測れていない（動画の最初のフレームの前）: シーンの光のまま。
                return;
            }

            if (modelLightLuminance < 0f || modelLightSmoothingSeconds <= 0f)
            {
                modelLightLuminance = modelLightLuminanceTarget;
            }
            else
            {
                float a = 1f - Mathf.Exp(-Time.unscaledDeltaTime / modelLightSmoothingSeconds);
                modelLightLuminance += (modelLightLuminanceTarget - modelLightLuminance) * a;
            }

            k = ModelLightScaleFor(modelLightLuminance, modelLightReferenceLuminance, modelLightExponent,
                modelLightMinScale, modelLightMaxScale);
        }

        ApplyModelLightScale(k);
        LogModelLightIfChanged(k);
    }

    // テスト用に外へ出す純粋な式。
    public static float ModelLightScaleFor(float luminance, float referenceLuminance, float exponent, float minScale, float maxScale)
    {
        if (luminance <= 0f || referenceLuminance <= 0f)
        {
            return Mathf.Clamp(1f, minScale, maxScale);
        }

        return Mathf.Clamp(Mathf.Pow(luminance / referenceLuminance, exponent), minScale, maxScale);
    }

    private void RequestModelLightSample()
    {
        if (modelLightReadbackPending || vp == null || vp.texture == null || !vp.isPrepared || vp.frame < 0 ||
            !SystemInfo.supportsAsyncGPUReadback)
        {
            return;
        }

        if (++modelLightFramesSinceSample < Mathf.Max(1, modelLightSampleEveryNFrames))
        {
            return;
        }

        modelLightFramesSinceSample = 0;
        if (modelLightSampleTexture == null)
        {
            // Linear: 動画のテクスチャを読んだ値（画面のシェーダーが出すのと同じ線形の値）をそのまま書く。
            modelLightSampleTexture = new RenderTexture(ModelLightSampleWidth, ModelLightSampleHeight, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            {
                name = "ModelLightSample",
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave,
            };
            modelLightSampleTexture.Create();
        }

        // 左目 = u 0〜0.5（左の画面の _UVScale (0.5, 1)・_UVOffset 0 と同じ。Screens.cs の ApplyStereoUvSettings）、
        // 下 = v 0〜modelLightSampleRegionHeight。
        RenderTexture previousActive = RenderTexture.active;
        Graphics.Blit(vp.texture, modelLightSampleTexture, new Vector2(0.5f, modelLightSampleRegionHeight), Vector2.zero);
        // Blit は RenderTexture.active を測る用のテクスチャのまま残すので戻す（確かめ役 V_Q1 の指摘）。
        RenderTexture.active = previousActive;
        modelLightReadbackPending = true;
        AsyncGPUReadback.Request(modelLightSampleTexture, 0, TextureFormat.RGBA32, OnModelLightReadback);
    }

    private void OnModelLightReadback(AsyncGPUReadbackRequest request)
    {
        modelLightReadbackPending = false;
        if (this == null || request.hasError)
        {
            return;
        }

        NativeArray<Color32> pixels = request.GetData<Color32>();
        if (pixels.Length == 0)
        {
            return;
        }

        double sum = 0.0;
        for (int i = 0; i < pixels.Length; i++)
        {
            Color32 c = pixels[i];
            sum += 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b;
        }

        modelLightLuminanceTarget = (float)(sum / (255.0 * pixels.Length));
    }

    private void ApplyModelLightScale(float k)
    {
        CaptureModelLightBases();
        // k が前の tick と同じなら書かない（毎フレーム光と RenderSettings を書き直さない、確かめ役 V_Q1 の指摘）。光の一覧が作り直されたとき
        // （試行のシーンを読み直した）は書く。
        if (modelLightApplied && !modelLightLightsRebuilt && Mathf.Abs(k - modelLightScale) < 0.0005f)
        {
            return;
        }

        modelLightLightsRebuilt = false;
        for (int i = 0; i < modelLightLights.Count; i++)
        {
            Light light = modelLightLights[i];
            if (light != null && modelLightBaseIntensityById.TryGetValue(light.GetInstanceID(), out float baseIntensity))
            {
                light.intensity = baseIntensity * k;
            }
        }

        // RenderSettings は今のアクティブなシーンのもの。自分のシーン（実験では SetActiveScene された TrialScene）が
        // アクティブなときだけ書く。ExperimentScene の設定を書き換えて残さないため。
        if (modelLightRenderSettingsCaptured && IsOwnSceneActiveForModelLight())
        {
            RenderSettings.ambientProbe = modelLightBaseAmbientProbe * k;
            RenderSettings.reflectionIntensity = modelLightBaseReflectionIntensity * k;
        }

        modelLightScale = k;
        modelLightApplied = true;
    }

    private void CaptureModelLightBases()
    {
        bool stale = modelLightLights.Count == 0;
        for (int i = 0; i < modelLightLights.Count && !stale; i++)
        {
            stale = modelLightLights[i] == null;
        }

        if (stale)
        {
            modelLightLightsRebuilt = true;
            modelLightLights.Clear();
            foreach (Light light in FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                // 車のランプなどの点・スポットの光は触らない（光るものは暗くしない）。
                if (light.type != LightType.Directional)
                {
                    continue;
                }

                modelLightLights.Add(light);
                int id = light.GetInstanceID();
                if (!modelLightBaseIntensityById.ContainsKey(id))
                {
                    modelLightBaseIntensityById[id] = light.intensity;
                }
            }
        }

        if (!modelLightRenderSettingsCaptured && IsOwnSceneActiveForModelLight())
        {
            modelLightBaseAmbientProbe = RenderSettings.ambientProbe;
            modelLightBaseReflectionIntensity = RenderSettings.reflectionIntensity;
            modelLightRenderSettingsCaptured = true;
        }
    }

    private bool IsOwnSceneActiveForModelLight()
    {
        return gameObject.scene.IsValid() && SceneManager.GetActiveScene() == gameObject.scene;
    }

    private void RestoreModelLight()
    {
        if (!modelLightApplied)
        {
            return;
        }

        for (int i = 0; i < modelLightLights.Count; i++)
        {
            Light light = modelLightLights[i];
            if (light != null && modelLightBaseIntensityById.TryGetValue(light.GetInstanceID(), out float baseIntensity))
            {
                light.intensity = baseIntensity;
            }
        }

        if (modelLightRenderSettingsCaptured && IsOwnSceneActiveForModelLight())
        {
            RenderSettings.ambientProbe = modelLightBaseAmbientProbe;
            RenderSettings.reflectionIntensity = modelLightBaseReflectionIntensity;
        }

        modelLightApplied = false;
        modelLightScale = 1f;
        modelLightLoggedScale = -1f;
    }

    // OnDestroy から。光を元に戻し、測る用のテクスチャを捨てる。
    private void DisposeModelLight()
    {
        RestoreModelLight();
        if (modelLightSampleTexture != null)
        {
            modelLightSampleTexture.Release();
            Destroy(modelLightSampleTexture);
            modelLightSampleTexture = null;
        }
    }

    private void LogModelLightIfChanged(float k)
    {
        if (!logModelLight)
        {
            return;
        }

        bool changed = modelLightLoggedScale < 0f || Mathf.Abs(k - modelLightLoggedScale) > 0.02f;
        if (!changed && Time.unscaledTime < modelLightNextLogTime)
        {
            return;
        }

        modelLightLoggedScale = k;
        modelLightNextLogTime = Time.unscaledTime + 10f;
        int enabledLights = 0;
        for (int i = 0; i < modelLightLights.Count; i++)
        {
            if (modelLightLights[i] != null && modelLightLights[i].isActiveAndEnabled)
            {
                enabledLights++;
            }
        }

        Debug.Log($"[MODELLIGHT] S={modelLightLuminance:F4} (target {modelLightLuminanceTarget:F4}) k={k:F3} applied={modelLightScale:F3} " +
                  $"force={(modelLightForceScale > 0f ? modelLightForceScale.ToString("F2") : "off")} lights={enabledLights} " +
                  $"renderSettings={(modelLightRenderSettingsCaptured && IsOwnSceneActiveForModelLight() ? "scaled" : "untouched")} " +
                  $"reflection={RenderSettings.reflectionIntensity:F3} frame={(vp != null ? vp.frame : -1)}");
    }
}
