using UnityEngine.Events;
using UnityEngine.UI;

public partial class StreamingStereoVideoPlayer
{
    private static void BindRuntimeButton(Button button, UnityAction action)
    {
        if (button == null)
        {
            return;
        }

        RuntimeUnityEventBinding.Bind(button, action);
    }


    private static void BindRuntimeSlider(Slider slider, UnityAction<float> action)
    {
        if (slider == null)
        {
            return;
        }

        RuntimeUnityEventBinding.Bind(slider, action);
    }


    private static void UnbindRuntimeButton(Button button, UnityAction action)
    {
        RuntimeUnityEventBinding.Unbind(button, action);
    }


    private static void UnbindRuntimeSlider(Slider slider, UnityAction<float> action)
    {
        RuntimeUnityEventBinding.Unbind(slider, action);
    }
}
