using UnityEngine;

public struct AnimalControlWorldData
{
    public bool hasRoot;
    public Vector3 rootWorld;
    public bool hasWithers;
    public Vector3 withersWorld;
    public bool hasHeadRoot;
    public Vector3 headRootWorld;
    public bool hasHeadTip;
    public Vector3 headTipWorld;
    public bool hasTailBase;
    public Vector3 tailBaseWorld;
    public bool hasTailTip;
    public Vector3 tailTipWorld;
    public bool hasForwardHint;
    public Vector3 forwardHintWorld;
    public bool hasUpHint;
    public Vector3 upHintWorld;
    public Vector3[] frontLeftLegWorld;
    public Vector3[] frontRightLegWorld;
    public Vector3[] rearLeftLegWorld;
    public Vector3[] rearRightLegWorld;
    public Vector3[] headWorld;
    public Vector3[] tailWorld;
}
