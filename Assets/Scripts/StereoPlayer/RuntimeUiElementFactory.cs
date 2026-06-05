using UnityEngine;
using UnityEngine.UI;

public static class RuntimeUiElementFactory
{
    public static GameObject CreateChild(string name, Transform parent)
    {
        GameObject child = new GameObject(name);
        child.transform.SetParent(parent, false);
        return child;
    }

    public static RectTransform CreateRectChild(string name, Transform parent, out GameObject child)
    {
        child = CreateChild(name, parent);
        return child.AddComponent<RectTransform>();
    }

    public static Image AddImage(GameObject target)
    {
        return target != null ? target.AddComponent<Image>() : null;
    }

    public static Button AddButton(GameObject target)
    {
        return target != null ? target.AddComponent<Button>() : null;
    }

    public static Text AddText(GameObject target)
    {
        return target != null ? target.AddComponent<Text>() : null;
    }

    public static Slider AddSlider(GameObject target)
    {
        return target != null ? target.AddComponent<Slider>() : null;
    }
}
