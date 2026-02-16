using UnityEngine;
using UnityEngine.Video;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private void EnsureScreensExist()
    {
        if (leftScreen == null)
        {
            var leftObj = GameObject.CreatePrimitive(PrimitiveType.Quad);
            leftObj.name = "LeftScreen_Runtime";
            leftObj.transform.SetParent(transform, false);
            leftScreen = leftObj.transform;
        }

        if (rightScreen == null)
        {
            var rightObj = GameObject.CreatePrimitive(PrimitiveType.Quad);
            rightObj.name = "RightScreen_Runtime";
            rightObj.transform.SetParent(transform, false);
            rightScreen = rightObj.transform;
        }

        EnsureScreenRenderer(leftScreen, "leftScreen");
        EnsureScreenRenderer(rightScreen, "rightScreen");
        EnsureScreenCollider(leftScreen, "leftScreen");
        EnsureScreenCollider(rightScreen, "rightScreen");
    }

    private Renderer EnsureScreenRenderer(Transform screen, string label)
    {
        if (screen == null)
        {
            return null;
        }

        var meshFilter = screen.GetComponent<MeshFilter>();
        if (meshFilter == null)
        {
            meshFilter = screen.gameObject.AddComponent<MeshFilter>();
        }

        if (meshFilter.sharedMesh == null)
        {
            if (quadMesh == null)
            {
                quadMesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            }

            if (quadMesh != null)
            {
                meshFilter.sharedMesh = quadMesh;
            }
            else
            {
                Debug.LogWarning($"Quad mesh not found for {label}.");
            }
        }

        var renderer = screen.GetComponent<MeshRenderer>();
        if (renderer == null)
        {
            renderer = screen.gameObject.AddComponent<MeshRenderer>();
        }

        renderer.enabled = true;
        EnsureUnlitMaterial(renderer, label);
        return renderer;
    }

    private void EnsureScreenCollider(Transform screen, string label)
    {
        if (screen == null)
        {
            return;
        }

        var collider = screen.GetComponent<Collider>();
        if (collider == null)
        {
            var meshCollider = screen.gameObject.AddComponent<MeshCollider>();
            var meshFilter = screen.GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh != null)
            {
                meshCollider.sharedMesh = meshFilter.sharedMesh;
            }
            else
            {
                Debug.LogWarning($"EnsureScreenCollider: mesh missing for {label}.");
            }
        }
    }

    private void EnsureUnlitMaterial(Renderer renderer, string label)
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
            renderer.material = mat;
        }
        else
        {
            Debug.LogWarning($"Fallback shader not found for {label}.");
        }
    }

    private void SetupScreensAndMaterials()
    {
        Renderer leftRenderer = EnsureScreenRenderer(leftScreen, "leftScreen");
        Renderer rightRenderer = EnsureScreenRenderer(rightScreen, "rightScreen");

        leftMat = CreateUniqueMaterial(leftRenderer, "left");
        rightMat = CreateUniqueMaterial(rightRenderer, "right");

        leftTexProp = ResolveTexProp(leftMat, "left");
        rightTexProp = ResolveTexProp(rightMat, "right");

        ApplyStereoUvSettings(leftMat, 1, "left");
        ApplyStereoUvSettings(rightMat, 2, "right");
    }

    private Material CreateUniqueMaterial(Renderer renderer, string label)
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
                Debug.LogWarning($"CreateUniqueMaterial: no shader for {label}.");
                return null;
            }

            baseMat = new Material(fallbackShader);
        }

        var uniqueMat = new Material(baseMat);
        renderer.material = uniqueMat;
        return uniqueMat;
    }

    private string ResolveTexProp(Material mat, string label)
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

        Debug.LogWarning($"ResolveTexProp: no known property on {label} material.");
        return "_MainTex";
    }

    private void ApplyVideoFrameTexture(VideoPlayer player)
    {
        if (player == null || player.texture == null)
        {
            return;
        }

        if (leftMat != null && leftMat.HasProperty(leftTexProp))
        {
            leftMat.SetTexture(leftTexProp, player.texture);
        }

        if (rightMat != null && rightMat.HasProperty(rightTexProp))
        {
            rightMat.SetTexture(rightTexProp, player.texture);
        }
    }

    private void ApplyStereoUvSettings(Material mat, int eyeMode, string label)
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

    private void PlaceScreens()
    {
        Camera viewCam = GetViewCamera();
        Transform head = viewCam != null ? viewCam.transform : GetHeadTransform();
        LockPinholeBasis(head);
        Vector3 headPos = head.position;
        Vector3 headFwd = head.forward;
        Vector3 center = headPos + headFwd * screenDistanceMeters + head.TransformVector(screenOffsetMeters);
        Vector3 toHead = (headPos - center).normalized;
        Quaternion rotation = Quaternion.LookRotation(toHead, head.up);

        if (fitScreenToFov && manifest != null && manifest.eye_w > 0 && manifest.eye_h > 0 && TryGetFovxDeg(out float fovxDeg))
        {
            float distance = Mathf.Max(0.0001f, screenDistanceMeters);
            float fovxRad = fovxDeg * Mathf.Deg2Rad;
            float width = 2f * distance * Mathf.Tan(fovxRad * 0.5f);
            float height = width * (manifest.eye_h / (float)manifest.eye_w);
            ApplyScreenScaleToFitFov(leftScreen, width, height);
            ApplyScreenScaleToFitFov(rightScreen, width, height);
            // Intentional: PlaceScreens logs are disabled in the category-only logger.
        }

        Vector3 rightOffset = head.right * 0.001f;
        if (leftScreen != null)
        {
            leftScreen.position = center - rightOffset;
            leftScreen.rotation = rotation;
        }

        if (rightScreen != null)
        {
            rightScreen.position = center + rightOffset;
            rightScreen.rotation = rotation;
        }

        if (!fitScreenToFov && leftScreen != null)
        {
            GetScreenSizeMeters(leftScreen, out float width, out float height, out _);
            // Intentional: PlaceScreens logs are disabled in the category-only logger.
        }

        FixFacingIfNeeded(leftScreen, head, "left");
        FixFacingIfNeeded(rightScreen, head, "right");
        UpdateRuntimeControlsPlacement();
    }

    private void FixFacingIfNeeded(Transform screen, Transform head, string label)
    {
        if (screen == null || head == null)
        {
            return;
        }

        Vector3 normalLocal = GetAverageNormalLocal(screen);
        Vector3 normalWorld = screen.TransformDirection(normalLocal).normalized;
        Vector3 toHead = (head.position - screen.position).normalized;
        float dotBefore = Vector3.Dot(normalWorld, toHead);
        LogScreens($"ScreenFacingMeshNormal[{label}]: normalLocal={normalLocal} normalWorld={normalWorld} toHead={toHead} dotBefore={dotBefore:F3}");

        if (dotBefore < 0f)
        {
            screen.Rotate(0f, 180f, 0f, Space.Self);
            Vector3 normalWorldAfter = screen.TransformDirection(normalLocal).normalized;
            float dotAfter = Vector3.Dot(normalWorldAfter, toHead);
            LogScreens($"ScreenFacingMeshNormalFix[{label}]: dotAfter={dotAfter:F3} newNormalWorld={normalWorldAfter}");
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
        screen.localScale = scale;
    }

    private void ApplyScreenFallbackMagenta()
    {
        if (fallbackApplied)
        {
            return;
        }

        fallbackApplied = true;
        ApplyFallbackToScreen(leftScreen, "left");
        ApplyFallbackToScreen(rightScreen, "right");
    }

    private void ApplyFallbackToScreen(Transform screen, string label)
    {
        if (screen == null)
        {
            Debug.LogWarning($"Fallback skipped: {label} screen is null.");
            return;
        }

        var renderer = screen.GetComponent<Renderer>();
        if (renderer == null || renderer.material == null)
        {
            Debug.LogWarning($"Fallback skipped: {label} renderer/material missing.");
            return;
        }

        var mat = renderer.material;
        if (mat.HasProperty("_BaseColor"))
        {
            mat.SetColor("_BaseColor", Color.magenta);
        }

        if (mat.HasProperty("_BaseMap"))
        {
            mat.SetTexture("_BaseMap", null);
        }

        LogScreens($"Fallback applied: {label} screen set to magenta.");
    }

    private void TrySpawnDebugMarker()
    {
        if (leftScreen == null)
        {
            Debug.LogWarning("Debug marker skipped: leftScreen is null.");
            return;
        }

        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            Debug.LogWarning("Debug marker skipped: manifest eye_w/eye_h invalid or not loaded.");
            return;
        }

        Vector2Int finalPixel = debugPixel;
        if (finalPixel.x < 0 || finalPixel.y < 0)
        {
            finalPixel = new Vector2Int(manifest.eye_w / 2, manifest.eye_h / 2);
        }

        Vector3 world = EyePixelToWorldOnScreen(finalPixel.x, finalPixel.y, leftScreen, manifest.eye_w, manifest.eye_h, markerOffset);

        LogScreens(
            $"SpawnDebugMarker: eye_w={manifest.eye_w} eye_h={manifest.eye_h} " +
            $"debugPixel=({finalPixel.x},{finalPixel.y}) " +
            $"leftScreen scale={leftScreen.localScale} pos={leftScreen.position} rot={leftScreen.rotation.eulerAngles} " +
            $"world={world}");

        GameObject marker = debugMarkerPrefab != null
            ? Instantiate(debugMarkerPrefab, world, leftScreen.rotation)
            : GameObject.CreatePrimitive(PrimitiveType.Sphere);

        if (debugMarkerPrefab == null)
        {
            marker.name = "DebugMarker(auto)";
            var collider = marker.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }
        }

        marker.transform.position = world;
        marker.transform.rotation = leftScreen.rotation;
        marker.transform.localScale = Vector3.one * debugMarkerScale;
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

        if (verboseLog)
        {
            Vector3 s = screen.lossyScale;
            LogScreens(
                $"EyePixelToWorldOnScreenF: u={u:F2} v={v:F2} eyeW={eyeW} eyeH={eyeH} " +
                $"lossy=({s.x:F3},{s.y:F3},{s.z:F3}) xN={xN:F4} yN={yN:F4} world={world}");
        }
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
            float min = Mathf.Min(runtimeFovxMinDeg, runtimeFovxMaxDeg);
            float max = Mathf.Max(runtimeFovxMinDeg, runtimeFovxMaxDeg);
            fovxDeg = Mathf.Clamp(runtimeFovxDeg, min, max);
            if (verboseLog && !loggedFovSource)
            {
                LogMeta($"FOVx source=runtimeOverride fovx_deg={fovxDeg}");
                loggedFovSource = true;
            }
            return true;
        }

        float manifestFovx = GetManifestFovxDeg();
        if (manifestFovx > 0f)
        {
            fovxDeg = manifestFovx;
            if (verboseLog && !loggedFovSource)
            {
                LogMeta($"FOVx source=manifest fovx_deg={fovxDeg}");
                loggedFovSource = true;
            }
            return true;
        }

        if (metaHeader.fovxDeg > 0f)
        {
            fovxDeg = metaHeader.fovxDeg;
            if (verboseLog && !loggedFovSource)
            {
                LogMeta($"FOVx source=metaHeader fovx_deg={fovxDeg}");
                loggedFovSource = true;
            }
            return true;
        }

        Debug.LogWarning("FOVx not available; no fallback value.");
        return false;
    }

    private bool TryGetFocalLengths(out float fx, out float fy)
    {
        fx = 0f;
        fy = 0f;
        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return false;
        }

        if (!TryGetFovxDeg(out float fovxDeg))
        {
            return false;
        }

        float fovxRad = fovxDeg * Mathf.Deg2Rad;
        fx = 1f / Mathf.Tan(fovxRad * 0.5f);
        fy = fx * (manifest.eye_w / (float)manifest.eye_h);
        return fx > 0f && fy > 0f;
    }

    private Vector3 AnchorUvZToWorld(Transform screen, float u, float v, float zMeters)
    {
        if (screen == null || manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return Vector3.zero;
        }

        if (!TryGetFocalLengths(out float fx, out float fy))
        {
            return Vector3.zero;
        }

        float xNdc = (u / manifest.eye_w - 0.5f) * 2f;
        float yNdc = (0.5f - v / manifest.eye_h) * 2f;

        float x = xNdc * zMeters / fx;
        float y = yNdc * zMeters / fy;
        float z = zMeters;

        Transform head = GetViewCamera() != null ? GetViewCamera().transform : GetHeadTransform();
        Vector3 origin = head != null ? head.position : Vector3.zero;

        Vector3 world = origin + screen.right * x + screen.up * y + screen.forward * z;
        return world;
    }

    private Vector3 ReconstructCamLocalFromEyePixel(float uEye, float vEye, float zMeters, float fx, float fy, int eyeW, int eyeH)
    {
        float xNdc = (uEye / (float)eyeW - 0.5f) * 2f;
        float yNdc = (0.5f - vEye / (float)eyeH) * 2f;
        return new Vector3(xNdc * zMeters / fx, yNdc * zMeters / fy, zMeters);
    }

    private void LockPinholeBasis(Transform head)
    {
        if (head == null)
        {
            return;
        }

        hasLockedPinholeBasis = true;
        lockedPinholeOrigin = head.position + head.TransformVector(screenOffsetMeters);
        lockedPinholeRotation = head.rotation;
    }

    private bool TryGetPinholeBasis(Transform screen, out Vector3 camOrigin, out Quaternion camRotation)
    {
        camOrigin = Vector3.zero;
        camRotation = Quaternion.identity;
        if (hasLockedPinholeBasis)
        {
            camOrigin = lockedPinholeOrigin;
            camRotation = lockedPinholeRotation;
            return true;
        }

        if (screen == null)
        {
            return false;
        }

        Vector3 camForward = -screen.forward;
        if (camForward.sqrMagnitude < 0.000001f)
        {
            camForward = Vector3.forward;
        }
        camForward.Normalize();

        Vector3 camUp = screen.up;
        if (camUp.sqrMagnitude < 0.000001f)
        {
            camUp = Vector3.up;
        }
        camUp.Normalize();

        camRotation = Quaternion.LookRotation(camForward, camUp);
        camOrigin = screen.position + screen.forward * screenDistanceMeters;
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
            Debug.LogWarning("AnchorUvZToWorldPinhole: screen missing.");
            return Vector3.zero;
        }

        Vector3 camLocal = ReconstructCamLocalFromEyePixel(uEye, vEye, zMeters, fx, fy, manifest.eye_w, manifest.eye_h);
        return camOrigin + (camRotation * camLocal);
    }
}
