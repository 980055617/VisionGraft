using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// HomeScene を ExperimentScene から生成する。
//
// 手で YAML を書くとリグの参照が壊れるので、Unity 自身に作らせる。ExperimentScene は
// XR リグ・カメラ・ライト・EventSystem を持っていて、違いは末尾の 1 オブジェクト
// （ExperimentController）だけなので、そこを HomeMenu に差し替えるのが一番安全。
//
//   Unity.exe -batchmode -projectPath . -executeMethod HomeSceneBuilder.Build -quit
public static class HomeSceneBuilder
{
    private const string SourceScenePath = "Assets/Scenes/ExperimentScene.unity";
    private const string HomeScenePath = "Assets/Scenes/HomeScene.unity";

    public static void Build()
    {
        Scene scene = EditorSceneManager.OpenScene(SourceScenePath, OpenSceneMode.Single);
        if (!scene.IsValid())
        {
            Debug.LogError($"[HomeSceneBuilder] could not open {SourceScenePath}");
            EditorApplication.Exit(1);
            return;
        }

        // ExperimentController を取り除き、その UI prefab 参照だけ引き継ぐ。
        GameObject panelPrefab = null;
        float distance = 1.2f;
        foreach (ExperimentController controller in Object.FindObjectsByType<ExperimentController>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (panelPrefab == null)
            {
                panelPrefab = controller.panelCanvasWithInteractionRayPrefab;
                distance = controller.panelDistanceMeters;
            }

            Debug.Log($"[HomeSceneBuilder] remove: {controller.gameObject.name}");
            Object.DestroyImmediate(controller.gameObject);
        }

        // 試行シーンのプレイヤーが紛れていたら落とす（ExperimentScene には無いはずだが保険）。
        foreach (StreamingStereoVideoPlayer player in Object.FindObjectsByType<StreamingStereoVideoPlayer>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            Debug.Log($"[HomeSceneBuilder] remove: {player.gameObject.name}");
            Object.DestroyImmediate(player.gameObject);
        }

        GameObject menuObject = new GameObject("HomeMenu");
        HomeMenu menu = menuObject.AddComponent<HomeMenu>();
        menu.panelCanvasWithInteractionRayPrefab = panelPrefab;
        menu.panelDistanceMeters = distance;

        Debug.Log($"[HomeSceneBuilder] HomeMenu created (panelPrefab={(panelPrefab != null ? panelPrefab.name : "null")})");

        if (!EditorSceneManager.SaveScene(scene, HomeScenePath))
        {
            Debug.LogError($"[HomeSceneBuilder] save failed: {HomeScenePath}");
            EditorApplication.Exit(1);
            return;
        }

        RegisterBuildScenes();
        AssetDatabase.SaveAssets();
        Debug.Log($"[HomeSceneBuilder] done: {HomeScenePath}");
    }


    // HomeScene を先頭にする。Build And Run は先頭シーンから起動する。
    private static void RegisterBuildScenes()
    {
        string[] wanted =
        {
            HomeScenePath,
            "Assets/Scenes/TestScene.unity",
            "Assets/Scenes/ExperimentScene.unity",
            "Assets/Scenes/TrialScene.unity",
        };

        var scenes = new List<EditorBuildSettingsScene>();
        for (int i = 0; i < wanted.Length; i++)
        {
            scenes.Add(new EditorBuildSettingsScene(wanted[i], true));
        }

        // 上のリストに無いシーンが登録されていたら、無効化して末尾に残す。
        // 黙って消すと「ビルドから消えた」に気付けない。
        foreach (EditorBuildSettingsScene existing in EditorBuildSettings.scenes)
        {
            if (System.Array.IndexOf(wanted, existing.path) >= 0)
            {
                continue;
            }

            Debug.LogWarning($"[HomeSceneBuilder] 未知のシーンを無効化して残します: {existing.path}");
            scenes.Add(new EditorBuildSettingsScene(existing.path, false));
        }

        EditorBuildSettings.scenes = scenes.ToArray();
        for (int i = 0; i < scenes.Count; i++)
        {
            Debug.Log($"[HomeSceneBuilder] build scene {i}: {scenes[i].path} enabled={scenes[i].enabled}");
        }
    }
}
