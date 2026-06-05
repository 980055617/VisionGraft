using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class RuntimeUiElementFactoryTests
{
    [Test]
    public void CreateChildNamesObjectAndParentsWithoutPreservingWorldPose()
    {
        GameObject parent = new GameObject("Parent");
        try
        {
            parent.transform.position = new Vector3(10f, 0f, 0f);

            GameObject child = RuntimeUiElementFactory.CreateChild("Child", parent.transform);
            try
            {
                Assert.That(child, Is.Not.Null);
                Assert.That(child.name, Is.EqualTo("Child"));
                Assert.That(child.transform.parent, Is.EqualTo(parent.transform));
                AssertVector(child.transform.localPosition, Vector3.zero);
                Assert.That(Quaternion.Angle(child.transform.localRotation, Quaternion.identity), Is.LessThan(0.001f));
                AssertVector(child.transform.localScale, Vector3.one);
            }
            finally
            {
                Object.DestroyImmediate(child);
            }
        }
        finally
        {
            Object.DestroyImmediate(parent);
        }
    }

    [Test]
    public void CreateRectChildAddsRectTransform()
    {
        GameObject parent = new GameObject("Parent");
        try
        {
            RectTransform rect = RuntimeUiElementFactory.CreateRectChild("RectChild", parent.transform, out GameObject child);
            try
            {
                Assert.That(child, Is.Not.Null);
                Assert.That(rect, Is.Not.Null);
                Assert.That(rect.gameObject, Is.EqualTo(child));
                Assert.That(child.transform.parent, Is.EqualTo(parent.transform));
            }
            finally
            {
                Object.DestroyImmediate(child);
            }
        }
        finally
        {
            Object.DestroyImmediate(parent);
        }
    }

    [Test]
    public void AddImageAddsImageToObject()
    {
        GameObject obj = new GameObject("Image");
        try
        {
            Image image = RuntimeUiElementFactory.AddImage(obj);

            Assert.That(image, Is.Not.Null);
            Assert.That(image.gameObject, Is.EqualTo(obj));
        }
        finally
        {
            Object.DestroyImmediate(obj);
        }
    }

    [Test]
    public void AddButtonAddsButtonToObject()
    {
        GameObject obj = new GameObject("Button");
        try
        {
            Button button = RuntimeUiElementFactory.AddButton(obj);

            Assert.That(button, Is.Not.Null);
            Assert.That(button.gameObject, Is.EqualTo(obj));
        }
        finally
        {
            Object.DestroyImmediate(obj);
        }
    }

    [Test]
    public void AddTextAddsTextToObject()
    {
        GameObject obj = new GameObject("Text");
        try
        {
            Text text = RuntimeUiElementFactory.AddText(obj);

            Assert.That(text, Is.Not.Null);
            Assert.That(text.gameObject, Is.EqualTo(obj));
        }
        finally
        {
            Object.DestroyImmediate(obj);
        }
    }

    [Test]
    public void AddSliderAddsSliderToObject()
    {
        GameObject obj = new GameObject("Slider");
        try
        {
            Slider slider = RuntimeUiElementFactory.AddSlider(obj);

            Assert.That(slider, Is.Not.Null);
            Assert.That(slider.gameObject, Is.EqualTo(obj));
        }
        finally
        {
            Object.DestroyImmediate(obj);
        }
    }

    private static void AssertVector(Vector3 actual, Vector3 expected)
    {
        Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0001f));
        Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0001f));
        Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.0001f));
    }
}
