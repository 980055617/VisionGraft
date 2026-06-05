using UnityEngine;

public readonly struct TrackPlacementCommand
{
    public readonly Vector3 position;
    public readonly Quaternion rotation;
    public readonly Vector3 localScale;

    public TrackPlacementCommand(Vector3 position, Quaternion rotation, Vector3 localScale)
    {
        this.position = position;
        this.rotation = rotation;
        this.localScale = localScale;
    }

    public static TrackPlacementCommand PositionOnly(
        Vector3 position,
        Quaternion currentRotation,
        Vector3 currentLocalScale)
    {
        return new TrackPlacementCommand(position, currentRotation, currentLocalScale);
    }

    public static TrackPlacementCommand RotationOnly(
        Vector3 currentPosition,
        Quaternion rotation,
        Vector3 currentLocalScale)
    {
        return new TrackPlacementCommand(currentPosition, rotation, currentLocalScale);
    }

    public static TrackPlacementCommand LocalScaleOnly(
        Vector3 currentPosition,
        Quaternion currentRotation,
        Vector3 localScale)
    {
        return new TrackPlacementCommand(currentPosition, currentRotation, localScale);
    }
}
