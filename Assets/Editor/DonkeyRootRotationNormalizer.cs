using UnityEditor;
using UnityEngine;

// 21_Donkey1.0 の root 回転を恒等へ正規化する。
//
// この prefab の root には (270, 90, 0) が入っているが、写像は local (x,y,z) → (-y,z,x) で、
// ローカル Z（鼻先〜尾の体軸）を world の上へ持ち上げる。**ロバが尾で立つ**姿勢であり、
// 作者の補正ではなく import 由来のゴミ（docs/bundle-placement.md の DumpAnimalUpAxis 実測）。
//
// 実行時は配置がこの回転を潰していたので今まで表面化していなかったが、
// prefab の root 補正を尊重する変更を入れると縦向きになってしまう。
// コードに例外を書くのではなく、データ側を正しくする。
//
// 子の補償はしない。潰された状態（＝ローカル軸がそのまま world 軸）が正しい姿なので、
// 恒等にするだけで実行時の見た目は変わらない。
//
//   Unity.exe -batchmode -projectPath . -executeMethod DonkeyRootRotationNormalizer.Normalize -quit
public static class DonkeyRootRotationNormalizer
{
    private const string Path = "Assets/Resources/Models/Animal/21_Donkey1.0.prefab";

    public static void Normalize()
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Path);
        if (prefab == null)
        {
            Debug.Log($"[DonkeyFix] not found: {Path}");
            return;
        }

        Quaternion before = prefab.transform.localRotation;
        if (Quaternion.Angle(Quaternion.identity, before) <= 0.01f)
        {
            Debug.Log("[DonkeyFix] 既に恒等です。何もしません");
            return;
        }

        GameObject root = PrefabUtility.LoadPrefabContents(Path);
        root.transform.localRotation = Quaternion.identity;
        PrefabUtility.SaveAsPrefabAsset(root, Path);
        PrefabUtility.UnloadPrefabContents(root);
        AssetDatabase.Refresh();

        GameObject reloaded = AssetDatabase.LoadAssetAtPath<GameObject>(Path);
        Debug.Log($"[DonkeyFix] before={before.eulerAngles:F1} after={reloaded.transform.localRotation.eulerAngles:F1}");
    }
}
