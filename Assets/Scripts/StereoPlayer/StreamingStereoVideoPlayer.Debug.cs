using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private Camera[] GetActiveCameras()
    {
#if UNITY_2023_1_OR_NEWER
        return FindObjectsByType<Camera>(FindObjectsSortMode.None);
#else
        return FindObjectsOfType<Camera>();
#endif
    }


    private Camera GetViewCamera()
    {
        if (ViewCameraSelection.IsUsable(cachedViewCamera))
        {
            return cachedViewCamera;
        }

        cachedViewCamera = ViewCameraSelection.Select(GetActiveCameras());
        return cachedViewCamera;
    }


    private Transform GetHeadTransform()
    {
        if (headTransform != null)
        {
            return headTransform;
        }

        if (Camera.main != null)
        {
            return Camera.main.transform;
        }

        return transform;
    }


    private Transform GetViewOrHeadTransform()
    {
        Camera viewCam = GetViewCamera();
        return viewCam != null ? viewCam.transform : GetHeadTransform();
    }


    // ---- 計測: 配置したモデルを再投影して meta.bin の bbox と比較する ----
    // sizeRatio   = 投影高さ ÷ bbox 高さ。1.0 なら映像どおりの大きさで置けている
    // topDelta    = 投影上端 - bbox 上端。正なら映像より下にずれている
    // bottomDelta = 投影下端 - bbox 下端。正なら映像より下にずれている
    // renderer.bounds を使うので updateWhenOffscreen=true により実際の姿勢が反映される。
    private void LogPlacementMeasurementIfEnabled(
        MetaObj obj,
        GameObject instance,
        Transform screen,
        int frame)
    {
        if (!logPlacementMeasurement ||
            instance == null ||
            obj.bboxH <= 0 ||
            frame % Mathf.Max(1, logPlacementMeasurementEveryNFrames) != 0)
        {
            return;
        }

        LogBoneLengthsOnce(instance);

        if (!TryProjectRendererBoundsToEyeHeight(
                instance,
                screen,
                out float topV,
                out float bottomV,
                out float heightPixels,
                out float depthMeters))
        {
            return;
        }

        float bboxTop = obj.bboxY;
        float bboxBottom = obj.bboxY + obj.bboxH;
        string category = IsCategoryPerson(obj.categoryId)
            ? "Person"
            : (IsCategoryAnimal(obj.categoryId) ? "Animal" : "Other");
        Vector3 localScale = instance.transform.localScale;

        // AABB は world 軸平行なので姿勢が傾くと過大に出る。ボーン位置ベースでも測って比較する。
        string boneInfo = string.Empty;
        if (TryProjectBonesToEyeHeight(
                instance,
                screen,
                out float boneTopV,
                out float boneBottomV,
                out float boneHeightPixels,
                out string topBoneName,
                out string bottomBoneName))
        {
            boneInfo =
                $" boneRatio={boneHeightPixels / obj.bboxH:F3} " +
                $"boneTopDelta={boneTopV - bboxTop:F1} " +
                $"boneBottomDelta={boneBottomV - bboxBottom:F1} " +
                $"topBone={topBoneName} bottomBone={bottomBoneName}";
        }

        Debug.Log(
            $"[PLACE] f={frame} track={obj.trackId} {category} " +
            $"sizeRatio={heightPixels / obj.bboxH:F3} " +
            $"topDelta={topV - bboxTop:F1} bottomDelta={bottomV - bboxBottom:F1} " +
            $"proj[top={topV:F1} bot={bottomV:F1} h={heightPixels:F1}] " +
            $"bbox[top={bboxTop:F0} bot={bboxBottom:F0} h={obj.bboxH}] " +
            $"anchorV={obj.anchorV} depth={depthMeters:F3} scale={localScale.x:F4}" +
            boneInfo);
    }


    private bool loggedBoneLengths;

    // 表示モデルの骨長を 1 回だけ出す。meta.bin の keypoints3d から測った骨長と比べて
    // 体型差（脚と胴の比率）がどれだけあるかを確認するための計測。
    private void LogBoneLengthsOnce(GameObject instance)
    {
        if (loggedBoneLengths || !logPlacementMeasurement || instance == null)
        {
            return;
        }

        Animator animator = instance.GetComponentInChildren<Animator>(true);
        if (animator == null || !animator.isHuman)
        {
            return;
        }

        HumanoidRigCache cache = GetOrBuildHumanoidCache(animator);
        if (cache == null || !cache.ready)
        {
            return;
        }

        float Len(HumanBodyBones a, HumanBodyBones b)
        {
            if (!cache.bones.TryGetValue(a, out Transform ta) || ta == null ||
                !cache.bones.TryGetValue(b, out Transform tb) || tb == null)
            {
                return 0f;
            }

            return Vector3.Distance(ta.position, tb.position);
        }

        float thigh = Len(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg);
        float shin = Len(HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot);
        float torso = Len(HumanBodyBones.Hips, HumanBodyBones.Neck);
        float upperArm = Len(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm);
        float foreArm = Len(HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand);
        float headTop = Len(HumanBodyBones.Neck, HumanBodyBones.Head);
        if (torso <= 0.0001f)
        {
            return;
        }

        loggedBoneLengths = true;
        Debug.Log(
            $"[BONELEN] thigh={thigh:F4} shin={shin:F4} torso={torso:F4} " +
            $"upperArm={upperArm:F4} foreArm={foreArm:F4} neckToHead={headTop:F4} " +
            $"| 胴で正規化: thigh={thigh / torso:F3} shin={shin / torso:F3} " +
            $"leg={(thigh + shin) / torso:F3} upperArm={upperArm / torso:F3} " +
            $"foreArm={foreArm / torso:F3} " +
            $"| scale={instance.transform.localScale.x:F4}");
    }


    // Humanoid のボーン world 位置を eye pixel に投影して縦の広がりを測る。
    // renderer.bounds（world 軸平行 AABB）と違い、姿勢が傾いても過大評価しない。
    private bool TryProjectBonesToEyeHeight(
        GameObject instance,
        Transform screen,
        out float topV,
        out float bottomV,
        out float heightPixels,
        out string topBoneName,
        out string bottomBoneName)
    {
        topV = 0f;
        bottomV = 0f;
        heightPixels = 0f;
        topBoneName = null;
        bottomBoneName = null;
        if (instance == null || manifest == null || manifest.eye_h <= 0)
        {
            return false;
        }

        Animator animator = instance.GetComponentInChildren<Animator>(true);
        if (animator == null || !animator.isHuman)
        {
            return false;
        }

        HumanoidRigCache cache = GetOrBuildHumanoidCache(animator);
        if (cache == null || !cache.ready)
        {
            return false;
        }

        if (!TryGetProjectionIntrinsics(out float fx, out float fy, out _, out _) ||
            !TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
        {
            return false;
        }

        Quaternion worldToCam = Quaternion.Inverse(camRotation);
        float minV = float.MaxValue;
        float maxV = float.MinValue;
        bool hasAny = false;
        foreach (var pair in cache.bones)
        {
            Transform bone = pair.Value;
            if (bone == null)
            {
                continue;
            }

            Vector3 cam = worldToCam * (bone.position - camOrigin);
            if (!PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                    manifest,
                    cam,
                    fx,
                    fy,
                    out Vector2 pixel))
            {
                continue;
            }

            if (pixel.y < minV)
            {
                minV = pixel.y;
                topBoneName = pair.Key.ToString();
            }

            if (pixel.y > maxV)
            {
                maxV = pixel.y;
                bottomBoneName = pair.Key.ToString();
            }

            hasAny = true;
        }

        if (!hasAny)
        {
            return false;
        }

        topV = minV;
        bottomV = maxV;
        heightPixels = maxV - minV;
        return heightPixels > 0.0001f;
    }
}
