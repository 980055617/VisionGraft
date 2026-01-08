using UnityEngine;

public class DumpHumanoidBones : MonoBehaviour
{
    public bool dumpOnStart = true;
    public bool dumpOnce = true;
    private bool dumped;

    private void Start()
    {
        if (dumpOnStart)
        {
            Dump();
        }
    }

    [ContextMenu("Dump Humanoid Bones")]
    public void Dump()
    {
        if (dumpOnce && dumped)
        {
            return;
        }

        dumped = true;
        Animator animator = GetComponentInChildren<Animator>();
        if (animator == null || !animator.isHuman)
        {
            Debug.LogWarning("DumpHumanoidBones: animator missing or not humanoid.");
            return;
        }

        foreach (HumanBodyBones bone in System.Enum.GetValues(typeof(HumanBodyBones)))
        {
            if (bone == HumanBodyBones.LastBone)
            {
                continue;
            }

            Transform t = animator.GetBoneTransform(bone);
            if (t == null)
            {
                continue;
            }

            Debug.Log($"HumanoidBone: {bone} name={t.name} path={GetPath(t)}");
        }
    }

    private string GetPath(Transform t)
    {
        string path = t.name;
        Transform cur = t.parent;
        while (cur != null)
        {
            path = $"{cur.name}/{path}";
            cur = cur.parent;
        }
        return path;
    }
}
