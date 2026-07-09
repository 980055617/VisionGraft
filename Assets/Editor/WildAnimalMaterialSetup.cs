using UnityEngine;
using UnityEditor;

public static class WildAnimalMaterialSetup
{
    private const string TexturePath = "Assets/Textures/wild_animals_map.png";
    private const string MaterialPath = "Assets/Resources/Models/Animal/WildAnimals.mat";
    private const string AnimalFolder = "Assets/Resources/Models/Animal";

    private static readonly string[] FbxNames =
    {
        "bear", "boar", "deer_1", "deer_2", "fox", "hedhog", "owl", "rabbit", "squirrel", "wolf"
    };

    [MenuItem("VisionGraft/Setup Wild Animal Materials")]
    public static void Run()
    {
        Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
        if (tex == null)
        {
            Debug.LogError($"[WildAnimalSetup] テクスチャが見つかりません: {TexturePath}");
            return;
        }

        Material mat = GetOrCreateMaterial(tex);

        foreach (string name in FbxNames)
        {
            string fbxPath = $"{AnimalFolder}/{name}.fbx";
            GameObject fbxRoot = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (fbxRoot == null)
            {
                Debug.LogWarning($"[WildAnimalSetup] FBX が見つかりません: {fbxPath}");
                continue;
            }

            string prefabName = char.ToUpper(name[0]) + name.Substring(1);
            string prefabPath = $"{AnimalFolder}/{prefabName}.prefab";

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(fbxRoot);
            ApplyMaterialToAllRenderers(instance, mat);
            EnsureAnimator(instance);
            PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
            Object.DestroyImmediate(instance);

            Debug.Log($"[WildAnimalSetup] Prefab 作成: {prefabPath}");
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[WildAnimalSetup] 完了。");
    }

    private static Material GetOrCreateMaterial(Texture2D tex)
    {
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (mat == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("[WildAnimalSetup] URP/Lit シェーダーが見つかりません。");
                return null;
            }
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, MaterialPath);
        }
        mat.SetTexture("_BaseMap", tex);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    private static void ApplyMaterialToAllRenderers(GameObject root, Material mat)
    {
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
        {
            Material[] slots = new Material[r.sharedMaterials.Length];
            for (int i = 0; i < slots.Length; i++)
                slots[i] = mat;
            r.sharedMaterials = slots;
        }
    }

    private static void EnsureAnimator(GameObject root)
    {
        if (root.GetComponent<Animator>() == null)
            root.AddComponent<Animator>();
    }
}
