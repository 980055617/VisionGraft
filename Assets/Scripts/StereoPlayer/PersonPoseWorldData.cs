using UnityEngine;

internal struct PersonPoseWorldData
{
    public int jointCount;
    public Vector3[] jointsWorld;
    public Vector3[] jointsCam;
    public byte[] jointVis;
    public Vector3 camOrigin;
    public Vector3 rootWorld;
}
