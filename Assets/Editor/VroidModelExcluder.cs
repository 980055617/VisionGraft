using UnityEditor;
using UnityEngine;

// 06_Female_C.vrm をモデル一覧から外す。
//
// 揺れもの（VRM10SpringBoneJoint ×131）が入っているのに、実際には髪もスカートも
// 体の軸に沿って固まったままで、被写体が逆さになっても落ちない
// （2026-09-02 実測。docs/vr-input-mapping.md §13）。
// Human 17 個のうち 1 個で、プロジェクトの最優先は姿勢一致なので外す判断をした。
//
// **ファイルは消さない。リネームで一覧から外す。**
// LoadPrefabsFromResources は IsIndexedPrefabName（2 桁数字 + "_"）で絞っているので、
// 先頭に "_" を付けるだけで一覧から落ちる。番号の振り直しが要らず、
// model_selection.json は prefab 名で保存しているので他のモデルの保存値も壊れない。
//
//   Unity.exe -batchmode -projectPath . -executeMethod VroidModelExcluder.Exclude -quit
public static class VroidModelExcluder
{
    private const string Path = "Assets/Resources/Models/Human/06_Female_C.vrm";
    private const string NewName = "_06_Female_C";

    public static void Exclude()
    {
        if (AssetDatabase.LoadAssetAtPath<Object>(Path) == null)
        {
            Debug.Log($"[Exclude] 見つかりません（既にリネーム済み？）: {Path}");
            return;
        }

        string error = AssetDatabase.RenameAsset(Path, NewName);
        if (!string.IsNullOrEmpty(error))
        {
            Debug.LogError($"[Exclude] リネームに失敗: {error}");
            return;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[Exclude] {Path} -> {NewName}.vrm にリネームしました");
    }
}
