using System.Reflection;
using UnityEngine;

public static class InteractionSdkRayCanvasAdapter
{
    private const string InteractionRootName = "ISDK_RayCanvasInteraction";
    private const string FallbackSurfaceName = "Surface";
    private const string SurfaceFieldName = "_surface";
    private const string SelectSurfaceFieldName = "_selectSurface";
    private const string SizeFieldName = "_size";
    private const float MinimumSize = 0.000001f;
    private const float DefaultDepth = 0.01f;
    private const BindingFlags InstanceFieldFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static void SyncSurfaceToScreen(Transform screen, Vector3 meshSizeLocal)
    {
        if (screen == null)
        {
            return;
        }

        float targetWidth = Mathf.Abs(meshSizeLocal.x);
        float targetHeight = Mathf.Abs(meshSizeLocal.y);
        if (targetWidth <= MinimumSize || targetHeight <= MinimumSize)
        {
            return;
        }

        if (screen is RectTransform screenRect)
        {
            RuntimeUiTransformWriter.ApplySizeDelta(screenRect, new Vector2(targetWidth, targetHeight));
        }

        Transform interactionRoot = FindDeepChildByName(screen, InteractionRootName);
        if (interactionRoot == null)
        {
            return;
        }

        Transform surface = ResolveInteractionSurfaceTransform(interactionRoot, SurfaceFieldName);
        Transform selectSurface = ResolveInteractionSurfaceTransform(interactionRoot, SelectSurfaceFieldName);
        if (surface == null)
        {
            surface = FindDeepChildByName(interactionRoot, FallbackSurfaceName);
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

        Component[] components = interactionRoot.GetComponents<Component>();
        for (int i = 0; i < components.Length; i++)
        {
            Component component = components[i];
            if (component == null)
            {
                continue;
            }

            FieldInfo field = component.GetType().GetField(fieldName, InstanceFieldFlags);
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
            RuntimeUiTransformWriter.ApplyFullStretchSurfaceRect(rect);
        }

        Component[] components = surface.GetComponentsInChildren<Component>(true);
        for (int i = 0; i < components.Length; i++)
        {
            Component component = components[i];
            if (component == null)
            {
                continue;
            }

            FieldInfo sizeField = component.GetType().GetField(SizeFieldName, InstanceFieldFlags);
            if (sizeField == null || sizeField.FieldType != typeof(Vector3))
            {
                continue;
            }

            Vector3 size = (Vector3)sizeField.GetValue(component);
            size.x = width;
            size.y = height;
            if (size.z <= 0f)
            {
                size.z = DefaultDepth;
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
}
