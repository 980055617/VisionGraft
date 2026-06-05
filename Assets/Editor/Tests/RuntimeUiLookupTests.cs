using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class RuntimeUiLookupTests
{
    [Test]
    public void FindsInactiveButtonByCaseInsensitiveNamePart()
    {
        GameObject root = new GameObject("Root");
        GameObject child = new GameObject("PauseToggleButton", typeof(RectTransform), typeof(Button));
        try
        {
            child.transform.SetParent(root.transform, false);
            child.SetActive(false);

            Button button = RuntimeUiLookup.FindButton(root, "pause");

            Assert.That(button, Is.SameAs(child.GetComponent<Button>()));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void FindsSliderAndTextByCaseInsensitiveNamePart()
    {
        GameObject root = new GameObject("Root");
        GameObject sliderObj = new GameObject("ProgressSlider", typeof(RectTransform), typeof(Slider));
        GameObject textObj = new GameObject("ProgressText", typeof(RectTransform), typeof(Text));
        try
        {
            sliderObj.transform.SetParent(root.transform, false);
            textObj.transform.SetParent(root.transform, false);

            Assert.That(RuntimeUiLookup.FindSlider(root, "progressslider"), Is.SameAs(sliderObj.GetComponent<Slider>()));
            Assert.That(RuntimeUiLookup.FindText(root, "progresstext"), Is.SameAs(textObj.GetComponent<Text>()));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void GetButtonTextReturnsDescendantText()
    {
        GameObject buttonObj = new GameObject("Button", typeof(RectTransform), typeof(Button));
        GameObject textObj = new GameObject("Label", typeof(RectTransform), typeof(Text));
        try
        {
            textObj.transform.SetParent(buttonObj.transform, false);

            Assert.That(RuntimeUiLookup.GetButtonText(buttonObj.GetComponent<Button>()), Is.SameAs(textObj.GetComponent<Text>()));
        }
        finally
        {
            Object.DestroyImmediate(buttonObj);
        }
    }
}
