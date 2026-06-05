using UnityEngine;

public struct AnimalControlPose
{
    public ushort kpCount;
    public Vector3[] jointsCamAbs;
    public byte[] jointsVis;
    public bool hasWithersCamAbs;
    public Vector3 withersCamAbs;
    public bool hasHeadRootCamAbs;
    public Vector3 headRootCamAbs;
    public bool hasHeadTipCamAbs;
    public Vector3 headTipCamAbs;
    public Vector3 rootCamAbs;
    public bool hasTailBaseCamAbs;
    public Vector3 tailBaseCamAbs;
    public bool hasTailTipCamAbs;
    public Vector3 tailTipCamAbs;
    public bool hasForwardHintCamAbs;
    public Vector3 forwardHintCamAbs;
    public bool hasUpHintCamAbs;
    public Vector3 upHintCamAbs;
    public Vector3[] frontLeftLegChainCamAbs;
    public Vector3[] frontRightLegChainCamAbs;
    public Vector3[] rearLeftLegChainCamAbs;
    public Vector3[] rearRightLegChainCamAbs;
    public Vector3[] headChainCamAbs;
    public Vector3[] tailChainCamAbs;
}
