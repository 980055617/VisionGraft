using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private const int TestModelLockFrames = 30;
    private int lastAutoTrackId = int.MinValue;
    private int lastMetaLogFrame = -1;
    private readonly Dictionary<uint, GameObject> trackInstances = new Dictionary<uint, GameObject>();
    private uint activeTrackId = uint.MaxValue;
    private GameObject activeTrackInstance;
    private void PlaceOrMoveTestModel(PickResult pick)
    {
        TrySpawnOrMoveTestModel(pick);
    }

    public void FollowTick()
    {
        if (useMetaFollow && metaLoaded)
        {
            FollowTickMeta();
            return;
        }

        if (!enableFollow || !hasPickedPixel || spawnedTestModel == null)
        {
            return;
        }

        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return;
        }

        Transform screen = leftScreen;
        if (screen == null)
        {
            return;
        }

        float t = vp != null ? (float)vp.time : Time.time;
        int du = Mathf.RoundToInt(Mathf.Sin(t * followSpeed) * followAmplitudePixels);
        int dv = Mathf.RoundToInt(Mathf.Cos(t * followSpeed) * followAmplitudePixels);
        int u2 = Mathf.Clamp(pickedPixel.x + du, 0, manifest.eye_w - 1);
        int v2 = Mathf.Clamp(pickedPixel.y + dv, 0, manifest.eye_h - 1);

        Vector3 worldOnPlane = EyePixelToWorldOnScreen(u2, v2, screen, manifest.eye_w, manifest.eye_h, 0f);
        Vector3 world = worldOnPlane + screen.forward * markerOffset;
        Quaternion rotation = screen.rotation;

        spawnedTestModel.transform.SetPositionAndRotation(world, rotation);
        spawnedTestModel.transform.localScale = Vector3.one * testModelSizeMeters;
        AttachTransformLock(spawnedTestModel, world, rotation);

        LogFollow($"FollowTick: base=({pickedPixel.x},{pickedPixel.y}) offset=({du},{dv}) pixel=({u2},{v2}) world={world}");
    }

    private void FollowTickMeta()
    {
        if (replacePrefab == null && spawnedTestModel == null)
        {
            return;
        }

        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return;
        }

        int frame = GetCurrentFrameIndex();
        if (!TryReadFrameObjects(frame, metaFrameObjects) || metaFrameObjects.Count == 0)
        {
            return;
        }

        if (verboseLog && frame != lastMetaLogFrame)
        {
            lastMetaLogFrame = frame;
            LogFollow($"MetaFollowMode: followTrackId={followTrackId} auto={(followTrackId < 0)}");
            LogMeta($"MetaFrameSummary: frame={frame} objCount={metaFrameObjects.Count}");
            for (int i = 0; i < metaFrameObjects.Count; i++)
            {
                MetaObj obj = metaFrameObjects[i];
                LogMeta($"MetaFrameObj[{i}]: track={obj.trackId} anchor=({obj.anchorU},{obj.anchorV}) z={obj.anchorZ:F3} bbox=({obj.bboxW},{obj.bboxH}) cat={obj.categoryId}");
            }
        }

        MetaObj target = metaFrameObjects[0];
        if (followTrackId >= 0)
        {
            bool found = false;
            for (int i = 0; i < metaFrameObjects.Count; i++)
            {
                if (metaFrameObjects[i].trackId == (uint)followTrackId)
                {
                    target = metaFrameObjects[i];
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return;
            }
        }
        else
        {
            target = SelectAutoFollowTarget(metaFrameObjects);
            followTrackId = (int)target.trackId;
            hasPickedPixel = true;
            pickedPixel = new Vector2Int(target.anchorU, target.anchorV);

            if (followTrackId != lastAutoTrackId)
            {
                LogMeta($"Meta auto-follow track: trackId={followTrackId} frame={frame}");
                lastAutoTrackId = followTrackId;
            }
        }

        if (!ResolveAnchorToScreen(target.anchorU, out Transform screen, out int uEye, out bool isRightEye))
        {
            return;
        }
        pickedScreen = screen;

        int vEye = target.anchorV;
        if (verboseLog)
        {
            float uFullNorm = metaHeader.width > 0 ? (float)target.anchorU / metaHeader.width : -1f;
            float uEyeNorm = manifest.eye_w > 0 ? (float)uEye / manifest.eye_w : -1f;
            float vNorm = manifest.eye_h > 0 ? (float)vEye / manifest.eye_h : -1f;
            LogMeta(
                $"MetaFollowMap: anchorU={target.anchorU} anchorV={target.anchorV} metaW={metaHeader.width} eyeW={manifest.eye_w} " +
                $"uFullNorm={uFullNorm:F3} uEye={uEye} uEyeNorm={uEyeNorm:F3} vNorm={vNorm:F3} " +
                $"screen={(screen != null ? screen.name : "null")} rightEye={isRightEye} scale={screen.localScale}");
        }
        if (replacePrefab != null)
        {
            GameObject instance = GetOrCreateTrackInstance(target.trackId);
            if (instance == null)
            {
                return;
            }

            Vector3 worldPinhole = AnchorUvZToWorld(screen, uEye, vEye, target.anchorZ);
            Quaternion rotationPinhole = screen.rotation;
            float targetHeight = ComputeTargetHeightMeters(target.bboxH, target.anchorZ);
            ApplyReplaceableModelTransform(instance, worldPinhole, rotationPinhole, targetHeight, target, uEye, vEye);

            LogFollow($"MetaFollow: frame={frame} track={target.trackId} uv=({target.anchorU},{target.anchorV}) uEye={uEye} depth={target.anchorZ:F3} screen={(screen != null ? screen.name : "null")} world={worldPinhole}");
            return;
        }

        Vector3 worldOnPlane = EyePixelToWorldOnScreen(uEye, vEye, screen, manifest.eye_w, manifest.eye_h, 0f);
        Transform head = GetViewCamera() != null ? GetViewCamera().transform : GetHeadTransform();
        Vector3 frontDir = (head != null)
            ? (head.position - screen.position).normalized
            : GetScreenFrontDirection(screen);
        if (frontDir == Vector3.zero)
        {
            frontDir = GetScreenFrontDirection(screen);
        }

        Vector3 world = worldOnPlane + screen.forward * markerOffset + frontDir * target.anchorZ;
        Quaternion rotation = screen.rotation;

        spawnedTestModel.transform.SetPositionAndRotation(world, rotation);
        spawnedTestModel.transform.localScale = Vector3.one * testModelSizeMeters;
        AttachTransformLock(spawnedTestModel, world, rotation);

        LogFollow($"MetaFollow: frame={frame} track={target.trackId} uv=({target.anchorU},{target.anchorV}) uEye={uEye} depth={target.anchorZ:F3} screen={(screen != null ? screen.name : "null")} world={world}");
    }

    private MetaObj SelectAutoFollowTarget(List<MetaObj> objs)
    {
        float eyeW = manifest != null ? manifest.eye_w : 0f;
        float eyeH = manifest != null ? manifest.eye_h : 0f;
        float leftCenterU = eyeW * 0.5f;
        float rightCenterU = eyeW * 1.5f;
        float centerV = eyeH * 0.5f;
        bool hasRightCenter = metaHeader.width >= eyeW * 2f && rightScreen != null;

        MetaObj best = objs[0];
        float bestDistSq = float.MaxValue;
        for (int i = 0; i < objs.Count; i++)
        {
            MetaObj obj = objs[i];
            float dx = obj.anchorU - leftCenterU;
            float dy = obj.anchorV - centerV;
            float distSq = dx * dx + dy * dy;
            if (hasRightCenter)
            {
                float dxR = obj.anchorU - rightCenterU;
                float distSqR = dxR * dxR + dy * dy;
                distSq = Mathf.Min(distSq, distSqR);
            }

            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = obj;
            }
        }

        if (verboseLog)
        {
            LogMeta($"Meta auto-select: track={best.trackId} anchor=({best.anchorU},{best.anchorV}) distSq={bestDistSq:F1} leftCenter={leftCenterU:F1} rightCenter={rightCenterU:F1}");
        }

        return best;
    }

    private GameObject GetOrCreateTrackInstance(uint trackId)
    {
        if (trackInstances.TryGetValue(trackId, out GameObject existing) && existing != null)
        {
            SetActiveTrackInstance(trackId, existing);
            return existing;
        }

        if (replacePrefab == null)
        {
            return null;
        }

        GameObject instance = Instantiate(replacePrefab, Vector3.zero, Quaternion.identity);
        instance.name = $"Track_{trackId}";
        if (instance.GetComponent<ReplaceableModel>() == null)
        {
            instance.AddComponent<ReplaceableModel>();
        }

        trackInstances[trackId] = instance;
        SetActiveTrackInstance(trackId, instance);
        return instance;
    }

    private void SetActiveTrackInstance(uint trackId, GameObject instance)
    {
        if (activeTrackId == trackId && activeTrackInstance == instance)
        {
            return;
        }

        foreach (var kvp in trackInstances)
        {
            if (kvp.Value == null)
            {
                continue;
            }

            kvp.Value.SetActive(kvp.Key == trackId);
        }

        activeTrackId = trackId;
        activeTrackInstance = instance;
    }

    private float ComputeTargetHeightMeters(ushort bboxH, float zMeters)
    {
        if (manifest == null || manifest.eye_h <= 0 || bboxH == 0)
        {
            return 0f;
        }

        if (!TryGetFocalLengths(out _, out float fy))
        {
            return 0f;
        }

        return (bboxH / (float)manifest.eye_h) * (2f * zMeters / fy);
    }

    private void ApplyReplaceableModelTransform(GameObject instance, Vector3 world, Quaternion rotation, float targetHeightMeters, MetaObj obj, int uEye, int vEye)
    {
        if (instance == null)
        {
            return;
        }

        ReplaceableModel model = instance.GetComponent<ReplaceableModel>();
        float modelHeight = model != null ? model.GetModelHeightMeters() : 0f;
        float userScale = model != null ? model.userScale : 1f;
        float scale = modelHeight > 0f && targetHeightMeters > 0f
            ? (targetHeightMeters / modelHeight) * userScale
            : userScale;

        instance.transform.SetPositionAndRotation(world, rotation);
        instance.transform.localScale = Vector3.one * scale;

        if (model != null && model.anchor != null)
        {
            Vector3 anchorWorld = model.anchor.position;
            Vector3 rootWorld = instance.transform.position;
            Vector3 delta = anchorWorld - rootWorld;
            instance.transform.position = world - delta;
        }

        if (verboseLog)
        {
            if (TryGetFocalLengths(out float fx, out float fy))
            {
                float xNdc = (uEye / (float)manifest.eye_w - 0.5f) * 2f;
                float yNdc = (0.5f - vEye / (float)manifest.eye_h) * 2f;
                float x = xNdc * obj.anchorZ / fx;
                float y = yNdc * obj.anchorZ / fy;
                LogModel($"MetaPinhole: u={uEye} v={vEye} z={obj.anchorZ:F3} X={x:F3} Y={y:F3} Z={obj.anchorZ:F3} targetH={targetHeightMeters:F3} modelH={modelHeight:F3} scale={scale:F3}");
            }

            Debug.DrawLine(world, world + rotation * Vector3.forward * 0.2f, Color.cyan, 0.05f);
        }
    }

    private bool ResolveAnchorToScreen(ushort anchorU, out Transform screen, out int uEye, out bool isRightEye)
    {
        screen = pickedScreen != null ? pickedScreen : leftScreen;
        uEye = anchorU;
        isRightEye = false;

        if (manifest == null || manifest.eye_w <= 0)
        {
            return false;
        }

        if (metaHeader.width >= manifest.eye_w * 2 && rightScreen != null)
        {
            if (anchorU >= manifest.eye_w)
            {
                screen = rightScreen;
                uEye = anchorU - manifest.eye_w;
                isRightEye = true;
            }
            else
            {
                screen = leftScreen;
                uEye = anchorU;
            }
        }

        if (screen == null)
        {
            return false;
        }

        uEye = Mathf.Clamp(uEye, 0, manifest.eye_w - 1);
        return true;
    }

    private void TrySpawnTestModel()
    {
        if (replacePrefab != null)
        {
            return;
        }

        if (leftScreen == null)
        {
            Debug.LogWarning("Test model skipped: leftScreen is null.");
            return;
        }

        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            Debug.LogWarning("Test model skipped: manifest eye_w/eye_h invalid or not loaded.");
            return;
        }

        Vector2Int finalPixel = testPixel;
        if (finalPixel.x < 0 || finalPixel.y < 0)
        {
            finalPixel = new Vector2Int(manifest.eye_w / 2, manifest.eye_h / 2);
        }

        TrySpawnOrMoveTestModelAtPixel(leftScreen, finalPixel.x, finalPixel.y);
    }

    private void TrySpawnOrMoveTestModel(PickResult pick)
    {
        if (pick.screen == null)
        {
            Debug.LogWarning("TrySpawnOrMoveTestModel: screen is null.");
            return;
        }

        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            Debug.LogWarning("TrySpawnOrMoveTestModel: manifest not ready.");
            return;
        }

        if (!pick.hasHitDistance)
        {
            TrySpawnOrMoveTestModelAtPixel(pick.screen, pick.pixel.x, pick.pixel.y);
            return;
        }

        Vector3 rayDir = pick.ray.direction.normalized;
        if (rayDir == Vector3.zero)
        {
            TrySpawnOrMoveTestModelAtPixel(pick.screen, pick.pixel.x, pick.pixel.y);
            return;
        }

        float depthTowardCamera = testDepthMeters;
        float placeDist = Mathf.Max(0.05f, pick.hitDistance - depthTowardCamera);
        Vector3 world = pick.ray.origin + rayDir * placeDist;

        Vector3 right = Vector3.Cross(Vector3.up, rayDir);
        if (right.sqrMagnitude < 0.000001f)
        {
            right = pick.screen.right;
        }
        right.Normalize();
        Vector3 up = Vector3.Cross(rayDir, right).normalized;

        world += right * testModelOffsetMeters.x + up * testModelOffsetMeters.y;
        Quaternion rotation = Quaternion.LookRotation(-rayDir, up);

        ApplyTestModelTransform(world, rotation, pick.screen, pick.pixel.x, pick.pixel.y, pick.hitDistance, depthTowardCamera, "ray");
    }

    private void TrySpawnOrMoveTestModelAtPixel(Transform screen, int u, int v)
    {
        if (replacePrefab != null)
        {
            return;
        }

        if (screen == null)
        {
            Debug.LogWarning("TrySpawnOrMoveTestModelAtPixel: screen is null.");
            return;
        }

        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            Debug.LogWarning("TrySpawnOrMoveTestModelAtPixel: manifest not ready.");
            return;
        }

        Vector3 worldOnPlane = EyePixelToWorldOnScreen(u, v, screen, manifest.eye_w, manifest.eye_h, 0f);
        Transform head = GetViewCamera() != null ? GetViewCamera().transform : GetHeadTransform();
        Vector3 frontDir = (head != null)
            ? (head.position - screen.position).normalized
            : GetScreenFrontDirection(screen);
        if (frontDir == Vector3.zero)
        {
            frontDir = GetScreenFrontDirection(screen);
        }

        float dist = head != null ? Vector3.Distance(head.position, screen.position) : testDepthMeters;
        float maxDepth = Mathf.Max(0.01f, dist - 0.05f);
        float depth = Mathf.Clamp(testDepthMeters, 0.01f, maxDepth);

        Vector3 world = worldOnPlane
            + screen.right * testModelOffsetMeters.x
            + screen.up * testModelOffsetMeters.y
            + frontDir * depth;
        Quaternion rotation = Quaternion.LookRotation(-frontDir, screen.up);

        ApplyTestModelTransform(world, rotation, screen, u, v, dist, depth, "pixel");
    }

    private void ApplyTestModelTransform(Vector3 world, Quaternion rotation, Transform screen, int u, int v, float dist, float depth, string mode)
    {
        if (destroyPreviousTestModel && spawnedTestModel != null)
        {
            Destroy(spawnedTestModel);
            spawnedTestModel = null;
        }

        if (spawnedTestModel == null)
        {
            spawnedTestModel = testModelPrefab != null
                ? Instantiate(testModelPrefab, world, rotation)
                : GameObject.CreatePrimitive(PrimitiveType.Cube);
            spawnedTestModel.name = "TestModel(auto)";
            if (testModelPrefab == null)
            {
                var collider = spawnedTestModel.GetComponent<Collider>();
                if (collider != null)
                {
                    Destroy(collider);
                }
            }
        }
        spawnedTestModel.transform.SetParent(null, true);
        EnsureTestModelComponents(spawnedTestModel);
        spawnedTestModel.transform.SetPositionAndRotation(world, rotation);
        spawnedTestModel.transform.localScale = Vector3.one * testModelSizeMeters;
        LogModel($"SpawnOrMoveTestModel({mode}): screen={screen.name} pixel=({u},{v}) world={world} rot={rotation.eulerAngles}");
        LogModel($"SpawnOrMoveTestModelDepth({mode}): dist={dist:F3} depth={depth:F3} screen={screen.position}");
        LogModel(
            $"SpawnOrMoveTestModelDebug: worldPos={spawnedTestModel.transform.position} localPos={spawnedTestModel.transform.localPosition} " +
            $"parent={(spawnedTestModel.transform.parent != null ? spawnedTestModel.transform.parent.name : "null")}");
        AttachTransformLock(spawnedTestModel, world, rotation);

        float posError = Vector3.Distance(spawnedTestModel.transform.position, world);
        if (posError > 0.001f)
        {
            Debug.LogWarning(
                $"TestModelPositionMismatch: expected={world} actual={spawnedTestModel.transform.position} " +
                $"error={posError:F4} active={spawnedTestModel.activeInHierarchy} " +
                $"components={DescribeMovementComponents(spawnedTestModel)}");
        }
    }

    private void EnsureTestModelComponents(GameObject model)
    {
        if (model == null)
        {
            return;
        }

        var rb = model.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.constraints = RigidbodyConstraints.FreezeAll;
        }

        var animator = model.GetComponent<Animator>();
        if (animator != null)
        {
            animator.enabled = false;
        }
    }

    private void AttachTransformLock(GameObject model, Vector3 world, Quaternion rotation)
    {
        if (model == null)
        {
            return;
        }

        var locker = model.GetComponent<TestModelTransformLock>();
        if (locker == null)
        {
            locker = model.AddComponent<TestModelTransformLock>();
        }

        locker.Arm(world, rotation, TestModelLockFrames);
    }

    private string DescribeMovementComponents(GameObject go)
    {
        if (go == null)
        {
            return "null";
        }

        var components = go.GetComponents<Component>();
        string allComponents = components != null && components.Length > 0
            ? string.Join(",", System.Array.ConvertAll(components, c => c != null ? c.GetType().Name : "null"))
            : "none";

        return $"Components[{allComponents}]";
    }

    private sealed class TestModelTransformLock : MonoBehaviour
    {
        private Vector3 targetPos;
        private Quaternion targetRot;
        private int framesLeft;

        public void Arm(Vector3 world, Quaternion rotation, int frames)
        {
            targetPos = world;
            targetRot = rotation;
            framesLeft = Mathf.Max(1, frames);
        }

        private void LateUpdate()
        {
            if (framesLeft <= 0)
            {
                Destroy(this);
                return;
            }

            transform.SetPositionAndRotation(targetPos, targetRot);
            framesLeft--;
        }
    }
}
