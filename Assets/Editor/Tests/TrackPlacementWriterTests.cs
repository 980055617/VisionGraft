using NUnit.Framework;
using UnityEngine;

public class TrackPlacementWriterTests
{
    [Test]
    public void ApplyWritesRootPoseAndScaleThroughOneCommand()
    {
        GameObject root = new GameObject("TrackRoot");
        try
        {
            TrackPlacementCommand command = new TrackPlacementCommand(
                new Vector3(1.25f, 2.5f, -0.75f),
                Quaternion.Euler(0f, 35f, 0f),
                new Vector3(1.5f, 1.5f, 1.5f));

            TrackPlacementWriter.Apply(root.transform, command);

            AssertVector(root.transform.position, command.position);
            Assert.That(Quaternion.Angle(root.transform.rotation, command.rotation), Is.LessThan(0.001f));
            AssertVector(root.transform.localScale, command.localScale);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ApplyPositionKeepsExistingRootRotationAndScale()
    {
        GameObject root = new GameObject("TrackRoot");
        try
        {
            root.transform.rotation = Quaternion.Euler(0f, 70f, 0f);
            root.transform.localScale = new Vector3(0.75f, 1.25f, 1.5f);

            Vector3 position = new Vector3(-1f, 0.5f, 3f);

            TrackPlacementWriter.ApplyPosition(root.transform, position);

            AssertVector(root.transform.position, position);
            Assert.That(Quaternion.Angle(root.transform.rotation, Quaternion.Euler(0f, 70f, 0f)), Is.LessThan(0.001f));
            AssertVector(root.transform.localScale, new Vector3(0.75f, 1.25f, 1.5f));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void PositionOnlyCommandKeepsCurrentRotationAndScale()
    {
        Vector3 position = new Vector3(2f, 3f, 4f);
        Quaternion currentRotation = Quaternion.Euler(0f, 25f, 0f);
        Vector3 currentScale = new Vector3(1.2f, 1.3f, 1.4f);

        TrackPlacementCommand command = TrackPlacementCommand.PositionOnly(
            position,
            currentRotation,
            currentScale);

        AssertVector(command.position, position);
        Assert.That(Quaternion.Angle(command.rotation, currentRotation), Is.LessThan(0.001f));
        AssertVector(command.localScale, currentScale);
    }

    [Test]
    public void RotationOnlyCommandKeepsCurrentPositionAndScale()
    {
        Vector3 currentPosition = new Vector3(-1f, 0.5f, 2f);
        Quaternion rotation = Quaternion.Euler(0f, 80f, 0f);
        Vector3 currentScale = new Vector3(0.8f, 1.1f, 1.3f);

        TrackPlacementCommand command = TrackPlacementCommand.RotationOnly(
            currentPosition,
            rotation,
            currentScale);

        AssertVector(command.position, currentPosition);
        Assert.That(Quaternion.Angle(command.rotation, rotation), Is.LessThan(0.001f));
        AssertVector(command.localScale, currentScale);
    }

    [Test]
    public void LocalScaleOnlyCommandKeepsCurrentPositionAndRotation()
    {
        Vector3 currentPosition = new Vector3(0.25f, -0.5f, 1.5f);
        Quaternion currentRotation = Quaternion.Euler(0f, 15f, 0f);
        Vector3 localScale = new Vector3(2f, 2.5f, 3f);

        TrackPlacementCommand command = TrackPlacementCommand.LocalScaleOnly(
            currentPosition,
            currentRotation,
            localScale);

        AssertVector(command.position, currentPosition);
        Assert.That(Quaternion.Angle(command.rotation, currentRotation), Is.LessThan(0.001f));
        AssertVector(command.localScale, localScale);
    }

    [Test]
    public void ApplyLocalScaleKeepsExistingRootPositionAndRotation()
    {
        GameObject root = new GameObject("TrackRoot");
        try
        {
            root.transform.position = new Vector3(2f, -1f, 0.25f);
            root.transform.rotation = Quaternion.Euler(0f, 15f, 0f);

            Vector3 localScale = new Vector3(2f, 2.5f, 3f);

            TrackPlacementWriter.ApplyLocalScale(root.transform, localScale);

            AssertVector(root.transform.position, new Vector3(2f, -1f, 0.25f));
            Assert.That(Quaternion.Angle(root.transform.rotation, Quaternion.Euler(0f, 15f, 0f)), Is.LessThan(0.001f));
            AssertVector(root.transform.localScale, localScale);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ApplyRotationKeepsExistingRootPositionAndScale()
    {
        GameObject root = new GameObject("TrackRoot");
        try
        {
            root.transform.position = new Vector3(1f, 2f, 3f);
            root.transform.localScale = new Vector3(1.1f, 1.2f, 1.3f);

            Quaternion rotation = Quaternion.Euler(0f, 110f, 0f);

            TrackPlacementWriter.ApplyRotation(root.transform, rotation);

            AssertVector(root.transform.position, new Vector3(1f, 2f, 3f));
            Assert.That(Quaternion.Angle(root.transform.rotation, rotation), Is.LessThan(0.001f));
            AssertVector(root.transform.localScale, new Vector3(1.1f, 1.2f, 1.3f));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ApplyAnchoredPosePlacesAnchorAtTargetWorldPosition()
    {
        GameObject root = new GameObject("TrackRoot");
        GameObject anchor = new GameObject("Anchor");
        try
        {
            anchor.transform.SetParent(root.transform, false);
            anchor.transform.localPosition = new Vector3(0f, 1f, 0f);

            Vector3 targetAnchorWorld = new Vector3(2f, 3f, 4f);
            Quaternion rotation = Quaternion.identity;
            Vector3 localScale = Vector3.one;

            TrackPlacementWriter.ApplyAnchoredPose(root.transform, targetAnchorWorld, rotation, localScale, anchor.transform);

            AssertVector(anchor.transform.position, targetAnchorWorld);
            AssertVector(root.transform.position, new Vector3(2f, 2f, 4f));
            Assert.That(Quaternion.Angle(root.transform.rotation, rotation), Is.LessThan(0.001f));
            AssertVector(root.transform.localScale, localScale);
        }
        finally
        {
            Object.DestroyImmediate(anchor);
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ApplyLocalScaleWithGroundAlignmentMovesAlongRootUpByScaledBottomOffset()
    {
        GameObject root = new GameObject("TrackRoot");
        try
        {
            root.transform.position = Vector3.zero;
            root.transform.rotation = Quaternion.identity;

            TrackPlacementWriter.ApplyLocalScaleWithGroundAlignment(
                root.transform,
                new Vector3(2f, 3f, 2f),
                true,
                0.5f);

            AssertVector(root.transform.localScale, new Vector3(2f, 3f, 2f));
            AssertVector(root.transform.position, Vector3.up * 1.5f);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ApplyBottomAlignmentCanPreserveHorizontalPosition()
    {
        GameObject root = new GameObject("TrackRoot");
        try
        {
            root.transform.position = new Vector3(1f, 2f, 3f);

            TrackPlacementWriter.ApplyBottomAlignment(
                root.transform,
                new Vector3(10f, 5f, 30f),
                Vector3.up,
                0f,
                true);

            AssertVector(root.transform.position, new Vector3(1f, 5f, 3f));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ApplyCameraSpaceOffsetMovesRootThroughCameraBasis()
    {
        GameObject root = new GameObject("TrackRoot");
        try
        {
            root.transform.position = new Vector3(1f, 2f, 3f);
            Quaternion cameraRotation = Quaternion.Euler(0f, 90f, 0f);

            TrackPlacementWriter.ApplyCameraSpaceOffset(
                root.transform,
                cameraRotation,
                new Vector3(0f, 0.5f, 2f));

            AssertVector(root.transform.position, new Vector3(3f, 2.5f, 3f));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ApplyAnchorPositionMovesRootSoAnchorMatchesTarget()
    {
        GameObject root = new GameObject("TrackRoot");
        GameObject anchor = new GameObject("Hips");
        try
        {
            root.transform.position = new Vector3(1f, 2f, 3f);
            anchor.transform.SetParent(root.transform, false);
            anchor.transform.localPosition = new Vector3(0f, 1.5f, 0f);

            TrackPlacementWriter.ApplyAnchorPosition(
                root.transform,
                anchor.transform,
                new Vector3(4f, 8f, 6f));

            AssertVector(anchor.transform.position, new Vector3(4f, 8f, 6f));
            AssertVector(root.transform.position, new Vector3(4f, 6.5f, 6f));
        }
        finally
        {
            Object.DestroyImmediate(anchor);
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void CalculateAnchorAlignedPositionReturnsRootPositionForTargetAnchor()
    {
        GameObject root = new GameObject("TrackRoot");
        GameObject anchor = new GameObject("PlacementBone");
        try
        {
            root.transform.position = new Vector3(-2f, 1f, 5f);
            anchor.transform.SetParent(root.transform, false);
            anchor.transform.localPosition = new Vector3(0.5f, 0.25f, -1f);

            Vector3 resolved = TrackPlacementWriter.CalculateAnchorAlignedPosition(
                root.transform,
                anchor.transform,
                new Vector3(3f, 4f, 7f));

            AssertVector(resolved, new Vector3(2.5f, 3.75f, 8f));
            AssertVector(root.transform.position, new Vector3(-2f, 1f, 5f));
        }
        finally
        {
            Object.DestroyImmediate(anchor);
            Object.DestroyImmediate(root);
        }
    }

    private static void AssertVector(Vector3 actual, Vector3 expected)
    {
        Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0001f));
        Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0001f));
        Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.0001f));
    }
}
