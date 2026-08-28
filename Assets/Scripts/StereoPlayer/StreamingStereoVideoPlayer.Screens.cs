using UnityEngine;
using UnityEngine.Video;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private const float StereoScreenEyeSeparationMeters = 0.001f;

    private void EnsureScreensExist()
    {
        RecreateRuntimeScreen(ref leftScreen, "LeftScreen_Runtime", leftScreenPrefab);
        RecreateRuntimeScreen(ref rightScreen, "RightScreen_Runtime", rightScreenPrefab);

        EnsureScreenRenderer(leftScreen);
        EnsureScreenRenderer(rightScreen);
        EnsureScreenCollider(leftScreen);
        EnsureScreenCollider(rightScreen);
    }

    private void RecreateRuntimeScreen(ref Transform screenSlot, string runtimeName, GameObject screenPrefab)
    {
        if (screenSlot != null)
        {
            SceneObjectWriter.DestroyObject(screenSlot.gameObject);
            screenSlot = null;
        }

        screenSlot = CreateRuntimeScreen(runtimeName, screenPrefab);
    }

    private Transform CreateRuntimeScreen(string runtimeName, GameObject screenPrefab)
    {
        return RuntimeScreenFactory.Create(runtimeName, screenPrefab, transform);
    }

    private Renderer EnsureScreenRenderer(Transform screen)
    {
        if (screen == null)
        {
            return null;
        }

        var meshFilter = RuntimeScreenComponentFactory.EnsureMeshFilter(screen.gameObject);

        if (meshFilter.sharedMesh == null)
        {
            if (quadMesh == null)
            {
                quadMesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            }

            SceneObjectWriter.ApplySharedMesh(meshFilter, quadMesh);
        }

        var renderer = RuntimeScreenComponentFactory.EnsureMeshRenderer(screen.gameObject);

        SceneObjectWriter.ApplyRendererEnabled(renderer, true);
        EnsureUnlitMaterial(renderer);
        return renderer;
    }

    private void EnsureScreenCollider(Transform screen)
    {
        if (screen == null)
        {
            return;
        }

        MeshFilter meshFilter = screen.GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.sharedMesh == null)
        {
            return;
        }

        MeshCollider meshCollider = null;
        Collider[] colliders = screen.GetComponents<Collider>();
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider collider = colliders[i];
            if (collider == null)
            {
                continue;
            }

            if (collider is MeshCollider asMesh && meshCollider == null)
            {
                meshCollider = asMesh;
                continue;
            }

            // Keep screen hit area tightly matched to the rendered quad.
            SceneObjectWriter.DestroyObject(collider);
        }

        meshCollider ??= RuntimeScreenComponentFactory.EnsureMeshCollider(screen.gameObject);

        SceneObjectWriter.ApplyMeshCollider(meshCollider, meshFilter.sharedMesh);
    }

    private void EnsureUnlitMaterial(Renderer renderer)
    {
        if (renderer == null)
        {
            return;
        }

        var mat = renderer.material;
        if (mat != null)
        {
            return;
        }

        var shader = Shader.Find("Custom/PerEyeStereoVideoURP");
        if (shader == null)
        {
            shader = Shader.Find("pereyestereovideoURP");
        }

        if (shader == null)
        {
            shader = Shader.Find("Universal Render Pipeline/Unlit");
        }

        if (shader == null)
        {
            shader = Shader.Find("Unlit/Texture");
        }

        if (shader != null)
        {
            mat = new Material(shader);
            SceneObjectWriter.ApplyMaterial(renderer, mat);
        }
    }

    private void SetupScreensAndMaterials()
    {
        Renderer leftRenderer = EnsureScreenRenderer(leftScreen);
        Renderer rightRenderer = EnsureScreenRenderer(rightScreen);

        leftMat = CreateUniqueMaterial(leftRenderer);
        rightMat = CreateUniqueMaterial(rightRenderer);

        leftTexProp = ResolveTexProp(leftMat);
        rightTexProp = ResolveTexProp(rightMat);

        ApplyStereoUvSettings(leftMat, 1);
        ApplyStereoUvSettings(rightMat, 2);

        // スクリーンは背景として先に描き、深度を書かない。
        // 既定のままだと、配置されたモデルの深度がスクリーンより奥になったフレームで
        // モデルがスクリーンに隠れて消える（2026-08-20 実測で 8.1% のフレーム）。
        // Custom/PerEyeStereoVideoURP は shader 側で Queue/ZWrite/ZTest を指定しているが、
        // フォールバックシェーダー（URP/Unlit 等）が使われた場合の保険としてここでも設定する。
        ForceBackgroundDrawOrder(leftMat);
        ForceBackgroundDrawOrder(rightMat);
    }

    private static void ForceBackgroundDrawOrder(Material mat)
    {
        if (mat == null)
        {
            return;
        }

        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Background;
        if (mat.HasProperty("_ZWrite"))
        {
            mat.SetFloat("_ZWrite", 0f);
        }

        if (mat.HasProperty("_ZTest"))
        {
            mat.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
        }
    }

    private Material CreateUniqueMaterial(Renderer renderer)
    {
        if (renderer == null)
        {
            return null;
        }

        Material baseMat = renderer.sharedMaterial;
        if (baseMat == null)
        {
            var fallbackShader = Shader.Find("Custom/PerEyeStereoVideoURP");
            if (fallbackShader == null)
            {
                fallbackShader = Shader.Find("Universal Render Pipeline/Unlit");
            }

            if (fallbackShader == null)
            {
                return null;
            }

            baseMat = new Material(fallbackShader);
        }

        var uniqueMat = new Material(baseMat);
        SceneObjectWriter.ApplyMaterial(renderer, uniqueMat);
        return uniqueMat;
    }

    private string ResolveTexProp(Material mat)
    {
        if (mat == null)
        {
            return "_MainTex";
        }

        if (mat.HasProperty("_MainTex"))
        {
            return "_MainTex";
        }

        if (mat.HasProperty("_BaseMap"))
        {
            return "_BaseMap";
        }

        return "_MainTex";
    }

    private void ApplyVideoFrameTexture(VideoPlayer player)
    {
        if (player == null || player.texture == null)
        {
            return;
        }

        SceneObjectWriter.ApplyTexture(leftMat, leftTexProp, player.texture);
        SceneObjectWriter.ApplyTexture(rightMat, rightTexProp, player.texture);
    }

    private void ApplyStereoUvSettings(Material mat, int eyeMode)
    {
        if (mat == null)
        {
            return;
        }

        if (mat.HasProperty("_EyeMode"))
        {
            mat.SetInt("_EyeMode", eyeMode);
        }

        if (mat.HasProperty("_UVScale"))
        {
            mat.SetVector("_UVScale", new Vector4(0.5f, 1f, 0f, 0f));
        }

        if (mat.HasProperty("_UVOffset"))
        {
            float uOffset = eyeMode == 2 ? 0.5f : 0f;
            mat.SetVector("_UVOffset", new Vector4(uOffset, 0f, 0f, 0f));
        }
    }

    // 視聴の基準はヨーだけにする。ピッチを含めると、置き直した瞬間に上（下）を向いていた
    // 角度ぶんスクリーンが持ち上がる（1.5m 先で 30 度なら 0.87m）。実機で「動画が少し高い」
    // と言われたのがこれ（2026-08-28。ピッカーが上に出ていたのと同じ原因）。
    //
    // **スクリーンと pinhole 基準は必ず同じ回転を使う。** LockPinholeBasis はモデル配置の
    // 投影基準で、片方だけ水平化すると「スクリーンは水平なのにモデルは傾いた基準で置かれる」
    // ことになり、映像とモデルがずれる。
    private static Quaternion ResolveYawOnlyViewRotation(Quaternion headRotation)
    {
        Vector3 forward = headRotation * Vector3.forward;
        Vector3 flat = Vector3.ProjectOnPlane(forward, Vector3.up);
        if (flat.sqrMagnitude < 0.000001f)
        {
            // 真上か真下を向いている。頭の up から前方を作る。
            flat = Vector3.ProjectOnPlane(headRotation * Vector3.up, Vector3.up);
        }

        if (flat.sqrMagnitude < 0.000001f)
        {
            return headRotation;
        }

        return Quaternion.LookRotation(flat.normalized, Vector3.up);
    }


    private void PlaceScreens()
    {
        Camera viewCam = GetViewCamera();
        Transform head = viewCam != null ? viewCam.transform : GetHeadTransform();
        Quaternion viewRotation = ResolveYawOnlyViewRotation(head.rotation);
        LockPinholeBasis(head.position, viewRotation);
        StereoScreenPlacement.Placement placement = StereoScreenPlacement.ResolvePlacement(
            head.position,
            viewRotation,
            screenDistanceMeters,
            screenOffsetMeters,
            StereoScreenEyeSeparationMeters);

        if (fitScreenToFov && manifest != null && manifest.eye_w > 0 && manifest.eye_h > 0 && TryGetFovxDeg(out float fovxDeg))
        {
            StereoScreenPlacement.ResolveFitSize(
                screenDistanceMeters,
                fovxDeg,
                manifest.eye_w,
                manifest.eye_h,
                out float width,
                out float height);
            ApplyScreenScaleToFitFov(leftScreen, width, height);
            ApplyScreenScaleToFitFov(rightScreen, width, height);
        }

        // Keep ISDK ray interaction surfaces aligned with the current screen mesh size.
        SyncRayCanvasInteractionSurfaceToScreen(leftScreen);
        SyncRayCanvasInteractionSurfaceToScreen(rightScreen);

        if (leftScreen != null)
        {
            TransformWriter.ApplyPose(leftScreen, placement.leftPosition, placement.rotation);
        }

        if (rightScreen != null)
        {
            TransformWriter.ApplyPose(rightScreen, placement.rightPosition, placement.rotation);
        }

        FixFacingIfNeeded(leftScreen, head);
        FixFacingIfNeeded(rightScreen, head);
        UpdateRuntimeControlsPlacement();
    }

    private void SyncRayCanvasInteractionSurfaceToScreen(Transform screen)
    {
        if (screen == null)
        {
            return;
        }

        GetScreenMeshLocalBounds(screen, out _, out Vector3 meshSizeLocal);
        InteractionSdkRayCanvasAdapter.SyncSurfaceToScreen(screen, meshSizeLocal);
    }

    private static Transform FindDeepChildByName(Transform root, string exactName)
    {
        if (root == null || string.IsNullOrEmpty(exactName))
        {
            return null;
        }

        if (root.name == exactName)
        {
            return root;
        }

        for (int i = 0; i < root.childCount; i++)
        {
            Transform child = root.GetChild(i);
            Transform found = FindDeepChildByName(child, exactName);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private void FixFacingIfNeeded(Transform screen, Transform head)
    {
        if (screen == null || head == null)
        {
            return;
        }

        Vector3 normalLocal = GetAverageNormalLocal(screen);
        Vector3 normalWorld = screen.TransformDirection(normalLocal).normalized;
        Vector3 toHead = (head.position - screen.position).normalized;
        float dotBefore = Vector3.Dot(normalWorld, toHead);

        if (dotBefore < 0f)
        {
            TransformWriter.RotateSelf(screen, 0f, 180f, 0f);
        }
    }

    private Vector3 GetAverageNormalLocal(Transform screen)
    {
        var meshFilter = screen.GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.sharedMesh == null)
        {
            return Vector3.forward;
        }

        Vector3[] normals = meshFilter.sharedMesh.normals;
        if (normals == null || normals.Length == 0)
        {
            return Vector3.forward;
        }

        Vector3 sum = Vector3.zero;
        for (int i = 0; i < normals.Length; i++)
        {
            sum += normals[i];
        }

        if (sum == Vector3.zero)
        {
            return Vector3.forward;
        }

        return sum.normalized;
    }

    private Vector3 GetScreenFrontDirection(Transform screen)
    {
        Vector3 normalLocal = GetAverageNormalLocal(screen);
        Vector3 normalWorld = screen.TransformDirection(normalLocal).normalized;
        if (normalWorld == Vector3.zero)
        {
            normalWorld = screen.forward;
        }

        return normalWorld;
    }

    private void ApplyScreenScaleToFitFov(Transform screen, float targetWidth, float targetHeight)
    {
        if (screen == null)
        {
            return;
        }

        GetScreenSizeMeters(screen, out float currentWidth, out float currentHeight, out _);
        float meshWidth = Mathf.Abs(screen.localScale.x) > 0f ? currentWidth / screen.localScale.x : 0f;
        float meshHeight = Mathf.Abs(screen.localScale.y) > 0f ? currentHeight / screen.localScale.y : 0f;
        if (meshWidth <= 0f || meshHeight <= 0f)
        {
            return;
        }

        Vector3 scale = screen.localScale;
        scale.x = targetWidth / meshWidth;
        scale.y = targetHeight / meshHeight;
        TransformWriter.ApplyLocalScale(screen, scale);
    }

    private Vector3 EyePixelToWorldOnScreen(int u, int v, Transform screen, float eyeW, float eyeH, float offsetMeters)
        => EyePixelToWorldOnScreen((float)u, (float)v, screen, eyeW, eyeH, offsetMeters);

    private Vector3 EyePixelToWorldOnScreen(float u, float v, Transform screen, float eyeW, float eyeH, float offsetMeters)
    {
        float xN = (u / eyeW) - 0.5f;
        float yN = 0.5f - (v / eyeH);
        Vector3 local = new Vector3(xN, yN, 0f);
        Vector3 worldOnPlane = screen.TransformPoint(local);
        Vector3 world = worldOnPlane + screen.forward * offsetMeters;
        return world;
    }

    private void GetScreenMeshLocalBounds(Transform screen, out Vector3 center, out Vector3 size)
    {
        center = Vector3.zero;
        size = Vector3.one;
        if (screen == null)
        {
            return;
        }

        MeshFilter meshFilter = screen.GetComponent<MeshFilter>();
        if (meshFilter != null && meshFilter.sharedMesh != null)
        {
            Bounds bounds = meshFilter.sharedMesh.bounds;
            center = bounds.center;
            size = bounds.size;
            return;
        }

        Renderer renderer = screen.GetComponent<Renderer>();
        if (renderer != null)
        {
            Bounds bounds = renderer.localBounds;
            center = bounds.center;
            size = bounds.size;
        }
    }

    private void GetScreenSizeMeters(Transform screen, out float width, out float height, out Vector3 localCenterOffset)
    {
        width = 1f;
        height = 1f;
        localCenterOffset = Vector3.zero;
        if (screen == null)
        {
            return;
        }

        GetScreenMeshLocalBounds(screen, out Vector3 center, out Vector3 size);
        width = size.x * screen.localScale.x;
        height = size.y * screen.localScale.y;
        localCenterOffset = Vector3.Scale(center, screen.localScale);
    }

    private bool TryGetFovxDeg(out float fovxDeg)
    {
        fovxDeg = 0f;
        if (useRuntimeFovxOverride)
        {
            float min = Mathf.Min(RuntimeFovxMinDeg, RuntimeFovxMaxDeg);
            float max = Mathf.Max(RuntimeFovxMinDeg, RuntimeFovxMaxDeg);
            fovxDeg = Mathf.Clamp(runtimeFovxDeg, min, max);
            return true;
        }

        float manifestFovx = GetManifestFovxDeg();
        if (manifestFovx > 0f)
        {
            fovxDeg = manifestFovx;
            return true;
        }

        if (metaHeader.fovxDeg > 0f)
        {
            fovxDeg = metaHeader.fovxDeg;
            return true;
        }

        return false;
    }

    private bool TryGetFocalLengths(out float fx, out float fy)
    {
        return TryGetProjectionIntrinsics(out fx, out fy, out _, out _);
    }

    private bool TryGetProjectionIntrinsics(out float fx, out float fy, out float cxPixels, out float cyPixels)
    {
        bool hasFovxDeg = TryGetFovxDeg(out float fovxDeg);
        return PinholePlacementSpace.TryResolveProjectionIntrinsics(
            manifest,
            hasFovxDeg,
            fovxDeg,
            out fx,
            out fy,
            out cxPixels,
            out cyPixels);
    }

    private Vector3 ReconstructCamLocalFromEyePixel(float uEye, float vEye, float zMeters, float fx, float fy, int eyeW, int eyeH)
    {
        return PinholePlacementSpace.ReconstructCamLocalFromEyePixel(
            manifest,
            uEye,
            vEye,
            zMeters,
            fx,
            fy,
            eyeW,
            eyeH);
    }

    private void LockPinholeBasis(Transform head)
    {
        if (head == null)
        {
            return;
        }

        LockPinholeBasis(head.position, head.rotation);
    }


    private void LockPinholeBasis(Vector3 headPosition, Quaternion viewRotation)
    {
        hasLockedPinholeBasis = true;
        lockedPinholeOrigin = headPosition + viewRotation * screenOffsetMeters;
        lockedPinholeRotation = viewRotation;
    }

    private bool TryGetHeadVirtualOrigin(out Vector3 origin, out Quaternion rotation)
    {
        origin = Vector3.zero;
        rotation = Quaternion.identity;

        if (hasLockedPinholeBasis)
        {
            origin = lockedPinholeOrigin;
            rotation = lockedPinholeRotation;
            return true;
        }

        Transform head = GetViewOrHeadTransform();
        if (head == null)
        {
            return false;
        }

        origin = head.position + head.TransformVector(screenOffsetMeters);
        rotation = head.rotation;
        return true;
    }

    private bool TryGetPinholeBasis(Transform screen, out Vector3 camOrigin, out Quaternion camRotation)
    {
        camOrigin = Vector3.zero;
        camRotation = Quaternion.identity;

        if (screen == null)
        {
            return TryGetHeadVirtualOrigin(out camOrigin, out camRotation);
        }

        Vector3 screenFront = GetScreenFrontDirection(screen);
        if (screenFront.sqrMagnitude < 0.000001f)
        {
            screenFront = screen.forward;
        }
        screenFront.Normalize();

        Vector3 camForward = -screenFront;

        Vector3 camUp = screen.up;
        if (camUp.sqrMagnitude < 0.000001f)
        {
            camUp = Vector3.up;
        }
        camUp.Normalize();

        camRotation = Quaternion.LookRotation(camForward, camUp);
        if (!TryGetHeadVirtualOrigin(out camOrigin, out _))
        {
            camOrigin = screen.position + screenFront * screenDistanceMeters;
        }
        return true;
    }

    private Quaternion GetPinholeBasisRotation(Transform screen)
    {
        if (TryGetPinholeBasis(screen, out _, out Quaternion rotation))
        {
            return rotation;
        }

        return screen != null ? screen.rotation : Quaternion.identity;
    }

    private Vector3 AnchorUvZToWorldPinhole(Transform screen, float uEye, float vEye, float zMeters)
    {
        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return Vector3.zero;
        }

        if (!TryGetFocalLengths(out float fx, out float fy))
        {
            return Vector3.zero;
        }

        if (!TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
        {
            return Vector3.zero;
        }

        return PinholePlacementSpace.EyePixelDepthToWorld(
            camOrigin,
            camRotation,
            manifest,
            uEye,
            vEye,
            zMeters,
            fx,
            fy);
    }
}
