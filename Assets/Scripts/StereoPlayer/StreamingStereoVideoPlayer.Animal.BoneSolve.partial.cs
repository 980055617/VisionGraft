using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private void RegisterAnimalAimChild(AnimalRigCache cache, Transform bone, Transform aimChild)
    {
        if (bone == null || aimChild == null)
        {
            return;
        }

        cache.aimChildByBone[bone] = aimChild;
    }

    private void RegisterAnimalAimPairs(AnimalRigCache cache, params Transform[] bones)
    {
        if (bones == null)
        {
            return;
        }

        for (int i = 0; i + 1 < bones.Length; i += 2)
        {
            RegisterAnimalAimChild(cache, bones[i], bones[i + 1]);
        }
    }


    private void PrimeAnimalBind(AnimalRigCache cache, Transform bone)
    {
        if (bone == null || cache.bindRotLocal.ContainsKey(bone))
        {
            return;
        }

        cache.bindRotLocal[bone] = bone.localRotation;
        Vector3 bindDirLocal = Vector3.forward;
        if (TryGetBoneCenterDirectionWorld(cache, bone, out Vector3 bindDirWorld))
        {
            bindDirLocal = bone.InverseTransformDirection(bindDirWorld);
        }
        cache.bindDirLocal[bone] = bindDirLocal == Vector3.zero ? Vector3.forward : bindDirLocal.normalized;
    }

    private void PrimeAnimalBinds(AnimalRigCache cache, params Transform[] bones)
    {
        if (bones == null)
        {
            return;
        }

        for (int i = 0; i < bones.Length; i++)
        {
            PrimeAnimalBind(cache, bones[i]);
        }
    }


    private bool ApplyAnimalBoneFromJoints(AnimalRigCache cache, Transform bone, Vector3[] jointsWorld, byte[] vis, int idxA, int idxB, float alpha)
    {
        if (bone == null)
        {
            return false;
        }

        if (!TryGetJointPoint(jointsWorld, vis, idxA, out Vector3 a) || !TryGetJointPoint(jointsWorld, vis, idxB, out Vector3 b))
        {
            return false;
        }

        return ApplyAnimalBoneFromPoints(cache, bone, a, b, alpha);
    }


    private bool ApplyAnimalBoneFromPoints(AnimalRigCache cache, Transform bone, Vector3 pointA, Vector3 pointB, float alpha)
    {
        if (bone == null)
        {
            return false;
        }

        Vector3 targetDir = (pointB - pointA).normalized;
        if (targetDir == Vector3.zero)
        {
            return false;
        }

        if (TryGetBoneCenterDirectionWorld(cache, bone, out Vector3 currentDir))
        {
            Quaternion deltaWorld = Quaternion.FromToRotation(currentDir, targetDir);
            Quaternion targetWorld = deltaWorld * bone.rotation;
            // When nearly opposite, FromTo rotation axis is unstable and can spin frame-to-frame.
            // Fall back to bind-space solve for deterministic behavior.
            float dot = Vector3.Dot(currentDir, targetDir);
            if (dot > -0.98f)
            {
                bone.rotation = Quaternion.Slerp(bone.rotation, targetWorld, Mathf.Clamp01(alpha));
                return true;
            }
        }

        Vector3 targetLocalDir = bone.parent != null
            ? bone.parent.InverseTransformDirection(targetDir)
            : targetDir;
        if (targetLocalDir == Vector3.zero)
        {
            return false;
        }
        targetLocalDir.Normalize();

        if (!cache.bindDirLocal.TryGetValue(bone, out Vector3 bindDirLocal) || bindDirLocal == Vector3.zero)
        {
            bindDirLocal = Vector3.forward;
        }

        if (!cache.bindRotLocal.TryGetValue(bone, out Quaternion bindRotLocal))
        {
            bindRotLocal = bone.localRotation;
        }

        Quaternion targetLocal = Quaternion.FromToRotation(bindDirLocal, targetLocalDir) * bindRotLocal;
        bone.localRotation = Quaternion.Slerp(bone.localRotation, targetLocal, Mathf.Clamp01(alpha));
        return true;
    }

    private Transform ResolveAnimalAimChild(AnimalRigCache cache, Transform bone)
    {
        if (bone != null && cache.aimChildByBone.TryGetValue(bone, out Transform mapped) && mapped != null)
        {
            return mapped;
        }

        if (bone != null && bone.childCount > 0)
        {
            return bone.GetChild(0);
        }

        return null;
    }


    private bool TryGetBoneCenterDirectionWorld(AnimalRigCache cache, Transform bone, out Vector3 dirWorld)
    {
        dirWorld = Vector3.zero;
        if (bone == null)
        {
            return false;
        }

        Transform centerTarget = ResolveAnimalAimChild(cache, bone);
        // For limb segments, prefer pure bone-to-bone pivot direction.
        if (centerTarget != null && IsAnimalLimbBone(cache, bone))
        {
            Vector3 childPivotDir = centerTarget.position - bone.position;
            if (childPivotDir.sqrMagnitude > 0.000001f)
            {
                dirWorld = childPivotDir.normalized;
                return true;
            }
        }

        if (centerTarget == null)
        {
            centerTarget = bone;
        }

        if (!TryGetTransformCenterWorld(centerTarget, out Vector3 centerWorld))
        {
            return false;
        }

        Vector3 rawDir = centerWorld - bone.position;
        if (rawDir.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        dirWorld = rawDir.normalized;
        return true;
    }


    private static bool IsAnimalLimbBone(AnimalRigCache cache, Transform bone)
    {
        if (bone == null)
        {
            return false;
        }

        return
            bone == cache.leftFrontUpper ||
            bone == cache.leftFrontLower ||
            bone == cache.leftFrontPaw ||
            bone == cache.rightFrontUpper ||
            bone == cache.rightFrontLower ||
            bone == cache.rightFrontPaw ||
            bone == cache.leftRearUpper ||
            bone == cache.leftRearLower ||
            bone == cache.leftRearPaw ||
            bone == cache.rightRearUpper ||
            bone == cache.rightRearLower ||
            bone == cache.rightRearPaw;
    }


    private bool TryGetTransformCenterWorld(Transform target, out Vector3 centerWorld)
    {
        centerWorld = Vector3.zero;
        if (target == null)
        {
            return false;
        }

        SkinnedMeshRenderer smr = target.GetComponent<SkinnedMeshRenderer>();
        if (smr != null)
        {
            centerWorld = target.TransformPoint(smr.localBounds.center);
            return true;
        }

        MeshFilter mf = target.GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null)
        {
            centerWorld = target.TransformPoint(mf.sharedMesh.bounds.center);
            return true;
        }

        Renderer renderer = target.GetComponent<Renderer>();
        if (renderer != null)
        {
            centerWorld = renderer.bounds.center;
            return true;
        }

        return false;
    }
}
