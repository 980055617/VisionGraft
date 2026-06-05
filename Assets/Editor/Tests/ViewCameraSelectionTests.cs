using NUnit.Framework;
using UnityEngine;

public class ViewCameraSelectionTests
{
    [Test]
    public void SelectPrefersMainCameraOverOtherEnabledCameras()
    {
        GameObject otherGo = new GameObject("OtherCamera", typeof(Camera));
        GameObject mainGo = new GameObject("MainCamera", typeof(Camera));
        try
        {
            mainGo.tag = "MainCamera";

            Camera selected = ViewCameraSelection.Select(new[]
            {
                otherGo.GetComponent<Camera>(),
                mainGo.GetComponent<Camera>()
            });

            Assert.That(selected, Is.EqualTo(mainGo.GetComponent<Camera>()));
        }
        finally
        {
            Object.DestroyImmediate(otherGo);
            Object.DestroyImmediate(mainGo);
        }
    }

    [Test]
    public void IsUsableRejectsDisabledOrInactiveCamera()
    {
        GameObject go = new GameObject("Camera", typeof(Camera));
        try
        {
            Camera camera = go.GetComponent<Camera>();
            camera.enabled = false;
            Assert.That(ViewCameraSelection.IsUsable(camera), Is.False);

            camera.enabled = true;
            go.SetActive(false);
            Assert.That(ViewCameraSelection.IsUsable(camera), Is.False);
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }
}
