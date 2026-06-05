using UnityEngine;
using UnityEngine.UI;

public static class RuntimeUiLookup
{
    public static Button FindButton(GameObject root, string namePartLower)
    {
        if (root == null)
        {
            return null;
        }

        Button[] buttons = root.GetComponentsInChildren<Button>(true);
        for (int i = 0; i < buttons.Length; i++)
        {
            Button button = buttons[i];
            if (MatchesNamePart(button, namePartLower))
            {
                return button;
            }
        }

        return null;
    }

    public static Text GetButtonText(Button button)
    {
        return button == null ? null : button.GetComponentInChildren<Text>(true);
    }

    public static Slider FindSlider(GameObject root, string namePartLower)
    {
        if (root == null)
        {
            return null;
        }

        Slider[] sliders = root.GetComponentsInChildren<Slider>(true);
        for (int i = 0; i < sliders.Length; i++)
        {
            Slider slider = sliders[i];
            if (MatchesNamePart(slider, namePartLower))
            {
                return slider;
            }
        }

        return null;
    }

    public static Text FindText(GameObject root, string namePartLower)
    {
        if (root == null)
        {
            return null;
        }

        Text[] texts = root.GetComponentsInChildren<Text>(true);
        for (int i = 0; i < texts.Length; i++)
        {
            Text text = texts[i];
            if (MatchesNamePart(text, namePartLower))
            {
                return text;
            }
        }

        return null;
    }

    private static bool MatchesNamePart(Component component, string namePartLower)
    {
        if (component == null || string.IsNullOrEmpty(namePartLower))
        {
            return false;
        }

        return component.name.ToLowerInvariant().Contains(namePartLower);
    }
}
