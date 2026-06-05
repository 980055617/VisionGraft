using NUnit.Framework;
using UnityEngine;

public class RuntimeCanvasWriterTests
{
    [Test]
    public void ApplyWorldSpaceCameraSetsRenderModeAndCamera()
    {
        GameObject canvasObject = new GameObject("Canvas", typeof(Canvas));
        GameObject cameraObject = new GameObject("Camera", typeof(Camera));
        try
        {
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            Camera camera = cameraObject.GetComponent<Camera>();

            RuntimeCanvasWriter.ApplyWorldSpaceCamera(canvas, camera);

            Assert.That(canvas.renderMode, Is.EqualTo(RenderMode.WorldSpace));
            Assert.That(canvas.worldCamera, Is.EqualTo(camera));
        }
        finally
        {
            Object.DestroyImmediate(canvasObject);
            Object.DestroyImmediate(cameraObject);
        }
    }

    [Test]
    public void ApplyWorldCameraIfMissingKeepsExistingCamera()
    {
        GameObject canvasObject = new GameObject("Canvas", typeof(Canvas));
        GameObject existingCameraObject = new GameObject("ExistingCamera", typeof(Camera));
        GameObject candidateCameraObject = new GameObject("CandidateCamera", typeof(Camera));
        try
        {
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            Camera existing = existingCameraObject.GetComponent<Camera>();
            Camera candidate = candidateCameraObject.GetComponent<Camera>();
            canvas.worldCamera = existing;

            RuntimeCanvasWriter.ApplyWorldCameraIfMissing(canvas, candidate);

            Assert.That(canvas.worldCamera, Is.EqualTo(existing));
        }
        finally
        {
            Object.DestroyImmediate(canvasObject);
            Object.DestroyImmediate(existingCameraObject);
            Object.DestroyImmediate(candidateCameraObject);
        }
    }

    [Test]
    public void ApplyWorldCameraReplacesExistingCamera()
    {
        GameObject canvasObject = new GameObject("Canvas", typeof(Canvas));
        GameObject existingCameraObject = new GameObject("ExistingCamera", typeof(Camera));
        GameObject replacementCameraObject = new GameObject("ReplacementCamera", typeof(Camera));
        try
        {
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.worldCamera = existingCameraObject.GetComponent<Camera>();
            Camera replacement = replacementCameraObject.GetComponent<Camera>();

            RuntimeCanvasWriter.ApplyWorldCamera(canvas, replacement);

            Assert.That(canvas.worldCamera, Is.EqualTo(replacement));
        }
        finally
        {
            Object.DestroyImmediate(canvasObject);
            Object.DestroyImmediate(existingCameraObject);
            Object.DestroyImmediate(replacementCameraObject);
        }
    }
}
