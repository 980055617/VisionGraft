using System.Collections.Generic;
using UnityEngine;

// 被験者実験の Model ボタン = 既定1 ⇄ 既定2 の切り替え（2026-10-24 ユーザー「被験者実験に関してはmodelを押すとそのwindowが
// 出てくるのではなくモデルを既定1,既定2で切り替えれるようにするだけでいいです」。組は ExperimentModelPresets の表）。
// 置換ありの試行と、その練習で使う（ExperimentTrialRequest.UsesModelPresetToggle）。一覧の窓は開かず、再生も止めない。
//   - 読み込み時（RestoreTrackCustomization の後）に、表の track を既定1 の名前で預ける（pendingModelNameByTrack）。
//     研究者の基準ファイル（model_selection.json）とセッション上書きのモデル名は、表の track では使わない（向き・大きさのキーはそのまま）
//   - 押すたびに表の track を全部もう一方の組へ。名前を預け、track が次に置かれるときに index へ直す（ResolvePendingModelSelection）。
//     まだ映っていない track（car2 の 2・3 台目など）は、現れたときに今の組で出る。今映っている track はピッカーで選んだときと同じく
//     インスタンスを作り直す（RecreateTrackInstanceForModelSelection。走っている自発的なモーションは、keepInteractiveMotionOnModelChange
//     （既定 ON、10/24 夜）で止めずに新しいモデルで続く。追従へ戻す最後の混ぜの途中ならそこで終わる）。映っていないのに残っている
//     インスタンス（画面の外へ進み続ける車）は作り直さない（押した瞬間に消えないように）
//   - 保存しない（PersistModelSelection を呼ばない）。試行のやり直し・次の試行は必ず既定1 から始まる
//   - 記録: operations.csv に押すたびに change_model を 1 行（via=toggle preset=1|2 tracks=<track>:<prefab>|… now=<その場で作り直した track>|…）。
//     1 行 = 被験者の 1 回の操作（ピッカーと同じ）。track ごとに実際に出たモデルは、インスタンスを作るたびの model_assigned で分かる
//   - モデルを掴んでいる間（もう一方の手でつまみを掴んで回している）は切り替えない（作り直すと掴みが外れる）
public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // 実験の指示。ApplyPendingExperimentTrialRequest で入れる（シーンに焼き込まない）。
    [System.NonSerialized] private bool experimentModelPresetToggle;

    // バッチ検証専用（batchmode のときだけ効く）。Model を押す meta のフレーム（カンマ区切り）。空でなければ、置換ありの試行と同じく
    // 既定1 で始め、そのフレームに達した tick で押す（同じ ToggleExperimentModelPreset）。
    public string batchModelPresetToggleFrames = "";

    // 切り替えで替わったモデルを、前と同じ大きさ・同じ基準で出す（2026-10-24、既定 ON。切り替えのときだけ効く。ピッカーの差し替えは変えない）。
    // 実測（10/24、Docs/tmp/meshy_models_20261024/README.md）: 動物は別の prefab に替えると倍率（scaleRefineFactorByTrack）を捨て、
    // 替えた tick の姿勢で測り直す（GetOrLockModelLocalScale、10/10）ので、A → B → A で A の大きさが変わった（練習の Labrador 0.368 → 0.304、
    // 猫 ×1.19 → ×2.66）。戻した直後は ⑧ の奥行きの平滑が前のモデルの値から寄っていくので、画面上 45% 大きい・48% 小さいまま 1 秒ほど続いた。
    // ON のとき、ロックを作り直す tick（PrepareModelPresetRelock、GetOrLockModelLocalScale の入口）で:
    //   1. 離れる prefab の倍率を track × prefab でしまい、戻る prefab の分があれば戻す（同じ prefab の持ち越しに乗る。測り直さない）
    //   2. モデルが替わったら ⑧ の奥行きの平滑を消し、新しいモデルの値から始め直す（SmoothProjectedDepthRatio の「モデル変更直後は平滑化せず」）
    // 倍率を決める基準フレームも、同じ shot の中で前に決めた基準を使い直す（TryResolveModelPresetRelockReference。shot の途中から現れる
    // Video4 の女性で、押したフレームが基準になり押す時刻で大きさが変わっていた）。
    // 初めて出す prefab は今までどおり、出した tick の姿勢で測る（押す時刻で 1 割ほど変わりうる）。
    public bool modelPresetToggleKeepsScalePerModel = true;

    private struct ModelPresetScaleStash
    {
        public float factor;
        public int referenceFrame;
        public bool hasBoneRatio;
        public float boneRatio;
        public bool hasSilhouette;
        public bool silhouette;
        public bool hasFullBodyGain;
        public float fullBodyGain;
    }

    private int currentModelPreset = ExperimentModelPresets.First;
    private readonly List<ExperimentModelPreset> activeModelPresets = new List<ExperimentModelPreset>();
    private readonly List<MetaObj> modelPresetFrameObjects = new List<MetaObj>(16);
    private readonly List<MetaObj> modelPresetReferenceObjects = new List<MetaObj>(16);
    private readonly HashSet<int> batchModelPresetToggleDone = new HashSet<int>();
    private readonly Dictionary<(uint, GameObject), ModelPresetScaleStash> modelPresetScaleStash =
        new Dictionary<(uint, GameObject), ModelPresetScaleStash>();
    // track ごとに、最後にロックを作ったときの prefab（モデルが替わったかを見る）。
    private readonly Dictionary<uint, GameObject> modelPresetLastLockedPrefabByTrack = new Dictionary<uint, GameObject>();

    private bool UsesModelPresetToggle
    {
        get
        {
            return experimentModelPresetToggle ||
                   (Application.isBatchMode && !string.IsNullOrEmpty(batchModelPresetToggleFrames));
        }
    }

    private bool IsModelPresetTrack(uint trackId)
    {
        for (int i = 0; i < activeModelPresets.Count; i++)
        {
            if (activeModelPresets[i].trackId == trackId)
            {
                return true;
            }
        }

        return false;
    }

    // 読み込み時。RestoreTrackCustomization（と ApplyBatchManualOverrideSpecs）の後、最初のインスタンス生成より前に呼ぶ。
    private void SeedExperimentModelPresets()
    {
        activeModelPresets.Clear();
        currentModelPreset = ExperimentModelPresets.First;
        if (!UsesModelPresetToggle)
        {
            return;
        }

        string bundle = System.IO.Path.GetFileName(bundleFileName);
        activeModelPresets.AddRange(ExperimentModelPresets.ForBundle(ExperimentModelPresets.Entries, bundle));
        if (activeModelPresets.Count == 0)
        {
            Debug.LogWarning($"[ModelPreset] {bundle} は表（ExperimentModelPresets）に無いので、Model ボタンは何もしない");
            return;
        }

        foreach (ExperimentModelPreset entry in activeModelPresets)
        {
            selectedModelIndexByTrack.Remove(entry.trackId);
            pendingModelNameByTrack[entry.trackId] = entry.NameFor(currentModelPreset);
        }

        CheckModelPresetCategoriesAgainstMeta(bundle);
        Debug.Log($"[ModelPreset] seed bundle={bundle} preset={currentModelPreset} {DescribeActiveModelPresets()}");
    }

    // 表の track の種類が meta.bin と合っているかを確かめる（記録だけ。bundle を作り直して track の番号・種類が変わったときに気づくため）。
    // 種類が違うと、名前がその種類の一覧に無く、既定のモデル（00_*）に落ちる。
    private void CheckModelPresetCategoriesAgainstMeta(string bundle)
    {
        int frames = manifest != null ? manifest.num_frames : 0;
        var seen = new Dictionary<uint, byte>();
        for (int f = 0; f < frames && seen.Count < activeModelPresets.Count; f++)
        {
            if (!TryReadFrameObjects(f, modelPresetReferenceObjects))
            {
                continue;
            }

            for (int i = 0; i < modelPresetReferenceObjects.Count; i++)
            {
                MetaObj obj = modelPresetReferenceObjects[i];
                if (!seen.ContainsKey(obj.trackId) && IsModelPresetTrack(obj.trackId))
                {
                    seen[obj.trackId] = obj.categoryId;
                }
            }
        }

        foreach (ExperimentModelPreset entry in activeModelPresets)
        {
            if (!seen.TryGetValue(entry.trackId, out byte categoryId))
            {
                Debug.LogError($"[ModelPreset] {bundle} の track {entry.trackId} が meta.bin に 1 フレームも無い（表 ExperimentModelPresets を確かめる）");
                continue;
            }

            string actual = ResolveRuntimeModelPickerCategoryLabel(categoryId);
            if (actual != entry.category)
            {
                Debug.LogError($"[ModelPreset] {bundle} の track {entry.trackId} は meta.bin では {actual}、表では {entry.category}（表を確かめる）");
            }
        }
    }

    // Model ボタン（ToggleRuntimeModelPickerPanel から）。
    private void ToggleExperimentModelPreset()
    {
        if (activeModelPresets.Count == 0)
        {
            Debug.LogWarning("[ModelPreset] 表に無い動画なので切り替えない");
            return;
        }

        // もう一方の手でモデルを掴んで回している間は切り替えない。作り直すと掴む対象が消え、掴みが外れて動画も動き出す
        // （UpdateGrabRotate が「掴む対象がまだ無い」で終わらせる。2026-10-24 の点検）。
        if (grabRotateActive)
        {
            Debug.Log("[ModelPreset] モデルを掴んでいる間は切り替えない");
            return;
        }

        currentModelPreset = ExperimentModelPresets.Next(currentModelPreset);
        int frame = GetCurrentPlaybackFrame();
        TryReadFrameObjects(frame, modelPresetFrameObjects);
        var models = new List<string>(activeModelPresets.Count);
        var now = new List<string>(activeModelPresets.Count);
        foreach (ExperimentModelPreset entry in activeModelPresets)
        {
            string name = entry.NameFor(currentModelPreset);
            pendingModelNameByTrack[entry.trackId] = name;
            models.Add($"{entry.trackId}:{name}");
            bool onScreen = false;
            for (int i = 0; i < modelPresetFrameObjects.Count; i++)
            {
                if (modelPresetFrameObjects[i].trackId == entry.trackId)
                {
                    onScreen = true;
                    break;
                }
            }

            // 画面の端から出て進んでいる人・動物（BodyFrameOut.partial.cs）は、車の慣性と同じく作り直さない（押した瞬間に消えないように）。
            if (onScreen && !IsBodyFrameOutActive(entry.trackId))
            {
                RecreateTrackInstanceForModelSelection(entry.trackId);
                now.Add(entry.trackId.ToString());
            }
        }

        // どの試行でどのモデルを見ていたかは分析の交絡要因になり得るので残す。1 回の押下につき 1 行（ピッカーと同じく 1 行 = 1 操作）。
        // tracks は押した後の組、now はその場で作り直した track（ほかは現れたときに替わる。実際に出たモデルは model_assigned）。
        ExperimentLog.Operation(
            "change_model",
            $"via=toggle preset={currentModelPreset} tracks={string.Join("|", models)} now={(now.Count > 0 ? string.Join("|", now) : "-")}");
        Debug.Log($"[ModelPreset] press f={frame} preset={currentModelPreset} {DescribeActiveModelPresets()} now={(now.Count > 0 ? string.Join("|", now) : "-")}");
    }

    // バッチ検証の押下（DisplayModelTick の頭、batchSwapModelSpec と同じ時点）。
    private void ApplyBatchModelPresetToggleForFrame(int frame)
    {
        if (!Application.isBatchMode || string.IsNullOrEmpty(batchModelPresetToggleFrames))
        {
            return;
        }

        string[] parts = batchModelPresetToggleFrames.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            if (batchModelPresetToggleDone.Contains(i))
            {
                continue;
            }

            // "m<秒>" / "h<秒>": Random のイベントが動き始めて（m）・追従へ戻す混ぜに入って（h）から <秒> 経った tick に押す（2026-10-24、
            // keepInteractiveMotionOnModelChange の確かめ用。イベント中は動画が止まりフレームが進まないので、フレームでは途中を狙えない）。
            string part = parts[i].Trim();
            if (part.Length > 1 && (part[0] == 'm' || part[0] == 'h'))
            {
                if (!float.TryParse(part.Substring(1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float afterSeconds))
                {
                    Debug.LogWarning($"[ModelPreset] batchModelPresetToggleFrames を解釈できません: '{parts[i]}'");
                    batchModelPresetToggleDone.Add(i);
                    continue;
                }

                if (!TryGetRandomMotionElapsedForBatch(part[0] == 'h', out uint motionTrack, out float elapsed) || elapsed < afterSeconds)
                {
                    continue;
                }

                batchModelPresetToggleDone.Add(i);
                Debug.Log($"[ModelPreset] batch press at {(part[0] == 'h' ? "handoff" : "motion")} +{elapsed:F2}s track={motionTrack} f={frame}");
                ToggleExperimentModelPreset();
                continue;
            }

            if (!int.TryParse(part, out int atFrame))
            {
                Debug.LogWarning($"[ModelPreset] batchModelPresetToggleFrames を解釈できません: '{parts[i]}'");
                batchModelPresetToggleDone.Add(i);
                continue;
            }

            if (frame < atFrame)
            {
                continue;
            }

            batchModelPresetToggleDone.Add(i);
            ToggleExperimentModelPreset();
        }
    }

    // バッチの "m<秒>" / "h<秒>" 用: 走っている Random イベント（handoff = false なら動いている段、true なら追従へ戻す混ぜ）の経過秒数。
    private bool TryGetRandomMotionElapsedForBatch(bool handoff, out uint trackId, out float elapsed)
    {
        trackId = 0;
        elapsed = 0f;
        float now = GetRuntimeTickContext().now;
        foreach (KeyValuePair<uint, InteractiveMotionState> kv in interactiveMotionByTrack)
        {
            InteractiveMotionState state = kv.Value;
            if (state == null || state.triggerSource != InteractiveTriggerSource.Random ||
                state.stage != (handoff ? InteractiveEventStage.HandoffBlend : InteractiveEventStage.Owned))
            {
                continue;
            }

            float start = handoff ? state.handoffStartTime : (state.subject == InteractiveMotionSubject.Rigid ? state.phaseStartTime : state.eventStartTime);
            trackId = kv.Key;
            elapsed = RuntimeClock.ResolveElapsed(now, start);
            return true;
        }

        return false;
    }

    // modelPresetToggleKeepsScalePerModel: 切り替えで作り直した表の track は、同じ shot の中で前に倍率を決めた基準フレームで決め直す。
    // ApplyReplaceableModelTransform がロックを作る tick に呼ぶ（shot の先頭に居る track は、もともと毎回 shot の先頭が基準なので通らない）。
    private bool TryResolveModelPresetRelockReference(MetaObj obj, out MetaObj referenceObj, out int referenceFrame)
    {
        referenceObj = default;
        referenceFrame = -1;
        if (!UsesModelPresetToggle || !modelPresetToggleKeepsScalePerModel || !IsModelPresetTrack(obj.trackId) ||
            !scaleLockReferenceFrameByTrack.TryGetValue(obj.trackId, out int frame) || frame < 0)
        {
            return false;
        }

        int current = GetCurrentPlaybackFrame();
        if (frame == current || shotBoundaries.ResolveShotIndex(frame) != shotBoundaries.ResolveShotIndex(current) ||
            !TryReadFrameObjects(frame, modelPresetReferenceObjects))
        {
            return false;
        }

        for (int i = 0; i < modelPresetReferenceObjects.Count; i++)
        {
            MetaObj candidate = modelPresetReferenceObjects[i];
            if (candidate.trackId == obj.trackId && candidate.bboxH > 0 && candidate.bboxW > 0)
            {
                referenceObj = candidate;
                referenceFrame = frame;
                return true;
            }
        }

        return false;
    }

    // modelPresetToggleKeepsScalePerModel: GetOrLockModelLocalScale の入口（ロックを作り直す tick）で呼ぶ。
    // 押した瞬間ではなくここで行うのは、映っていない間に押した track もここを通るため（現れたときにインスタンスが替わる）。
    private void PrepareModelPresetRelock(uint trackId)
    {
        if (!UsesModelPresetToggle || !modelPresetToggleKeepsScalePerModel || !IsModelPresetTrack(trackId) ||
            !trackPrefabSources.TryGetValue(trackId, out GameObject currentPrefab) || currentPrefab == null)
        {
            return;
        }

        bool modelChanged = modelPresetLastLockedPrefabByTrack.TryGetValue(trackId, out GameObject lastPrefab) &&
                            lastPrefab != null && lastPrefab != currentPrefab;
        modelPresetLastLockedPrefabByTrack[trackId] = currentPrefab;

        // 1. 離れる prefab の倍率をしまい、戻る prefab の分があれば戻す。
        bool hasFactor = scaleRefineFactorByTrack.TryGetValue(trackId, out float factor) &&
                         scaleRefineFactorPrefabByTrack.TryGetValue(trackId, out GameObject factorPrefab) && factorPrefab != null;
        GameObject leavingPrefab = hasFactor ? scaleRefineFactorPrefabByTrack[trackId] : null;
        if (hasFactor && leavingPrefab != currentPrefab)
        {
            ModelPresetScaleStash stash = new ModelPresetScaleStash
            {
                factor = factor,
                referenceFrame = scaleRefineFactorReferenceFrameByTrack.TryGetValue(trackId, out int referenceFrame) ? referenceFrame : -1,
            };
            stash.hasBoneRatio = scaleRefineFactorBoneRatioByTrack.TryGetValue(trackId, out stash.boneRatio);
            stash.hasSilhouette = scaleRefineFactorRatioSilhouetteByTrack.TryGetValue(trackId, out stash.silhouette);
            stash.hasFullBodyGain = animalFullBodyGainByTrack.TryGetValue(trackId, out stash.fullBodyGain);
            modelPresetScaleStash[(trackId, leavingPrefab)] = stash;
        }

        if ((!hasFactor || leavingPrefab != currentPrefab) &&
            modelPresetScaleStash.TryGetValue((trackId, currentPrefab), out ModelPresetScaleStash back))
        {
            // しまったときと同じ基準フレーム（= 同じ shot・同じ基準）のときだけ戻す。RecordScaleLockReferenceFrame は呼び出し元で済んでいる。
            if (scaleLockReferenceFrameByTrack.TryGetValue(trackId, out int referenceFrameNow) && referenceFrameNow == back.referenceFrame &&
                (!back.hasSilhouette || back.silhouette == useSilhouetteProjectionExtent))
            {
                RestoreModelPresetScale(trackId, currentPrefab, back);
            }
            else if (logPlacementMeasurement)
            {
                Debug.Log($"[ModelPreset] track={trackId} {currentPrefab.name} の倍率はしまったときと基準が違うので戻さない " +
                          $"(ref {back.referenceFrame} → {(scaleLockReferenceFrameByTrack.TryGetValue(trackId, out int r) ? r : -1)})");
            }
        }

        // 2. モデルが替わったら ⑧ の奥行きの平滑を新しいモデルの値から始め直す。消さないと前のモデルの比から 1 秒ほどかけて寄っていく。
        if (modelChanged)
        {
            smoothedProjectedDepthRatioByTrack.Remove(trackId);
            smoothedDepthRatioFrameByTrack.Remove(trackId);
            smoothedDepthRatioPrefabByTrack.Remove(trackId);
            if (logPlacementMeasurement)
            {
                Debug.Log($"[ModelPreset] track={trackId} {lastPrefab.name} → {currentPrefab.name}: ⑧ の奥行きの平滑を始め直す");
            }
        }
    }

    private void RestoreModelPresetScale(uint trackId, GameObject prefab, ModelPresetScaleStash back)
    {
        scaleRefineFactorByTrack[trackId] = back.factor;
        scaleRefineFactorPrefabByTrack[trackId] = prefab;
        scaleRefineFactorReferenceFrameByTrack[trackId] = back.referenceFrame;
        if (back.hasBoneRatio)
        {
            scaleRefineFactorBoneRatioByTrack[trackId] = back.boneRatio;
        }
        else
        {
            scaleRefineFactorBoneRatioByTrack.Remove(trackId);
        }

        if (back.hasSilhouette)
        {
            scaleRefineFactorRatioSilhouetteByTrack[trackId] = back.silhouette;
        }
        else
        {
            scaleRefineFactorRatioSilhouetteByTrack.Remove(trackId);
        }

        if (back.hasFullBodyGain)
        {
            animalFullBodyGainByTrack[trackId] = back.fullBodyGain;
        }
        else
        {
            animalFullBodyGainByTrack.Remove(trackId);
        }

        if (logPlacementMeasurement)
        {
            Debug.Log($"[ModelPreset] track={trackId} {prefab.name} は前に測った倍率 x{back.factor:F3} に戻す（ref {back.referenceFrame}）");
        }
    }

    private string DescribeActiveModelPresets()
    {
        var parts = new List<string>(activeModelPresets.Count);
        foreach (ExperimentModelPreset entry in activeModelPresets)
        {
            parts.Add($"{entry.trackId}={entry.NameFor(currentModelPreset)}");
        }

        return "tracks=[" + string.Join(",", parts) + "]";
    }
}
