using UnityEngine;

public struct AnimalPoseWorldData
{
    public int jointCount;
    public Vector3[] jointsWorld;
    public Vector3[] jointsCam;
    public byte[] jointVis;
    public bool hasAnimalControl;
    public AnimalControlWorldData animalControl;
    public Vector3 camOrigin;
    public Vector3 rootWorld;
}
