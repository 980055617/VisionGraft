using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// 被験者実験用の 2 シーンを TestScene から生成する。
//
//   ExperimentScene … ベースシーン。XR リグ・EventSystem・ExperimentController を持ち、
//                     セッション中ずっと常駐する。
//   TrialScene      … 試行ごとに Additive でロード／アンロードされる再生シーン。
//                     VideoPlayerRoot とライティングのみを持ち、XR リグは持たない。
//
// TestScene をコピー元にするのは、OVRCameraRig / OVRInteractionComprehensive の
// prefab インスタンスとその override をそのまま引き継ぐため。手でシーンを組み直すと
// リグの設定ズレに気付きにくい。
//
// 詳細は Docs/experiment-flow.md を参照。
public static class ExperimentSceneBuilder
{
    private const string SourceScenePath = "Assets/Scenes/TestScene.unity";
    private const string ExperimentScenePath = "Assets/Scenes/ExperimentScene.unity";
    private const string TrialScenePath = "Assets/Scenes/TrialScene.unity";

    // TrialScene に残すルートオブジェクト。XR リグと EventSystem はベースシーン側が持つ。
    private static readonly HashSet<string> TrialSceneKeepNames = new HashSet<string>
    {
        "Directional Light",
        "Global Volume",
    };

    [MenuItem("VisionGraft/Experiment/Create Experiment Scenes")]
    public static void CreateExperimentScenes()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            return;
        }

        GameObject panelPrefab = CreateTrialScene();
        CreateExperimentScene(panelPrefab);
        AddScenesToBuildSettings();

        Debug.Log(
            $"[Experiment] シーンを生成しました:\n  {ExperimentScenePath}\n  {TrialScenePath}\n" +
            "実験を実行するには VisionGraft/Experiment/Set Experiment Scene As Startup を実行してください。");
    }

    // TrialScene を作り、あわせて ExperimentScene の UI に使う ISDK Canvas prefab を返す。
    private static GameObject CreateTrialScene()
    {
        Scene scene = EditorSceneManager.OpenScene(SourceScenePath, OpenSceneMode.Single);

        StreamingStereoVideoPlayer player = FindPlayer(scene);
        if (player == null)
        {
            Debug.LogError($"[Experiment] {SourceScenePath} に StreamingStereoVideoPlayer が見つかりません。");
            return null;
        }

        GameObject panelPrefab = player.bundlePickerCanvasWithInteractionRayPrefab != null
            ? player.bundlePickerCanvasWithInteractionRayPrefab
            : player.runtimeControlsPrefab;

        GameObject playerRoot = player.transform.root.gameObject;
        GameObject[] roots = scene.GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
        {
            GameObject root = roots[i];
            if (root == playerRoot || TrialSceneKeepNames.Contains(root.name))
            {
                continue;
            }

            Object.DestroyImmediate(root);
        }

        ConfigurePlayerForTrialScene(player);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, TrialScenePath);
        return panelPrefab;
    }

    private static void ConfigurePlayerForTrialScene(StreamingStereoVideoPlayer player)
    {
        // 実験では ExperimentController が bundle と条件を注入するので picker は出さない。
        // これらは ExperimentTrialHandoff によって試行ごとに上書きされるが、
        // シーンを単体で開いて確認したときにも安全な値にしておく。
        player.showBundlePickerOnStart = false;
        player.enableNormalModeToggleButton = false;
        player.startInNormalMode = false;
        player.enableRuntimeControls = true;

        // XR リグはベースシーン側にあるため、シーン内参照は必ず切れる。
        // null にしておけば GetHeadTransform() が実行時にカメラから解決する。
        player.headTransform = null;

        EditorUtility.SetDirty(player);
    }

    private static void CreateExperimentScene(GameObject panelPrefab)
    {
        Scene scene = EditorSceneManager.OpenScene(SourceScenePath, OpenSceneMode.Single);

        StreamingStereoVideoPlayer player = FindPlayer(scene);
        if (player != null)
        {
            Object.DestroyImmediate(player.transform.root.gameObject);
        }

        GameObject controllerObj = new GameObject("ExperimentController");
        ExperimentController controller = controllerObj.AddComponent<ExperimentController>();
        controller.trialSceneName = System.IO.Path.GetFileNameWithoutExtension(TrialScenePath);
        controller.panelCanvasWithInteractionRayPrefab = panelPrefab;
        EditorUtility.SetDirty(controller);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ExperimentScenePath);
    }

    private static StreamingStereoVideoPlayer FindPlayer(Scene scene)
    {
        GameObject[] roots = scene.GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
        {
            StreamingStereoVideoPlayer player = roots[i].GetComponentInChildren<StreamingStereoVideoPlayer>(true);
            if (player != null)
            {
                return player;
            }
        }

        return null;
    }

    private static void AddScenesToBuildSettings()
    {
        List<EditorBuildSettingsScene> scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        AddSceneIfMissing(scenes, ExperimentScenePath);
        AddSceneIfMissing(scenes, TrialScenePath);
        EditorBuildSettings.scenes = scenes.ToArray();
    }

    private static void AddSceneIfMissing(List<EditorBuildSettingsScene> scenes, string path)
    {
        for (int i = 0; i < scenes.Count; i++)
        {
            if (scenes[i].path == path)
            {
                scenes[i].enabled = true;
                return;
            }
        }

        scenes.Add(new EditorBuildSettingsScene(path, true));
    }

    // 実験ビルドでは ExperimentScene が最初に起動する必要がある。
    [MenuItem("VisionGraft/Experiment/Set Experiment Scene As Startup")]
    public static void SetExperimentSceneAsStartup()
    {
        MoveSceneToTop(ExperimentScenePath);
        Debug.Log("[Experiment] 起動シーンを ExperimentScene にしました。");
    }

    // 通常の単体再生に戻すとき用。
    [MenuItem("VisionGraft/Experiment/Set Test Scene As Startup")]
    public static void SetTestSceneAsStartup()
    {
        MoveSceneToTop(SourceScenePath);
        Debug.Log("[Experiment] 起動シーンを TestScene に戻しました。");
    }

    private static void MoveSceneToTop(string path)
    {
        List<EditorBuildSettingsScene> scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        int index = scenes.FindIndex(s => s.path == path);
        if (index < 0)
        {
            Debug.LogError($"[Experiment] Build Settings に {path} がありません。先に Create Experiment Scenes を実行してください。");
            return;
        }

        EditorBuildSettingsScene target = scenes[index];
        target.enabled = true;
        scenes.RemoveAt(index);
        scenes.Insert(0, target);
        EditorBuildSettings.scenes = scenes.ToArray();
    }
}
