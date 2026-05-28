using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class InteractiveMotionAnimationTools
{
    private const string HumanClipFolder = "Assets/Animations/InteractiveMotion/Human";
    private const string CharacterPath = "Assets/Models/character.fbx";
    private const string TestScenePath = "Assets/Scenes/InteractiveMotionAnimationTest.unity";

    [MenuItem("VisionGraft/Interactive Motion/Reimport Human Clips As Humanoid")]
    public static void ReimportHumanClipsAsHumanoid()
    {
        string[] fbxPaths = Directory.GetFiles(HumanClipFolder, "*.fbx", SearchOption.TopDirectoryOnly);
        for (int i = 0; i < fbxPaths.Length; i++)
        {
            string path = NormalizeAssetPath(fbxPaths[i]);
            ModelImporter importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null)
            {
                continue;
            }

            importer.importAnimation = true;
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.animationCompression = ModelImporterAnimationCompression.KeyframeReduction;
            importer.SaveAndReimport();
        }

        AssetDatabase.Refresh();
        Debug.Log("Reimported Human interactive motion FBX files as Humanoid.");
    }

    [MenuItem("VisionGraft/Interactive Motion/Create Animation Test Scene")]
    public static void CreateAnimationTestScene()
    {
        ReimportHumanClipsAsHumanoid();

        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        scene.name = "InteractiveMotionAnimationTest";

        GameObject characterAsset = AssetDatabase.LoadAssetAtPath<GameObject>(CharacterPath);
        if (characterAsset == null)
        {
            Debug.LogError("Could not load character model at " + CharacterPath);
            return;
        }

        GameObject character = PrefabUtility.InstantiatePrefab(characterAsset) as GameObject;
        if (character == null)
        {
            character = Object.Instantiate(characterAsset);
        }
        character.name = "HumanAnimationTestCharacter";
        character.transform.position = Vector3.zero;
        character.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
        character.transform.localScale = Vector3.one;

        Animator animator = character.GetComponentInChildren<Animator>();
        if (animator == null)
        {
            animator = character.AddComponent<Animator>();
        }
        animator.applyRootMotion = false;

        HumanAnimationClipTester tester = character.GetComponent<HumanAnimationClipTester>();
        if (tester == null)
        {
            tester = character.AddComponent<HumanAnimationClipTester>();
        }
        tester.animator = animator;
        tester.SetClips(LoadHumanClips());

        Camera camera = Camera.main;
        if (camera != null)
        {
            camera.transform.position = new Vector3(0f, 1.2f, -3f);
            camera.transform.rotation = Quaternion.Euler(8f, 0f, 0f);
        }

        Light light = Object.FindFirstObjectByType<Light>();
        if (light != null)
        {
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            light.intensity = 1.2f;
        }

        EnsureDirectory("Assets/Scenes");
        EditorSceneManager.SaveScene(scene, TestScenePath);
        Selection.activeGameObject = character;
        Debug.Log("Created " + TestScenePath + " with " + tester.clips.Length + " Human clips. Use Left/Right arrows to switch clips and Space to replay.");
    }

    private static List<AnimationClip> LoadHumanClips()
    {
        List<AnimationClip> clips = new List<AnimationClip>();
        string[] guids = AssetDatabase.FindAssets("t:AnimationClip", new[] { HumanClipFolder });
        for (int i = 0; i < guids.Length; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[i]);
            Object[] assets = AssetDatabase.LoadAllAssetRepresentationsAtPath(path);
            for (int j = 0; j < assets.Length; j++)
            {
                AnimationClip clip = assets[j] as AnimationClip;
                if (clip == null || clip.name.StartsWith("__preview__", System.StringComparison.Ordinal))
                {
                    continue;
                }
                clips.Add(clip);
            }
        }
        return clips;
    }

    private static string NormalizeAssetPath(string path)
    {
        return path.Replace('\\', '/');
    }

    private static void EnsureDirectory(string assetDirectory)
    {
        if (!Directory.Exists(assetDirectory))
        {
            Directory.CreateDirectory(assetDirectory);
        }
    }
}
