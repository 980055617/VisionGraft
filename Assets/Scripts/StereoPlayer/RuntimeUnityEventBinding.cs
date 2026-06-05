using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

public static class RuntimeUnityEventBinding
{
    public static void Bind(Button button, UnityAction action)
    {
        if (button == null)
        {
            return;
        }

        button.onClick.RemoveListener(action);
        button.onClick.AddListener(action);
    }

    public static void Unbind(Button button, UnityAction action)
    {
        if (button == null)
        {
            return;
        }

        button.onClick.RemoveListener(action);
    }

    public static void Bind(Slider slider, UnityAction<float> action)
    {
        if (slider == null)
        {
            return;
        }

        slider.onValueChanged.RemoveListener(action);
        slider.onValueChanged.AddListener(action);
    }

    public static void Unbind(Slider slider, UnityAction<float> action)
    {
        if (slider == null)
        {
            return;
        }

        slider.onValueChanged.RemoveListener(action);
    }

    public static void ClearButtonListenersInChildren(GameObject root)
    {
        if (root == null)
        {
            return;
        }

        Button[] buttons = root.GetComponentsInChildren<Button>(true);
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].onClick.RemoveAllListeners();
        }
    }
}
