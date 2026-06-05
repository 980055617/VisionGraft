using System;

public static class RuntimeTrackedDeviceGraphicRaycasterResolver
{
    private static readonly string[] TypeNames =
    {
        "UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster, Unity.XR.Interaction.Toolkit",
        "UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster, Unity.XR.Interaction.Toolkit.Runtime"
    };

    private static Type cachedType;
    private static bool resolved;

    public static Type Resolve()
    {
        if (resolved)
        {
            return cachedType;
        }

        resolved = true;
        cachedType = ResolveFirstAvailable(TypeNames);
        return cachedType;
    }

    public static Type ResolveFirstAvailable(params string[] assemblyQualifiedTypeNames)
    {
        if (assemblyQualifiedTypeNames == null)
        {
            return null;
        }

        for (int i = 0; i < assemblyQualifiedTypeNames.Length; i++)
        {
            string typeName = assemblyQualifiedTypeNames[i];
            if (string.IsNullOrEmpty(typeName))
            {
                continue;
            }

            Type type = Type.GetType(typeName);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }
}
