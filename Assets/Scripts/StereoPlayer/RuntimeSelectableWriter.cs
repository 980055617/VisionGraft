using UnityEngine.UI;

public static class RuntimeSelectableWriter
{
    public static void ApplyInteractable(Selectable selectable, bool interactable)
    {
        if (selectable == null)
        {
            return;
        }

        selectable.interactable = interactable;
    }
}
