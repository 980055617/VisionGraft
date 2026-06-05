using System.Collections.Generic;
using UnityEngine;

internal sealed class AnimalRigCache
{
    public Transform root;
    public Transform neck;
    public Transform head;
    public Transform spine;
    public Transform tailBase;
    public Transform leftFrontUpper;
    public Transform leftFrontLower;
    public Transform leftFrontPaw;
    public Transform rightFrontUpper;
    public Transform rightFrontLower;
    public Transform rightFrontPaw;
    public Transform leftRearUpper;
    public Transform leftRearLower;
    public Transform leftRearPaw;
    public Transform rightRearUpper;
    public Transform rightRearLower;
    public Transform rightRearPaw;
    public Vector3 modelForwardLocal;
    public Vector3 modelUpLocal;
    public readonly Dictionary<Transform, Vector3> bindDirLocal = new Dictionary<Transform, Vector3>();
    public readonly Dictionary<Transform, Quaternion> bindRotLocal = new Dictionary<Transform, Quaternion>();
    public readonly Dictionary<Transform, Transform> aimChildByBone = new Dictionary<Transform, Transform>();
    public bool ready;
}
