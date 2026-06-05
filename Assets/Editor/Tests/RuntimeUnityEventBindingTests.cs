using NUnit.Framework;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

public class RuntimeUnityEventBindingTests
{
    [Test]
    public void BindButtonReplacesExistingSameListenerAndUnbindRemovesIt()
    {
        GameObject go = new GameObject("Button", typeof(RectTransform), typeof(Button));
        try
        {
            Button button = go.GetComponent<Button>();
            int calls = 0;
            UnityAction action = () => calls++;

            RuntimeUnityEventBinding.Bind(button, action);
            RuntimeUnityEventBinding.Bind(button, action);
            button.onClick.Invoke();
            RuntimeUnityEventBinding.Unbind(button, action);
            button.onClick.Invoke();

            Assert.That(calls, Is.EqualTo(1));
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void BindSliderReplacesExistingSameListenerAndUnbindRemovesIt()
    {
        GameObject go = new GameObject("Slider", typeof(RectTransform), typeof(Slider));
        try
        {
            Slider slider = go.GetComponent<Slider>();
            int calls = 0;
            UnityAction<float> action = _ => calls++;

            RuntimeUnityEventBinding.Bind(slider, action);
            RuntimeUnityEventBinding.Bind(slider, action);
            slider.onValueChanged.Invoke(1f);
            RuntimeUnityEventBinding.Unbind(slider, action);
            slider.onValueChanged.Invoke(2f);

            Assert.That(calls, Is.EqualTo(1));
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void ClearButtonListenersInChildrenOnlyClearsButtonsUnderRoot()
    {
        GameObject root = new GameObject("Root");
        GameObject child = new GameObject("ChildButton", typeof(RectTransform), typeof(Button));
        GameObject outside = new GameObject("OutsideButton", typeof(RectTransform), typeof(Button));
        try
        {
            child.transform.SetParent(root.transform, false);
            Button childButton = child.GetComponent<Button>();
            Button outsideButton = outside.GetComponent<Button>();
            int childCalls = 0;
            int outsideCalls = 0;
            childButton.onClick.AddListener(() => childCalls++);
            outsideButton.onClick.AddListener(() => outsideCalls++);

            RuntimeUnityEventBinding.ClearButtonListenersInChildren(root);
            childButton.onClick.Invoke();
            outsideButton.onClick.Invoke();

            Assert.That(childCalls, Is.EqualTo(0));
            Assert.That(outsideCalls, Is.EqualTo(1));
        }
        finally
        {
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(outside);
        }
    }
}
