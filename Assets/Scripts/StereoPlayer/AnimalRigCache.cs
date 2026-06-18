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
    public Transform leftRearToe;
    public Transform rightRearUpper;
    public Transform rightRearLower;
    public Transform rightRearPaw;
    public Transform rightRearToe;
    public Transform tailMid;
    public Transform tailTip;
    public Vector3 modelForwardLocal;
    public Vector3 modelUpLocal;
    // Plain bind-time world direction from spine to neck (neck.position - spine.position,
    // captured once - no aim-child/pivot heuristics). Rotating this by a candidate root
    // delta predicts where the neck should end up; comparing that prediction against a
    // real per-frame keypoint reference is how the root yaw fix gets decided per model
    // (see AnimalSmalFkApplier, ADR-0002).
    public Vector3 spineToNeckBindDirWorld;
    public readonly Dictionary<Transform, Vector3> bindDirLocal = new Dictionary<Transform, Vector3>();
    public readonly Dictionary<Transform, Quaternion> bindRotLocal = new Dictionary<Transform, Quaternion>();
    public readonly Dictionary<Transform, Quaternion> bindRotWorld = new Dictionary<Transform, Quaternion>();
    public readonly Dictionary<Transform, Transform> aimChildByBone = new Dictionary<Transform, Transform>();
    public bool ready;
}
