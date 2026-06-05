using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class RuntimeSelectableWriterTests
{
    [Test]
    public void ApplyInteractableSetsSelectableState()
    {
        GameObject obj = new GameObject("Button");
        try
        {
            Button button = obj.AddComponent<Button>();

            RuntimeSelectableWriter.ApplyInteractable(button, false);

            Assert.That(button.interactable, Is.False);
        }
        finally
        {
            Object.DestroyImmediate(obj);
        }
    }
}
