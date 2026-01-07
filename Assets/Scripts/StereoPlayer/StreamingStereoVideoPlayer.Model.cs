using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private void PlaceOrMoveTestModel(PickResult pick)
    {
        TrySpawnOrMoveTestModelAtPixel(pick.screen, pick.pixel.x, pick.pixel.y);
    }

    private void TrySpawnTestModel()
    {
        if (leftScreen == null)
        {
            Debug.LogWarning("Test model skipped: leftScreen is null.");
            return;
        }

        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            Debug.LogWarning("Test model skipped: manifest eye_w/eye_h invalid or not loaded.");
            return;
        }

        Vector2Int finalPixel = testPixel;
        if (finalPixel.x < 0 || finalPixel.y < 0)
        {
            finalPixel = new Vector2Int(manifest.eye_w / 2, manifest.eye_h / 2);
        }

        TrySpawnOrMoveTestModelAtPixel(leftScreen, finalPixel.x, finalPixel.y);
    }

    private void TrySpawnOrMoveTestModelAtPixel(Transform screen, int u, int v)
    {
        if (screen == null)
        {
            Debug.LogWarning("TrySpawnOrMoveTestModelAtPixel: screen is null.");
            return;
        }

        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            Debug.LogWarning("TrySpawnOrMoveTestModelAtPixel: manifest not ready.");
            return;
        }

        Vector3 worldOnPlane = EyePixelToWorldOnScreen(u, v, screen, manifest.eye_w, manifest.eye_h, 0f);
        Transform head = GetViewCamera() != null ? GetViewCamera().transform : GetHeadTransform();
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
        else
        {
            spawnedTestModel.transform.SetPositionAndRotation(world, rotation);
        }

        spawnedTestModel.transform.localScale = Vector3.one * testModelSizeMeters;
        VLog($"SpawnOrMoveTestModel: screen={screen.name} pixel=({u},{v}) world={world} rot={rotation.eulerAngles}");
        VLog($"SpawnOrMoveTestModelDepth: dist={dist:F3} depth={depth:F3} head={(head != null ? head.position.ToString() : "null")} screen={screen.position}");
    }
}
