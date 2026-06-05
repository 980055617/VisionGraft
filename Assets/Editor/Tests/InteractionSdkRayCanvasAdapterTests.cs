using NUnit.Framework;
using UnityEngine;

public class InteractionSdkRayCanvasAdapterTests
{
    [Test]
    public void SyncSurfaceToScreenKeepsExistingRayCanvasSurfaceSizing()
    {
        GameObject screen = new GameObject("Screen", typeof(RectTransform));
        GameObject interaction = new GameObject("ISDK_RayCanvasInteraction");
        GameObject surface = new GameObject("Surface", typeof(RectTransform), typeof(SizeHolder));
        GameObject selectSurface = new GameObject("SelectSurface", typeof(RectTransform), typeof(SizeHolder));
        try
        {
            interaction.transform.SetParent(screen.transform, false);
            surface.transform.SetParent(interaction.transform, false);
            selectSurface.transform.SetParent(interaction.transform, false);
            interaction.AddComponent<InteractionSurfaceHolder>().Set(surface.transform, selectSurface.transform);

            InteractionSdkRayCanvasAdapter.SyncSurfaceToScreen(screen.transform, new Vector3(2f, 3f, 0f));

            RectTransform screenRect = (RectTransform)screen.transform;
            RectTransform surfaceRect = (RectTransform)surface.transform;
            RectTransform selectSurfaceRect = (RectTransform)selectSurface.transform;

            Assert.That(screenRect.sizeDelta, Is.EqualTo(new Vector2(2f, 3f)));
            AssertRectFillsParent(surfaceRect);
            AssertRectFillsParent(selectSurfaceRect);
            Assert.That(surface.GetComponent<SizeHolder>().Size, Is.EqualTo(new Vector3(2f, 3f, 0.01f)));
            Assert.That(selectSurface.GetComponent<SizeHolder>().Size, Is.EqualTo(new Vector3(2f, 3f, 0.01f)));
        }
        finally
        {
            Object.DestroyImmediate(screen);
        }
    }

    private static void AssertRectFillsParent(RectTransform rect)
    {
        Assert.That(rect.anchorMin, Is.EqualTo(Vector2.zero));
        Assert.That(rect.anchorMax, Is.EqualTo(Vector2.one));
        Assert.That(rect.pivot, Is.EqualTo(new Vector2(0.5f, 0.5f)));
        Assert.That(rect.anchoredPosition3D, Is.EqualTo(Vector3.zero));
        Assert.That(rect.offsetMin, Is.EqualTo(Vector2.zero));
        Assert.That(rect.offsetMax, Is.EqualTo(Vector2.zero));
    }

    private sealed class InteractionSurfaceHolder : MonoBehaviour
    {
        private Transform _surface;
        private Component _selectSurface;

        public void Set(Transform surface, Component selectSurface)
        {
            _surface = surface;
            _selectSurface = selectSurface;
        }
    }

    private sealed class SizeHolder : MonoBehaviour
    {
        private Vector3 _size;

        public Vector3 Size => _size;
    }
}
