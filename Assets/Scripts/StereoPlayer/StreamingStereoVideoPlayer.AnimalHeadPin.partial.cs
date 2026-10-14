using System.Collections.Generic;
using UnityEngine;

// animalBottomClippedHeadPin（既定 OFF、2026-10-14。Core.cs の宣言と Docs/tmp/headpin_20261014/README.md）。
// 下端が切れて上端が切れていない tick の ⑦ で、頭の骨（AnimalRigCache.head、SMAL J16 を当てる骨）を、meta.bin の頭の関節（kp18）の像へ
// カメラの面内の平行移動で置く。判定は今の ⑦ の上端の分岐そのもの（tick ごと・ヒステリシス込み）。z・倍率・⑧ の目標は変えない。
// 目標の解決（TryResolveAnimalHeadPin）は状態を書かない。⑦ と、⑦ を写す U1（TryBuildExactScaleRefineSetup）・U2（TryBuildExactDepthSecondFit）が
// 同じ tick に同じ値を得る。控え（データが欠けた tick に使う値）を書くのは ⑦（TryApplyAnimalHeadPin）だけ。
public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // AniMer の焦点距離 ÷ 画の一辺。D-017 Q1 の回答（2026-10-13）: AniMer の焦点距離は 256 px の入力 patch 基準で 1000、全体画像のカメラは
    // f_full = 1000 / 256 × 1280 = 5000 px、主点は画像の中心（4D-Humans / AniMer の cam_crop_to_full の流儀。HMR2 の 25000 = 5000/256 × 1280 も同じ形）。
    // 全体画像の f = この値 × max(幅, 高さ)。今の bundle（長辺 1280）では 5000。
    private const float AniMerFocalPerImagePixel = 1000f / 256f;

    // 使えた最後の「頭の関節の像 − anchor」（eye px、データの項だけ。平滑の遅れは含めない）。データが欠けた tick に使う。
    // 書くのは TryApplyAnimalHeadPin だけ。shot の切れ目（偽のカットを含む）で消す（ResetPerShotTrackState、bboxBottomClippedByTrack と同じ寿命）。
    private readonly Dictionary<uint, Vector2> animalHeadPinHeldOffsetByTrack = new Dictionary<uint, Vector2>();
    // 記録用（[HEADPIN-BRANCH]）: 直前の ⑦ で頭合わせをしたか。同じ寿命。
    private readonly Dictionary<uint, bool> animalHeadPinActiveByTrack = new Dictionary<uint, bool>();
    // 記録用: この tick の目標（[HEADPIN-END]）と、⑦ の何回目か（[HEADPIN] の call）。[HEADPIN-MISS] を tick に 1 回にするための tick。
    private readonly Dictionary<uint, Vector3> animalHeadPinLastTargetByTrack = new Dictionary<uint, Vector3>(); // x,y = 目標 px、z = Time.frameCount
    private readonly Dictionary<uint, int> animalHeadPinCallCountByTrack = new Dictionary<uint, int>();
    private readonly Dictionary<uint, int> animalHeadPinMissTickByTrack = new Dictionary<uint, int>();
    // ApplyMetaTarget が受け取ったフレーム（obj を読んだ表示用のメタのフレーム）。
    private int animalHeadPinFrame = -1;

    private struct AnimalHeadPinTarget
    {
        public Vector2 pixel;       // 頭の骨を置く eye px（データ + 横の平滑の遅れ）
        public Vector2 rawPixel;    // 平滑の遅れを足さない値（記録用）
        public Vector2 anchor;      // anchor（eye px、丸めない）
        public Vector2 dataOffset;  // rawPixel − anchor
        public float lagU;          // 横の平滑の遅れ（eye px）
        public float tz;            // SMAL transl z（控えを使ったときは −1）
        public bool held;           // 控えを使った
        public string missReason;   // held のとき、このフレームのデータが使えなかった理由
        public bool inBBox;         // rawPixel が bbox の中か（記録用。実行時の条件にはしない）
    }

    // 元の画（推定器が走った画）と片目の画が同じ（一様な拡大だけ）と manifest が言っているか。D-017 Q1-3 の回答: work 動画（1280×720）は元動画の
    // 等比縮小で、切り抜き・letterbox は無い。crop_fraction を持たない bundle（bundle.svb・bundle_tutorial）は通さない。
    private bool IsAnimalHeadPinCanvasEye()
    {
        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0 ||
            manifest.left_eye_origin != "full" || manifest.crop_fraction == null)
        {
            return false;
        }

        ManifestCropFractionData c = manifest.crop_fraction;
        return Mathf.Abs(c.crop_w_fraction - 1f) < 1e-4f && Mathf.Abs(c.crop_h_fraction - 1f) < 1e-4f &&
               Mathf.Abs(c.eye_w_fraction - 1f) < 1e-4f && Mathf.Abs(c.eye_h_fraction - 1f) < 1e-4f;
    }

    // 頭の関節（kp18）の像 − anchor（eye px）。使えたら null、使えなければ理由。
    //   根の像 = anchor（奥行き tz）。anchor は AniMer の keypoints2d[7]・[18] の 2D の中点（2120 フレームで 0.000 px）で、根（3D の中点）の像とは
    //   p50 12.7 / p95 25.9 px 違う。根の奥行きは transl z（モデルの原点。根との差は猫で中央 0.42〜0.49 単位、D-017 Q4）で代用。
    //   関節は根からの相対（x 右・y 上・z 前、AniMer の単位、生成側の EMA w = 0.8 済み、D-017 Q3）。
    private string TryResolveAnimalHeadKeypointOffset(MetaObj obj, Vector2 anchor, out Vector2 offset, out float tz)
    {
        offset = default;
        tz = 0f;
        const int k = AnimalHeadKeypoints.Head;
        if (!obj.hasSkeleton || obj.jointsCam == null || obj.jointsVis == null || obj.skeletonKpCount <= k ||
            obj.jointsCam.Length <= k || obj.jointsVis.Length <= k)
        {
            return "noSkeleton";
        }

        if (obj.jointsVis[k] == 0)
        {
            return "headInvisible";
        }

        Vector3 j = ApplyPoseAxisSign(obj.jointsCam[k], AnimalPoseAxisSign); // TryBuildAnimalPoseWorld と同じ
        // DecodeJointCamFromBundle は NaN・Inf を 0 にする（Manifest.partial.cs）。根は kp7 と kp18 の中点なので、kp18 = 0 は欠けたときだけ。
        if (j == Vector3.zero)
        {
            return "headZero";
        }

        if (!TryGetAnimalSmalPoseLoaded(animalHeadPinFrame, obj.trackId, out AnimalSmalPose smal) || !smal.hasTransl || smal.transl.z <= 0.0001f)
        {
            return "noTransl";
        }

        tz = smal.transl.z;
        float d = tz + j.z;
        if (d <= tz * 0.2f) // TryResolveAnimalFullBodyHeightPixels と同じガード
        {
            return "depthGuard";
        }

        float f = AniMerFocalPerImagePixel * Mathf.Max(manifest.eye_w, manifest.eye_h);
        float cx = manifest.eye_w * 0.5f;
        float cy = manifest.eye_h * 0.5f;
        float xr = (anchor.x - cx) * tz / f;
        float yr = -(anchor.y - cy) * tz / f;
        offset = new Vector2(cx + f * (xr + j.x) / d, cy - f * (yr + j.y) / d) - anchor;
        return null;
    }

    // 動物の root の位置の平滑（AnimalPoseApplier の One-Euro、AlignAnimalRootToSkeleton）が今 root を生の目標からずらしている量の、anchor の像での
    // 横の成分（eye px）。今の ⑦ の上端合わせは縦を毎 tick bbox の上端へ置き直すので、今の配置で平滑が効いているのは横だけ。頭合わせも横だけ同じだけ残す。
    // 新しい平滑ではない（同じフィルタの今の状態を読むだけ）。平滑がまだ無ければ 0。
    private float ResolveAnimalRootSmoothingLagU(GameObject instance, MetaObj obj, int uEye, float fx, float fy, Quaternion camRotation)
    {
        if (!animalPoseApplier.TryGetRootPositionSmoothingLag(instance.transform, out Vector3 lagWorld) || lagWorld == Vector3.zero)
        {
            return 0f;
        }

        float uEyeF = Mathf.Clamp(uEye, 0f, manifest.eye_w - 1f);      // ApplyMetaTarget の anchorWorld と同じ anchor の点
        float vEyeF = Mathf.Clamp(obj.anchorV, 0f, manifest.eye_h - 1f);
        Vector3 anchorCam = PinholePlacementSpace.ReconstructCamLocalFromEyePixel(manifest, uEyeF, vEyeF, obj.anchorZ, fx, fy, manifest.eye_w, manifest.eye_h);
        Vector3 lagCam = Quaternion.Inverse(camRotation) * lagWorld;
        if (!PinholePlacementSpace.TryProjectCamLocalToEyePixel(manifest, anchorCam, fx, fy, out Vector2 anchorPx) ||
            !PinholePlacementSpace.TryProjectCamLocalToEyePixel(manifest, anchorCam + lagCam, fx, fy, out Vector2 lagged))
        {
            return 0f;
        }

        return lagged.x - anchorPx.x;
    }

    // 頭の骨（カメラ空間）と目標。状態を書かない。false: 頭の骨が無い・目の後ろ・目標が作れず控えも無い・manifest の判定が通らない（呼び出し側は今の上端合わせ）。
    private bool TryResolveAnimalHeadPin(GameObject instance, MetaObj obj, Vector3 camOrigin, Quaternion camRotation,
        out Vector3 headCam, out AnimalHeadPinTarget target, out string failReason)
    {
        headCam = default;
        target = default;
        failReason = null;
        if (!IsAnimalHeadPinCanvasEye()) { failReason = "canvas"; return false; }
        if (!TryGetProjectionIntrinsics(out float fx, out float fy, out _, out _)) { failReason = "intrinsics"; return false; }
        AnimalRigCache cache = animalPoseApplier.PeekAnimalRigCache(instance);
        if (cache == null || !cache.ready || cache.head == null) { failReason = "noHead"; return false; }
        headCam = Quaternion.Inverse(camRotation) * (cache.head.position - camOrigin);
        if (headCam.z <= 0.001f) { failReason = "headBehindEye"; return false; }
        if (!ResolveAnchorToScreen(obj.anchorU, out _, out int uEye, out bool isRightEye)) { failReason = "anchor"; return false; }

        Vector2 anchor = new Vector2(uEye, obj.anchorV);
        string dataMiss = TryResolveAnimalHeadKeypointOffset(obj, anchor, out Vector2 offset, out float tz);
        if (dataMiss != null)
        {
            if (!animalHeadPinHeldOffsetByTrack.TryGetValue(obj.trackId, out offset)) { failReason = dataMiss; return false; }
            target.held = true;
            target.missReason = dataMiss;
            tz = -1f;
        }

        target.anchor = anchor;
        target.dataOffset = offset;
        target.tz = tz;
        target.lagU = ResolveAnimalRootSmoothingLagU(instance, obj, uEye, fx, fy, camRotation);
        target.rawPixel = anchor + offset;
        target.pixel = new Vector2(target.rawPixel.x + target.lagU, target.rawPixel.y);
        float bx = obj.bboxX - (isRightEye ? manifest.eye_w : 0); // 右目の物体の bbox は eye_w ぶん右
        target.inBBox = target.rawPixel.x >= bx && target.rawPixel.x <= bx + obj.bboxW &&
                        target.rawPixel.y >= obj.bboxY && target.rawPixel.y <= obj.bboxY + obj.bboxH;
        return true;
    }

    // ⑦ の上端合わせの代わり（FitDisplayedModelToBBox の上端の分岐の中、フラグ ON の animal だけ）。置けたら true。
    private bool TryApplyAnimalHeadPin(GameObject instance, MetaObj obj, Transform screen, float aabbTopV)
    {
        if (!TryGetProjectionIntrinsics(out float fx, out float fy, out _, out _) ||
            !TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
        {
            NoteAnimalHeadPinMiss(obj, "basis", false);
            return false;
        }

        if (!TryResolveAnimalHeadPin(instance, obj, camOrigin, camRotation, out Vector3 headCam, out AnimalHeadPinTarget t, out string fail) ||
            !PinholePlacementSpace.TryProjectCamLocalToEyePixel(manifest, headCam, fx, fy, out Vector2 headPx))
        {
            NoteAnimalHeadPinMiss(obj, fail ?? "headProject", false);
            return false;
        }

        Vector3 shift = new Vector3(
            PinholePlacementSpace.CameraShiftXToPutPointOnColumn(manifest.eye_w, fx, headCam.z, headPx.x, t.pixel.x),
            PinholePlacementSpace.CameraShiftYToPutPointOnRow(manifest.eye_h, fy, headCam.z, headPx.y, t.pixel.y),
            0f);
        TrackPlacementWriter.ApplyCameraSpaceOffset(instance.transform, camRotation, shift);

        if (t.held)
        {
            NoteAnimalHeadPinMiss(obj, t.missReason, true);
        }
        else
        {
            animalHeadPinHeldOffsetByTrack[obj.trackId] = t.dataOffset;
        }

        NoteAnimalHeadPinBranch(obj, true, t.held ? "held" : "live");
        int tick = Time.frameCount;
        bool sameTick = animalHeadPinLastTargetByTrack.TryGetValue(obj.trackId, out Vector3 last) && (int)last.z == tick;
        int call = sameTick && animalHeadPinCallCountByTrack.TryGetValue(obj.trackId, out int c) ? c + 1 : 1;
        animalHeadPinCallCountByTrack[obj.trackId] = call;
        animalHeadPinLastTargetByTrack[obj.trackId] = new Vector3(t.pixel.x, t.pixel.y, tick);
        if (logPlacementMeasurement)
        {
            LogAnimalHeadPin(instance, obj, camOrigin, camRotation, fx, fy, t, headPx, shift, headCam.z, aabbTopV, call);
        }

        return true;
    }

    // [HEADPIN-MISS]: track ごとに tick に 1 回（⑦ が 2 回呼ばれても 1 行）。held = 控えを使った / false = 今の上端合わせに戻した。
    private void NoteAnimalHeadPinMiss(MetaObj obj, string reason, bool held)
    {
        if (!held)
        {
            NoteAnimalHeadPinBranch(obj, false, reason);
        }

        int tick = Time.frameCount;
        if (animalHeadPinMissTickByTrack.TryGetValue(obj.trackId, out int lastTick) && lastTick == tick)
        {
            return;
        }

        animalHeadPinMissTickByTrack[obj.trackId] = tick;
        Debug.Log($"[HEADPIN-MISS] f={animalHeadPinFrame} track={obj.trackId} tick={tick} reason={reason} action={(held ? "held" : "fallbackTop")}");
    }

    // [HEADPIN-BRANCH]: shot の中で頭合わせの入り・出が変わったときだけ（shot の最初の入りは none->pin）。
    private void NoteAnimalHeadPinBranch(MetaObj obj, bool active, string reason)
    {
        bool had = animalHeadPinActiveByTrack.TryGetValue(obj.trackId, out bool previous);
        animalHeadPinActiveByTrack[obj.trackId] = active;
        if ((!had && active) || (had && previous != active))
        {
            Debug.Log($"[HEADPIN-BRANCH] f={animalHeadPinFrame} track={obj.trackId} tick={Time.frameCount} " +
                      $"{(had ? (previous ? "pin" : "top") : "none")}->{(active ? "pin" : "top")} reason={reason}");
        }
    }

    private void LogAnimalHeadPin(GameObject instance, MetaObj obj, Vector3 camOrigin, Quaternion camRotation, float fx, float fy,
        AnimalHeadPinTarget t, Vector2 headPx, Vector3 shift, float headZ, float aabbTopV, int call)
    {
        AnimalRigCache cache = animalPoseApplier.PeekAnimalRigCache(instance);
        float residual = -1f;
        if (cache != null && cache.head != null &&
            PinholePlacementSpace.TryProjectCamLocalToEyePixel(manifest, Quaternion.Inverse(camRotation) * (cache.head.position - camOrigin), fx, fy, out Vector2 after))
        {
            residual = (after - t.pixel).magnitude;
        }

        float f = AniMerFocalPerImagePixel * Mathf.Max(manifest.eye_w, manifest.eye_h);
        Debug.Log($"[HEADPIN] f={animalHeadPinFrame} track={obj.trackId} tick={Time.frameCount} call={call} src={(t.held ? "held" : "live")} " +
                  $"target=({t.pixel.x:F1},{t.pixel.y:F1}) raw=({t.rawPixel.x:F1},{t.rawPixel.y:F1}) anchor=({t.anchor.x:F0},{t.anchor.y:F0}) " +
                  $"lagU={t.lagU:F2} offset=({t.dataOffset.x:F1},{t.dataOffset.y:F1}) head=({headPx.x:F1},{headPx.y:F1}) " +
                  $"shiftPx=({t.pixel.x - headPx.x:F1},{t.pixel.y - headPx.y:F1}) shiftMm=({shift.x * 1000f:F1},{shift.y * 1000f:F1}) " +
                  $"headZ={headZ:F4} tz={t.tz:F3} F={f:F0} top7DeltaV={obj.bboxY - aabbTopV:F1} inBBox={(t.inBBox ? 1 : 0)} " +
                  $"residual={residual:F3} model={instance.name}");
    }

    // tick の最後（⑧・2 回目の ⑦・引き継ぎの混ぜの後）の頭と目標の差。頭合わせをした tick だけ。
    private void LogAnimalHeadPinEndOfTickIfEnabled(MetaObj obj, GameObject instance, Transform screen)
    {
        if (!animalBottomClippedHeadPin || !logPlacementMeasurement || instance == null || !IsCategoryAnimal(obj.categoryId) ||
            !animalHeadPinLastTargetByTrack.TryGetValue(obj.trackId, out Vector3 last) || (int)last.z != Time.frameCount ||
            !TryGetProjectionIntrinsics(out float fx, out float fy, out _, out _) || !TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
        {
            return;
        }

        AnimalRigCache cache = animalPoseApplier.PeekAnimalRigCache(instance);
        if (cache == null || cache.head == null ||
            !PinholePlacementSpace.TryProjectCamLocalToEyePixel(manifest, Quaternion.Inverse(camRotation) * (cache.head.position - camOrigin), fx, fy, out Vector2 head))
        {
            return;
        }

        Debug.Log($"[HEADPIN-END] f={animalHeadPinFrame} track={obj.trackId} tick={Time.frameCount} head=({head.x:F1},{head.y:F1}) " +
                  $"target=({last.x:F1},{last.y:F1}) residual={(head - new Vector2(last.x, last.y)).magnitude:F3}");
    }
}
