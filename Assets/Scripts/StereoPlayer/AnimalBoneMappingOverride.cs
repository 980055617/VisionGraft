using UnityEngine;

/// <summary>
/// 新しい動物モデルのボーン名が canonical names（front_l_upper 等）と異なる場合に
/// prefab root に付けて使用する。runtime の bone resolver がこのマッピングを優先する。
/// Tools > Setup Animal Bone Mappings で自動設定可能。
/// </summary>
[AddComponentMenu("VisionGraft/Animal Bone Mapping Override")]
public class AnimalBoneMappingOverride : MonoBehaviour
{
    [Header("Body")]
    public string spine;
    public string neck;
    public string head;

    [Header("Tail")]
    public string tailBase;
    public string tailMid;
    public string tailTip;

    [Header("Front Left Leg")]
    public string frontLUpper;
    public string frontLLower;
    public string frontLPaw;

    [Header("Front Right Leg")]
    public string frontRUpper;
    public string frontRLower;
    public string frontRPaw;

    [Header("Rear Left Leg")]
    public string rearLUpper;
    public string rearLLower;
    public string rearLPaw;
    public string rearLToe;

    [Header("Rear Right Leg")]
    public string rearRUpper;
    public string rearRLower;
    public string rearRPaw;
    public string rearRToe;
}
