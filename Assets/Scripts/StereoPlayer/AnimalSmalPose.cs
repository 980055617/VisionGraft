using UnityEngine;

public struct AnimalSmalPose
{
    public bool hasGlobalOrient;
    public Quaternion globalOrient;
    public Quaternion[] bodyPose; // length 34: SMAL joints 1-34 mapped to indices 0-33
    public float[] betas;
    public bool hasTransl;
    public Vector3 transl;
    public Quaternion camRotation;
}
