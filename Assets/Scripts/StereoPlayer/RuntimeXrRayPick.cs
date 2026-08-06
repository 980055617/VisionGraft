using UnityEngine;

// コントローラのレイで動画スクリーン上の対象を選ぶための純粋ロジック。
//
// XR デバイスが返す姿勢は tracking origin ローカルなので、そのまま world のレイには
// 使えない。シーンの階層（OVRCameraRig の TrackingSpace / XR Origin の Camera Offset）を
// 名前や親子関係で当てにすると構成を変えたときに黙って壊れるため、HMD の「ローカル姿勢」と
// シーン上の head Transform の「world 姿勢」の差分から変換を作る。head と HMD は同じものを
// 指しているので、この 2 つが分かれば tracking origin → world の変換が一意に決まる。
public static class RuntimeXrRayPick
{
    public readonly struct PressDecision
    {
        public PressDecision(bool pick, bool previousPressed)
        {
            this.pick = pick;
            this.previousPressed = previousPressed;
        }

        public readonly bool pick;
        public readonly bool previousPressed;
    }

    // 押しっぱなしで毎フレーム選び直さないよう、押した瞬間だけ拾う。
    public static PressDecision ResolvePress(bool hasPointer, bool pressed, bool previousPressed)
    {
        if (!hasPointer)
        {
            return new PressDecision(false, false);
        }

        return new PressDecision(pressed && !previousPressed, pressed);
    }

    public static Ray ResolveWorldRay(
        Vector3 headWorldPosition,
        Quaternion headWorldRotation,
        Vector3 headLocalPosition,
        Quaternion headLocalRotation,
        Vector3 pointerLocalPosition,
        Quaternion pointerLocalRotation)
    {
        Quaternion trackingToWorldRotation = headWorldRotation * Quaternion.Inverse(headLocalRotation);
        Vector3 trackingToWorldOffset = headWorldPosition - trackingToWorldRotation * headLocalPosition;

        Vector3 origin = trackingToWorldRotation * pointerLocalPosition + trackingToWorldOffset;
        Vector3 direction = trackingToWorldRotation * pointerLocalRotation * Vector3.forward;
        return new Ray(origin, direction);
    }
}
