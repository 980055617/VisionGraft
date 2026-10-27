using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// モデルの影の距離（2026-10-25、既定 Scene = 今のまま）。
// 起きていたこと: Quest とバッチの品質（Mobile_RPAsset）は主ライトの影を 50 m・1 段（cascade 1）・1024 で描く。URP は影の地図を
// カメラから 50 m までを包む球に合わせるので、影の地図の 1 画素（texel）がバッチの撮影カメラで約 8 cm、Quest の視野では
// 14〜19 cm（視野からの推定。下の [MODELSHADOW-PROBE] で実機の値を読む）になる。深さと法線のずらし（Mobile_RPAsset の
// bias 1・1）は texel 単位なので（URP ShadowUtils.cs:414-416）、ずらしも同じ 8〜19 cm。モデルはミニチュアで 0.12〜0.33 m
// （犬 0.12 m・テレビの女性 0.33 m）しかなく、体全体が影の地図の 1〜4 画素に収まる。その数画素の中心にどの部位（髪・頭・腕・裾）
// が当たるかで体の広い範囲が影になったり明るくなったりし、姿勢が数 mm 動くたびに入れ替わる（動画のフレームが進むたびに服に
// 縁のくっきりした暗い板が出たり消えたりする）。
// FitToScreenDistance: 影の距離を「画面の距離 × modelShadowDistanceScreenMultiple」にする。texel もずらしも同じ割合で縮む
//   （画面 1 m・2 倍 = 2 m で、バッチ約 0.33 cm・Quest 約 0.55〜0.76 cm の推定）。モデルの大きさは置いた深さに比例するので、
//   texel とモデルの比は画面の距離によらない。⑧ は画面の奥（allowDepthBehindScreen、画面の距離の 3 倍まで）にも置けるが、
//   実験の 6 本では一番遠いモデルでも 1.04 m（10/25 の撮影、確かめ役 V_O5）。画面の距離は実験中に 0.5〜3 m で変えられる
//   （Core.cs RuntimeScreenDistanceMin/MaxMeters）ので毎 tick 見て付け直す。
// Off: 影の距離を 0 にする（URP は影を描かない: UniversalRenderPipeline.cs:1474-1475 で maxShadowDistance = 0）。
// 床の影はもともと無い（動画の画面は Background の unlit で ShadowCaster も影の受け取りも無い。床の物も無い）。
// 変わるのはモデル自身の影・モデルどうしの影（人とボール・犬とボール・車）と、実験の手と controller の影（同じ光・同じ asset）。
// 品質の asset はシーンの外の物で、Editor では play を抜けても書いた値が残る（10/23 の skinWeights と同じ罠）。
// 最初に見た値を static に持ち、OnDisable / OnDestroy で戻す。
public partial class StreamingStereoVideoPlayer
{
    public enum ModelShadowMode
    {
        // 何もしない（品質の asset の値のまま。Mobile_RPAsset は 50 m）。
        Scene = 0,
        // 影の距離 = screenDistanceMeters × modelShadowDistanceScreenMultiple。
        FitToScreenDistance = 1,
        // 影を描かない（影の距離 0）。
        Off = 2,
    }

    [Header("Model shadow distance (2026-10-25)")]
    public ModelShadowMode modelShadowMode = ModelShadowMode.Scene;
    // 2: 全部の影が付くのは画面の距離の 1.6 倍まで（URP は最後の 20% で消していく: cascadeBorder 0.2）。実験の 6 本の
    // モデルは 1.04 m 以内（2026-10-25 の撮影で動物 1.01 m・車の往復 0.2〜0.7 m）。画面の奥 3 倍まで置かれるモデルでも
    // 影を消さないなら 4（texel は 2 倍）。
    [Min(1f)] public float modelShadowDistanceScreenMultiple = 2f;
    public bool logModelShadow = true;
    // 実機で texel を測る: 10 秒に 1 回、視点のカメラを描き終えたときの _ShadowBias.x（= -depthBias × texel）を出す。
    // バッチの [SHADOWPARAMS] は撮影カメラ（縦 44°）の値なので、Quest の視野での値はこれで logcat から読む。
    // 既定 false（確かめ役 V_O5: 既定 OFF の切り替えが毎 10 秒ログを足さないように）。測るときだけ true にする。
    public bool logModelShadowProbe = false;

    // 品質の asset（RenderPipelineAsset）の instance ID → 最初に見た影の距離。試行のシーンを読み直しても残る。
    private static readonly Dictionary<int, float> modelShadowBaseDistanceByAssetId = new Dictionary<int, float>();

    private UniversalRenderPipelineAsset modelShadowAsset;
    private bool modelShadowApplied;
    private bool modelShadowProbeSubscribed;
    private float modelShadowNextProbeTime;

    // UpdateRuntimePlaybackTick から毎フレーム（UpdateModelLightFromVideo の後）。
    private void UpdateModelShadow()
    {
        if (logModelShadowProbe && !modelShadowProbeSubscribed)
        {
            RenderPipelineManager.endCameraRendering += OnModelShadowEndCameraRendering;
            modelShadowProbeSubscribed = true;
        }

        UniversalRenderPipelineAsset asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (asset != modelShadowAsset)
        {
            // 品質の段が変わった（バッチの -qualityLevel など）: 前の asset を戻してから今の asset に当てる。
            RestoreModelShadow();
            modelShadowAsset = asset;
        }

        if (asset == null)
        {
            return;
        }

        if (modelShadowMode == ModelShadowMode.Scene)
        {
            if (modelShadowApplied)
            {
                RestoreModelShadow();
            }

            return;
        }

        int id = asset.GetInstanceID();
        if (!modelShadowBaseDistanceByAssetId.TryGetValue(id, out float baseDistance))
        {
            baseDistance = asset.shadowDistance;
            modelShadowBaseDistanceByAssetId[id] = baseDistance;
        }

        float target = modelShadowMode == ModelShadowMode.Off
            ? 0f
            : Mathf.Max(0.1f, screenDistanceMeters) * Mathf.Max(1f, modelShadowDistanceScreenMultiple);
        if (modelShadowApplied && Mathf.Abs(asset.shadowDistance - target) < 0.001f)
        {
            return;
        }

        asset.shadowDistance = target;
        modelShadowApplied = true;
        if (logModelShadow)
        {
            Debug.Log($"[MODELSHADOW] mode={modelShadowMode} asset={asset.name} shadowDistance {baseDistance:F2} -> {asset.shadowDistance:F2} m " +
                      $"(screen {screenDistanceMeters:F2} m x {modelShadowDistanceScreenMultiple:F2}) cascades={asset.shadowCascadeCount} " +
                      $"res={asset.mainLightShadowmapResolution} bias={asset.shadowDepthBias:F2}/{asset.shadowNormalBias:F2}");
        }
    }

    // 品質の asset の影の距離を最初に見た値へ戻す（Scene に切り替えたとき・asset が替わったとき）。
    private void RestoreModelShadow()
    {
        if (!modelShadowApplied)
        {
            return;
        }

        modelShadowApplied = false;
        if (modelShadowAsset == null ||
            !modelShadowBaseDistanceByAssetId.TryGetValue(modelShadowAsset.GetInstanceID(), out float baseDistance))
        {
            return;
        }

        modelShadowAsset.shadowDistance = baseDistance;
        if (logModelShadow)
        {
            Debug.Log($"[MODELSHADOW] restored {modelShadowAsset.name} shadowDistance={baseDistance:F2} m");
        }
    }

    // OnDisable / OnDestroy から。影の距離を戻し、測るための購読を外す。
    private void ShutdownModelShadow()
    {
        RestoreModelShadow();
        if (modelShadowProbeSubscribed)
        {
            RenderPipelineManager.endCameraRendering -= OnModelShadowEndCameraRendering;
            modelShadowProbeSubscribed = false;
        }
    }

    private void OnModelShadowEndCameraRendering(ScriptableRenderContext context, Camera cam)
    {
        if (!logModelShadowProbe || cam == null || cam.cameraType != CameraType.Game || Time.unscaledTime < modelShadowNextProbeTime)
        {
            return;
        }

        Camera view = GetViewCamera();
        if (view != null && cam != view)
        {
            return;
        }

        modelShadowNextProbeTime = Time.unscaledTime + 10f;
        UniversalRenderPipelineAsset asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        float depthBias = asset != null && asset.shadowDepthBias > 1e-6f ? asset.shadowDepthBias : 1f;
        Vector4 sb = Shader.GetGlobalVector("_ShadowBias");
        // _ShadowBias は直前に描いた影の pass の値（Mobile は追加ライトの影が無いので主ライトの値）。1 フレーム前の値のこともある。
        Debug.Log($"[MODELSHADOW-PROBE] cam={cam.name} stereo={cam.stereoEnabled} mode={modelShadowMode} " +
                  $"shadowDistance={(asset != null ? asset.shadowDistance : -1f):F2} _ShadowBias=({sb.x:F5},{sb.y:F5}) " +
                  $"texel={-sb.x / depthBias * 100f:F2} cm");
    }
}
