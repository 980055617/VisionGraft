using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// Batch-mode diagnostic: dumps the full Transform hierarchy of animal source FBX
// assets so their real bone names can be read without opening the Editor UI.
// Used while renaming new "50+ Animated Animals" package models to the canonical
// bone names in AnimalRigDefinition (see docs/adr/0001-canonical-animal-rig-bone-names.md).
public static class AnimalBoneHierarchyDumper
{
    [MenuItem("Tools/VisionGraft/Dump Animal Bone Hierarchy (Buffalo, Lion, Horse)")]
    public static void Run()
    {
        string[] paths =
        {
            "Assets/50+ Animated Animals/2.0/Buffalo/Buffalo.fbx",
            "Assets/50+ Animated Animals/1.0/Lion/Lion.fbx",
            "Assets/50+ Animated Animals/2.0/Horse/Horse.fbx",
            "Assets/50+ Animated Animals/2.0/Wolf/Wolf.fbx",
            "Assets/50+ Animated Animals/1.0/Wild Boar/WildBoar.fbx",
        };

        var sb = new StringBuilder();
        foreach (string path in paths)
        {
            sb.AppendLine("=== " + path + " ===");
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null)
            {
                sb.AppendLine("(load failed)");
                continue;
            }
            Dump(go.transform, 0, sb);
            sb.AppendLine();
        }

        string outPath = Path.Combine(Application.dataPath, "..", "animal_bone_dump.txt");
        File.WriteAllText(outPath, sb.ToString());
        Debug.Log("Wrote " + outPath);
    }

    // Dumps the remaining (not-yet-canonical) Resources/Models/Animal prefabs by resolving
    // each PrefabInstance back to its source FBX automatically (GetCorrespondingObjectFromSource)
    // instead of needing each FBX path/guid looked up by hand.
    [MenuItem("Tools/VisionGraft/Dump Remaining Animal Prefab Bone Hierarchies")]
    public static void RunRemaining()
    {
        string[] prefabPaths =
        {
            "Assets/Resources/Models/Animal/AmericanMink.prefab",
            "Assets/Resources/Models/Animal/Badger.prefab",
            "Assets/Resources/Models/Animal/Bear1.0.prefab",
            "Assets/Resources/Models/Animal/Bear2.0.prefab",
            "Assets/Resources/Models/Animal/BearWITHFUR.prefab",
            "Assets/Resources/Models/Animal/Beaver.prefab",
            "Assets/Resources/Models/Animal/BighornSheep.prefab",
            "Assets/Resources/Models/Animal/Bloodhund.prefab",
            "Assets/Resources/Models/Animal/BoarV2.prefab",
            "Assets/Resources/Models/Animal/Deer.prefab",
            "Assets/Resources/Models/Animal/Deer1.0.prefab",
            "Assets/Resources/Models/Animal/Deer1.prefab",
            "Assets/Resources/Models/Animal/DeerV2.prefab",
            "Assets/Resources/Models/Animal/Doe1.0.prefab",
            "Assets/Resources/Models/Animal/Donkey.prefab",
            "Assets/Resources/Models/Animal/Donkey1.0.prefab",
            "Assets/Resources/Models/Animal/Elk1.0.prefab",
            "Assets/Resources/Models/Animal/EuropeanBadger.prefab",
            "Assets/Resources/Models/Animal/Fox.prefab",
            "Assets/Resources/Models/Animal/FoxV2.prefab",
            "Assets/Resources/Models/Animal/FoxWITHFUR.prefab",
            "Assets/Resources/Models/Animal/Gnou.prefab",
            "Assets/Resources/Models/Animal/Goat.prefab",
            "Assets/Resources/Models/Animal/Goat1.prefab",
            "Assets/Resources/Models/Animal/GrayWolf.prefab",
            "Assets/Resources/Models/Animal/Hare.prefab",
            "Assets/Resources/Models/Animal/Hyena.prefab",
            "Assets/Resources/Models/Animal/LabradorDog.prefab",
            "Assets/Resources/Models/Animal/Lioness.prefab",
            "Assets/Resources/Models/Animal/LionessV2.prefab",
            "Assets/Resources/Models/Animal/Lynx.prefab",
            "Assets/Resources/Models/Animal/Mammoth.prefab",
            "Assets/Resources/Models/Animal/Moose.prefab",
            "Assets/Resources/Models/Animal/Moose1.0.prefab",
            "Assets/Resources/Models/Animal/MooseF.prefab",
            "Assets/Resources/Models/Animal/MooseM.prefab",
            "Assets/Resources/Models/Animal/MountainGoat.prefab",
            "Assets/Resources/Models/Animal/Pronghorn1.0.prefab",
            "Assets/Resources/Models/Animal/Puma.prefab",
            "Assets/Resources/Models/Animal/Rabbit.prefab",
            "Assets/Resources/Models/Animal/Racoon.prefab",
            "Assets/Resources/Models/Animal/Warthog.prefab",
            // Bird / non-quadruped - dumped anyway for confirmation, excluded from renaming:
            "Assets/Resources/Models/Animal/Goose.prefab",
            "Assets/Resources/Models/Animal/Guineafowl.prefab",
            "Assets/Resources/Models/Animal/Pheasant.prefab",
            "Assets/Resources/Models/Animal/Kangaroo.prefab",
        };

        var sb = new StringBuilder();
        foreach (string path in prefabPaths)
        {
            sb.AppendLine("=== " + path + " ===");
            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefabAsset == null)
            {
                sb.AppendLine("(load failed)");
                continue;
            }

            GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(prefabAsset) as GameObject;
            GameObject dumpTarget = source != null ? source : prefabAsset;
            string sourcePath = source != null ? AssetDatabase.GetAssetPath(source) : "(no source prefab found - dumping prefab itself)";
            sb.AppendLine("source: " + sourcePath);
            Dump(dumpTarget.transform, 0, sb);
            sb.AppendLine();
        }

        string outPath = Path.Combine(Application.dataPath, "..", "animal_bone_dump_remaining.txt");
        File.WriteAllText(outPath, sb.ToString());
        Debug.Log("Wrote " + outPath);
    }

    private static void Dump(Transform t, int depth, StringBuilder sb)
    {
        sb.Append(' ', depth * 2).AppendLine(t.name);
        for (int i = 0; i < t.childCount; i++)
        {
            Dump(t.GetChild(i), depth + 1, sb);
        }
    }
}
