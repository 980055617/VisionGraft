using UnityEngine;

public static class AnimalControlPoseMapper
{
    public static AnimalControlWorldData ToWorldData(
        AnimalControlPose controlPose,
        Vector3 camOrigin,
        Quaternion camRotation,
        Vector3 axisSign)
    {
        AnimalControlWorldData world = new AnimalControlWorldData
        {
            hasRoot = true,
            rootWorld = TransformCamPoint(controlPose.rootCamAbs, camOrigin, camRotation, axisSign),
            hasWithers = controlPose.hasWithersCamAbs,
            hasHeadRoot = controlPose.hasHeadRootCamAbs,
            hasHeadTip = controlPose.hasHeadTipCamAbs,
            hasTailBase = controlPose.hasTailBaseCamAbs,
            hasTailTip = controlPose.hasTailTipCamAbs,
            hasForwardHint = controlPose.hasForwardHintCamAbs,
            hasUpHint = controlPose.hasUpHintCamAbs
        };

        if (controlPose.hasWithersCamAbs)
        {
            world.withersWorld = TransformCamPoint(controlPose.withersCamAbs, camOrigin, camRotation, axisSign);
        }
        if (controlPose.hasHeadRootCamAbs)
        {
            world.headRootWorld = TransformCamPoint(controlPose.headRootCamAbs, camOrigin, camRotation, axisSign);
        }
        if (controlPose.hasHeadTipCamAbs)
        {
            world.headTipWorld = TransformCamPoint(controlPose.headTipCamAbs, camOrigin, camRotation, axisSign);
        }
        if (controlPose.hasTailBaseCamAbs)
        {
            world.tailBaseWorld = TransformCamPoint(controlPose.tailBaseCamAbs, camOrigin, camRotation, axisSign);
        }
        if (controlPose.hasTailTipCamAbs)
        {
            world.tailTipWorld = TransformCamPoint(controlPose.tailTipCamAbs, camOrigin, camRotation, axisSign);
        }
        if (controlPose.hasForwardHintCamAbs)
        {
            world.forwardHintWorld = TransformCamPoint(controlPose.forwardHintCamAbs, camOrigin, camRotation, axisSign);
        }
        if (controlPose.hasUpHintCamAbs)
        {
            world.upHintWorld = TransformCamPoint(controlPose.upHintCamAbs, camOrigin, camRotation, axisSign);
        }

        world.frontLeftLegWorld = TransformChain(controlPose.frontLeftLegChainCamAbs, camOrigin, camRotation, axisSign);
        world.frontRightLegWorld = TransformChain(controlPose.frontRightLegChainCamAbs, camOrigin, camRotation, axisSign);
        world.rearLeftLegWorld = TransformChain(controlPose.rearLeftLegChainCamAbs, camOrigin, camRotation, axisSign);
        world.rearRightLegWorld = TransformChain(controlPose.rearRightLegChainCamAbs, camOrigin, camRotation, axisSign);
        world.headWorld = TransformChain(controlPose.headChainCamAbs, camOrigin, camRotation, axisSign);
        world.tailWorld = TransformChain(controlPose.tailChainCamAbs, camOrigin, camRotation, axisSign);
        return world;
    }

    public static Vector3 TransformCamPoint(
        Vector3 point,
        Vector3 camOrigin,
        Quaternion camRotation,
        Vector3 axisSign)
    {
        return camOrigin + (camRotation * ApplyAxisSign(point, axisSign));
    }

    public static Vector3[] TransformChain(
        Vector3[] chainCamAbs,
        Vector3 camOrigin,
        Quaternion camRotation,
        Vector3 axisSign)
    {
        if (chainCamAbs == null || chainCamAbs.Length == 0)
        {
            return null;
        }

        Vector3[] chainWorld = new Vector3[chainCamAbs.Length];
        for (int i = 0; i < chainCamAbs.Length; i++)
        {
            chainWorld[i] = TransformCamPoint(chainCamAbs[i], camOrigin, camRotation, axisSign);
        }

        return chainWorld;
    }

    private static Vector3 ApplyAxisSign(Vector3 point, Vector3 axisSign)
    {
        return new Vector3(point.x * axisSign.x, point.y * axisSign.y, point.z * axisSign.z);
    }
}
