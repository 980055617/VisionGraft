using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// シーンに焼かれた displayTrackIds を空（＝全 track 表示）に戻す。
//
// TestScene / TrialScene の両方に [0, 1] が焼かれていた。bundle_human が
// person + ball の 2 track なので当時はこれで足りていたが、bundle_train は 8 track あり、
// 列車は接近中 track 1 → 通過中 5 → 6 → 7 と ID が振り直される。
// [0, 1] 固定だと f=935 以降は動かない信号柱しか表示されず「追従しない」ように見える
// （2026-08-31 実機、docs/bundle-placement.md）。
//
// **全 track 表示が既定**（ユーザー確認済み。複数同時に出てよい）。
//
//   Unity.exe -batchmode -projectPath . -executeMethod DisplayTrackIdsReset.ClearAll -quit
public static class DisplayTrackIdsReset
{
    private static readonly string[] Scenes =
    {
        "Assets/Scenes/TestScene.unity",
        "Assets/Scenes/TrialScene.unity",
    };

    public static void ClearAll()
    {
        foreach (string path in Scenes)
        {
            Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            int changed = 0;
            foreach (StreamingStereoVideoPlayer p in Object.FindObjectsByType<StreamingStereoVideoPlayer>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (p.displayTrackIds != null && p.displayTrackIds.Length > 0)
                {
                    Debug.Log($"[TrackIds] {path}: {p.name} displayTrackIds=[{string.Join(",", p.displayTrackIds)}] -> []");
                    p.displayTrackIds = new int[0];
                    EditorUtility.SetDirty(p);
                    changed++;
                }

                // trackModelIndices も併せて外す。**全 track 表示にすると害になる。**
                // 焼かれていた [0→36, 1→39] は Animal の番号（LabradorDog / Lynx）で、
                // カテゴリが違う track では Clamp されて別物になる:
                //   Human(17 個) → 16、Else(7 個) → 6（＝機関車）
                // 実測での影響:
                //   bundle_human … ボール(track 1)が機関車になる（実機で 04_Soccer を手で
                //                  指定し直していたのはこれが理由）
                //   bundle_train … 信号柱(track 0)と接近中の列車(track 1)だけ機関車になり、
                //                  通過中の車両(track 5〜7)は野球ボールになる
                //   bundle_animal … 同じ動物が途中で犬から山猫に変わる（track 0→1 で ID が
                //                  振り直されるため）
                // モデル選択は実行時のピッカーで動画ごとに保存されるので、Inspector 側の
                // 固定指定は不要。
                if (p.trackModelIndices != null && p.trackModelIndices.Length > 0)
                {
                    foreach (var e in p.trackModelIndices)
                    {
                        Debug.Log($"[TrackIds] {path}: {p.name} trackModelIndices track={e.trackId} index={e.modelIndex} を削除");
                    }

                    p.trackModelIndices = new StreamingStereoVideoPlayer.TrackModelIndexOverride[0];
                    EditorUtility.SetDirty(p);
                    changed++;
                }

                if (changed == 0)
                {
                    Debug.Log($"[TrackIds] {path}: {p.name} は既に空です");
                }
            }

            if (changed > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                Debug.Log($"[TrackIds] saved {path} ({changed} 件)");
            }
        }
    }
}
