using System.Reflection;
using UnityEngine;
using UnityEngine.Video;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
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
            Destroy(screenSlot.gameObject);
            screenSlot = null;
        }

        screenSlot = CreateRuntimeScreen(runtimeName, screenPrefab);
    }

    private Transform CreateRuntimeScreen(string runtimeName, GameObject screenPrefab)
    {
        GameObject screenObj;
        if (screenPrefab != null)
        {
            screenObj = Instantiate(screenPrefab, transform, false);
            screenObj.name = runtimeName;
        }
        else
        {
            screenObj = GameObject.CreatePrimitive(PrimitiveType.Quad);
            screenObj.name = runtimeName;
            screenObj.transform.SetParent(transform, false);
        }

        return screenObj != null ? screenObj.transform : null;
    }

    private Renderer EnsureScreenRenderer(Transform screen)
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
        }

        var renderer = screen.GetComponent<MeshRenderer>();
        if (renderer == null)
        {
            renderer = screen.gameObject.AddComponent<MeshRenderer>();
        }

        renderer.enabled = true;
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
            Destroy(collider);
        }

        if (meshCollider == null)
        {
            meshCollider = screen.gameObject.AddComponent<MeshCollider>();
        }

        if (meshCollider.sharedMesh != meshFilter.sharedMesh)
        {
            meshCollider.sharedMesh = meshFilter.sharedMesh;
        }

        meshCollider.convex = false;
        meshCollider.isTrigger = false;
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
            renderer.material = mat;
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
        renderer.material = uniqueMat;
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

        if (leftMat != null && leftMat.HasProperty(leftTexProp))
        {
            leftMat.SetTexture(leftTexProp, player.texture);
        }

        if (rightMat != null && rightMat.HasProperty(rightTexProp))
        {
            rightMat.SetTexture(rightTexProp, player.texture);
        }
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
        }

        // Keep ISDK ray interaction surfaces aligned with the current screen mesh size.
        SyncRayCanvasInteractionSurfaceToScreen(leftScreen);
        SyncRayCanvasInteractionSurfaceToScreen(rightScreen);

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
        float targetWidth = Mathf.Abs(meshSizeLocal.x);
        float targetHeight = Mathf.Abs(meshSizeLocal.y);
        if (targetWidth <= 0.000001f || targetHeight <= 0.000001f)
        {
            return;
        }

        if (screen is RectTransform screenRect)
        {
            screenRect.sizeDelta = new Vector2(targetWidth, targetHeight);
        }

        Transform interactionRoot = FindDeepChildByName(screen, "ISDK_RayCanvasInteraction");
        if (interactionRoot == null)
        {
            return;
        }

        Transform surface = ResolveInteractionSurfaceTransform(interactionRoot, "_surface");
        Transform selectSurface = ResolveInteractionSurfaceTransform(interactionRoot, "_selectSurface");
        if (surface == null)
        {
            surface = FindDeepChildByName(interactionRoot, "Surface");
        }

        SyncSurfaceRectAndClipperSize(surface, targetWidth, targetHeight);
        if (selectSurface != null && selectSurface != surface)
        {
            SyncSurfaceRectAndClipperSize(selectSurface, targetWidth, targetHeight);
        }
    }

    private static Transform ResolveInteractionSurfaceTransform(Transform interactionRoot, string fieldName)
    {
        if (interactionRoot == null || string.IsNullOrEmpty(fieldName))
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Component[] components = interactionRoot.GetComponents<Component>();
        for (int i = 0; i < components.Length; i++)
        {
            Component component = components[i];
            if (component == null)
            {
                continue;
            }

            FieldInfo field = component.GetType().GetField(fieldName, flags);
            if (field == null)
            {
                continue;
            }

            object value = field.GetValue(component);
            if (value is Transform tr)
            {
                return tr;
            }

            if (value is Component comp)
            {
                return comp.transform;
            }

            if (value is GameObject go)
            {
                return go.transform;
            }
        }

        return null;
    }

    private static void SyncSurfaceRectAndClipperSize(Transform surface, float width, float height)
    {
        if (surface == null)
        {
            return;
        }

        if (surface is RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition3D = Vector3.zero;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Component[] components = surface.GetComponentsInChildren<Component>(true);
        for (int i = 0; i < components.Length; i++)
        {
            Component component = components[i];
            if (component == null)
            {
                continue;
            }

            FieldInfo sizeField = component.GetType().GetField("_size", flags);
            if (sizeField == null || sizeField.FieldType != typeof(Vector3))
            {
                continue;
            }

            Vector3 size = (Vector3)sizeField.GetValue(component);
            size.x = width;
            size.y = height;
            if (size.z <= 0f)
            {
                size.z = 0.01f;
            }
            sizeField.SetValue(component, size);
        }
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
            screen.Rotate(0f, 180f, 0f, Space.Self);
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
            float min = Mathf.Min(runtimeFovxMinDeg, runtimeFovxMaxDeg);
            float max = Mathf.Max(runtimeFovxMinDeg, runtimeFovxMaxDeg);
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
        fx = 0f;
        fy = 0f;
        cxPixels = 0f;
        cyPixels = 0f;
        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return false;
        }

        float w = manifest.eye_w;
        float h = manifest.eye_h;
        float cxNorm = 0.5f;
        float cyNorm = 0.5f;
        if (manifest.cx > 0f)
        {
            cxNorm = manifest.cx > 1f ? manifest.cx / w : manifest.cx;
        }
        if (manifest.cy > 0f)
        {
            cyNorm = manifest.cy > 1f ? manifest.cy / h : manifest.cy;
        }
        cxPixels = Mathf.Clamp01(cxNorm) * w;
        cyPixels = Mathf.Clamp01(cyNorm) * h;

        if (manifest.fx_norm > 0f && manifest.fy_norm > 0f)
        {
            fx = manifest.fx_norm;
            fy = manifest.fy_norm;
            return true;
        }

        if (!TryGetFovxDeg(out float fovxDeg))
        {
            return false;
        }

        float fovxRad = fovxDeg * Mathf.Deg2Rad;
        fx = 1f / Mathf.Tan(fovxRad * 0.5f);
        if (manifest.fovy_deg > 0f || manifest.fovy > 0f)
        {
            float fovyDeg = manifest.fovy_deg > 0f ? manifest.fovy_deg : manifest.fovy;
            float fovyRad = fovyDeg * Mathf.Deg2Rad;
            fy = 1f / Mathf.Tan(fovyRad * 0.5f);
        }
        else
        {
            fy = fx * (manifest.eye_w / (float)manifest.eye_h);
        }
        return fx > 0f && fy > 0f;
    }

    private Vector3 ReconstructCamLocalFromEyePixel(float uEye, float vEye, float zMeters, float fx, float fy, int eyeW, int eyeH)
    {
        float cxNorm = 0.5f;
        float cyNorm = 0.5f;
        if (manifest != null && manifest.eye_w > 0 && manifest.eye_h > 0)
        {
            if (manifest.cx > 0f)
            {
                cxNorm = manifest.cx > 1f ? manifest.cx / manifest.eye_w : manifest.cx;
            }
            if (manifest.cy > 0f)
            {
                cyNorm = manifest.cy > 1f ? manifest.cy / manifest.eye_h : manifest.cy;
            }
        }
        float xNdc = ((uEye / (float)eyeW) - cxNorm) * 2f;
        float yNdc = (cyNorm - (vEye / (float)eyeH)) * 2f;
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
        if (legacyVirtualOrigin)
        {
            camOrigin = screen.position + screenFront * screenDistanceMeters;
            return true;
        }

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

        Vector3 camLocal = ReconstructCamLocalFromEyePixel(uEye, vEye, zMeters, fx, fy, manifest.eye_w, manifest.eye_h);
        return camOrigin + (camRotation * camLocal);
    }
}
