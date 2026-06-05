using UnityEngine;

public static class AnimalAimChildSelector
{
    public static Transform Select(Transform registeredAimChild, Transform fallbackFirstChild)
    {
        return registeredAimChild != null ? registeredAimChild : fallbackFirstChild;
    }
}
