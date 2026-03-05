using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: test model settings/state, screen projection helpers, lock frame constant
    // Provides: test model spawn/move/lock workflow and transform lock component

    private void TrySpawnTestModel()
    {
        if (replacePrefab != null)
        {
            return;
        }

        if (leftScreen == null)
        {
            return;
        }

        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return;
        }

        Vector2Int finalPixel = testPixel;
        if (finalPixel.x < 0 || finalPixel.y < 0)
        {
            finalPixel = new Vector2Int(manifest.eye_w / 2, manifest.eye_h / 2);
        }

        TrySpawnOrMoveTestModelAtPixel(leftScreen, finalPixel.x, finalPixel.y);
    }


    private void TrySpawnOrMoveTestModel(PickResult pick)
    {
        if (pick.screen == null)
        {
            return;
        }

        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return;
        }

        if (!pick.hasHitDistance)
        {
            TrySpawnOrMoveTestModelAtPixel(pick.screen, pick.pixel.x, pick.pixel.y);
            return;
        }

        Vector3 rayDir = pick.ray.direction.normalized;
        if (rayDir == Vector3.zero)
        {
            TrySpawnOrMoveTestModelAtPixel(pick.screen, pick.pixel.x, pick.pixel.y);
            return;
        }

        float depthTowardCamera = testDepthMeters;
        float placeDist = Mathf.Max(0.05f, pick.hitDistance - depthTowardCamera);
        Vector3 world = pick.ray.origin + rayDir * placeDist;

        Vector3 right = Vector3.Cross(Vector3.up, rayDir);
        if (right.sqrMagnitude < 0.000001f)
        {
            right = pick.screen.right;
        }
        right.Normalize();
        Vector3 up = Vector3.Cross(rayDir, right).normalized;

        world += right * testModelOffsetMeters.x + up * testModelOffsetMeters.y;
        Quaternion rotation = Quaternion.LookRotation(-rayDir, up);

        ApplyTestModelTransform(world, rotation);
    }


    private void TrySpawnOrMoveTestModelAtPixel(Transform screen, int u, int v)
    {
        if (replacePrefab != null)
        {
            return;
        }

        if (screen == null)
        {
            return;
        }

        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return;
        }

        Vector3 worldOnPlane = EyePixelToWorldOnScreen(u, v, screen, manifest.eye_w, manifest.eye_h, 0f);
        Transform head = GetViewOrHeadTransform();
        Vector3 frontDir = (head != null)
            ? (head.position - screen.position).normalized
            : GetScreenFrontDirection(screen);
        if (frontDir == Vector3.zero)
        {
            frontDir = GetScreenFrontDirection(screen);
        }

        float dist = head != null ? Vector3.Distance(head.position, screen.position) : testDepthMeters;
        float maxDepth = Mathf.Max(0.01f, dist - 0.05f);
        float depth = Mathf.Clamp(testDepthMeters, 0.01f, maxDepth);

        Vector3 world = worldOnPlane
            + screen.right * testModelOffsetMeters.x
            + screen.up * testModelOffsetMeters.y
            + frontDir * depth;
        Quaternion rotation = Quaternion.LookRotation(-frontDir, screen.up);

        ApplyTestModelTransform(world, rotation);
    }


    private void ApplyTestModelTransform(Vector3 world, Quaternion rotation)
    {
        if (destroyPreviousTestModel && spawnedTestModel != null)
        {
            Destroy(spawnedTestModel);
            spawnedTestModel = null;
        }

        if (spawnedTestModel == null)
        {
            spawnedTestModel = testModelPrefab != null
                ? Instantiate(testModelPrefab, world, rotation)
                : GameObject.CreatePrimitive(PrimitiveType.Cube);
            spawnedTestModel.name = "TestModel(auto)";
            if (testModelPrefab == null)
            {
                var collider = spawnedTestModel.GetComponent<Collider>();
                if (collider != null)
                {
                    Destroy(collider);
                }
            }
        }
        spawnedTestModel.transform.SetParent(null, true);
        EnsureTestModelComponents(spawnedTestModel);
        spawnedTestModel.transform.SetPositionAndRotation(world, rotation);
        spawnedTestModel.transform.localScale = Vector3.one * testModelSizeMeters;
        AttachTransformLock(spawnedTestModel, world, rotation);
    }


    private void EnsureTestModelComponents(GameObject model)
    {
        if (model == null)
        {
            return;
        }

        var rb = model.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.constraints = RigidbodyConstraints.FreezeAll;
        }

        var animator = model.GetComponent<Animator>();
        if (animator != null)
        {
            animator.enabled = false;
        }
    }


    private void AttachTransformLock(GameObject model, Vector3 world, Quaternion rotation)
    {
        if (model == null)
        {
            return;
        }

        var locker = model.GetComponent<TestModelTransformLock>();
        if (locker == null)
        {
            locker = model.AddComponent<TestModelTransformLock>();
        }

        locker.Arm(world, rotation, TestModelLockFrames);
    }

    private sealed class TestModelTransformLock : MonoBehaviour
    {
        private Vector3 targetPos;
        private Quaternion targetRot;
        private int framesLeft;

        public void Arm(Vector3 world, Quaternion rotation, int frames)
        {
            targetPos = world;
            targetRot = rotation;
            framesLeft = Mathf.Max(1, frames);
        }

        private void LateUpdate()
        {
            if (framesLeft <= 0)
            {
                Destroy(this);
                return;
            }

            transform.SetPositionAndRotation(targetPos, targetRot);
            framesLeft--;
        }
    }
}

