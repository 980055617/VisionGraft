using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

// 人のリグを Blender で直す試作（2026-10-23、ユーザー「猫と犬と人の rig をもう少し崩れないように blender でやりたい」）。
// 関門 B: 何も変えずに Blender を通した FBX（往復）を、元の prefab（17_Male_Meshy / 18_Female_Meshy）と同じ形の prefab にして、
// Humanoid の対応・材質・FK の姿勢・皮膚が元と同じかを確かめる。直した FBX も同じ手順で prefab にする。
//   - FBX の取り込みの設定（Humanoid・Avatar・倍率・軸・材質）を元の FBX から写す
//   - 元の prefab の各 Transform の局所の位置・回転・大きさを、同じパスの Transform に写す（作者の向きと大きさの調整を引き継ぐ）
//   - Animator は元と同じ設定（controller・root motion・culling）。Avatar は新しい FBX のもの
//   - 材質は元の SkinnedMeshRenderer のものを同じパスの描画の部品に当てる（Meshy の画像は名前が texture_0 で、名前で探させると
//     別のモデルの画像が当たる。2026-10-23 に犬で確かめた）
// Unity を閉じたバッチで -executeMethod HumanRigPilotTools.CreateRoundTripPrefabs。
public static class HumanRigPilotTools
{
    private const string LogTag = "[HUMANRIG]";

    // 試作の prefab の置き場所（Resources の外。APK に入らず、モデルの番号もずらさない）。2026-10-24 ユーザー「8.いいよ」で Resources から移した。
    private const string PilotFolder = "Assets/MeshyModels/Pilot";

    private sealed class Job
    {
        public string fbx;
        public string originalFbx;
        public string originalPrefab;
        public string outPrefab;
        public string name;
    }

    private static readonly Job[] RoundTripJobs =
    {
        new Job
        {
            fbx = "Assets/MeshyModels/HumanRig/man_roundtrip.fbx",
            originalFbx = "Assets/MeshyModels/Models/man/Meshy_AI_Open_Stance_biped_Character_output.fbx",
            originalPrefab = "Assets/Resources/Models/Human/17_Male_Meshy.prefab",
            outPrefab = PilotFolder + "/19_Male_Meshy_RT.prefab",
            name = "19_Male_Meshy_RT",
        },
        new Job
        {
            fbx = "Assets/MeshyModels/HumanRig/woman_roundtrip.fbx",
            originalFbx = "Assets/MeshyModels/Models/woman/Meshy_AI_Gray_Stillness_biped_Character_output.fbx",
            originalPrefab = "Assets/Resources/Models/Human/18_Female_Meshy.prefab",
            outPrefab = PilotFolder + "/20_Female_Meshy_RT.prefab",
            name = "20_Female_Meshy_RT",
        },
    };

    [MenuItem("Tools/VisionGraft/Human Rig Pilot/Create Round Trip Prefabs")]
    public static void CreateRoundTripPrefabs()
    {
        foreach (Job job in RoundTripJobs)
        {
            CreatePrefab(job.fbx, job.originalFbx, job.originalPrefab, job.outPrefab, job.name);
        }

        AssetDatabase.SaveAssets();
        ModelResourceIndexGenerator.Generate();
    }

    // 2026-10-24: 皮膚の作り直しの候補（Workflow human-rig-build の判定役の勝ち）。重みだけを変えた FBX（1 頂点 2 本以下）を、
    // 関門 B と同じ手順で prefab にする。名前は 90 番台にしてコミット済みの番号をずらさない（採否が決まるまでコミットしない）。
    //   90_Male_Meshy_hw2   男性 hw2（骨の鎖に沿って 2 本で設計した重み）。判定役が末端の骨に重みを戻した 28 群の版
    //   91_Female_Meshy_hw1 女性 hw1（作者の 4 本の形を 2 本で再現する当てはめ）
    //   92_Female_Meshy_hw2 女性 hw2（重みの群 22）
    private static readonly Job[] SkinCandidateJobs =
    {
        new Job
        {
            fbx = "Assets/MeshyModels/HumanRig/man_hw2_leaf.fbx",
            originalFbx = "Assets/MeshyModels/Models/man/Meshy_AI_Open_Stance_biped_Character_output.fbx",
            originalPrefab = "Assets/Resources/Models/Human/17_Male_Meshy.prefab",
            outPrefab = PilotFolder + "/90_Male_Meshy_hw2.prefab",
            name = "90_Male_Meshy_hw2",
        },
        new Job
        {
            fbx = "Assets/MeshyModels/HumanRig/woman_hw1.fbx",
            originalFbx = "Assets/MeshyModels/Models/woman/Meshy_AI_Gray_Stillness_biped_Character_output.fbx",
            originalPrefab = "Assets/Resources/Models/Human/18_Female_Meshy.prefab",
            outPrefab = PilotFolder + "/91_Female_Meshy_hw1.prefab",
            name = "91_Female_Meshy_hw1",
        },
        new Job
        {
            fbx = "Assets/MeshyModels/HumanRig/woman_hw2.fbx",
            originalFbx = "Assets/MeshyModels/Models/woman/Meshy_AI_Gray_Stillness_biped_Character_output.fbx",
            originalPrefab = "Assets/Resources/Models/Human/18_Female_Meshy.prefab",
            outPrefab = PilotFolder + "/92_Female_Meshy_hw2.prefab",
            name = "92_Female_Meshy_hw2",
        },
    };

    [MenuItem("Tools/VisionGraft/Human Rig Pilot/Create Skin Candidate Prefabs")]
    public static void CreateSkinCandidatePrefabs()
    {
        foreach (Job job in SkinCandidateJobs)
        {
            CreatePrefab(job.fbx, job.originalFbx, job.originalPrefab, job.outPrefab, job.name);
        }

        AssetDatabase.SaveAssets();
        ModelResourceIndexGenerator.Generate();
    }

    // 2026-10-24 夜: 新しい女性 2 体（Workflow human-rig-new-women の判定役の勝ちと、ゾンビの次点）。prefab は Resources の外（PilotFolder）に
    // 作るので一覧と番号は変わらない。バッチでは -humanPrefab <パス> で読む。
    //   93_Female_Meshy_Soccer_hw2  サッカーの女性 hw2（骨の鎖に沿って 2 本で設計した重み、勝ち）
    //   94_Female_Meshy_Zombie_hw1  ゾンビ hw1（作者の 4 本の形を 2 本で再現する当てはめ、勝ち）
    //   95_Female_Meshy_Zombie_hw2  ゾンビ hw2（次点。テレビでは同点、座る・前かがみでワンピースが割れる）
    private const string SoccerWomanFbx = "Assets/MeshyModels/Models/soccer_woman/Meshy_AI_Blue_Soccer_Uniform_P_biped_Character_output.fbx";
    private const string ZombieWomanFbx = "Assets/MeshyModels/Models/zombie/Meshy_AI_The_Forgotten_Doll_biped_Character_output.fbx";

    private static readonly Job[] NewWomenSkinJobs =
    {
        new Job
        {
            fbx = "Assets/MeshyModels/HumanRig/soccer_hw2.fbx",
            originalFbx = SoccerWomanFbx,
            originalPrefab = "Assets/Resources/Models/Human/19_Female_Meshy_Soccer.prefab",
            outPrefab = PilotFolder + "/93_Female_Meshy_Soccer_hw2.prefab",
            name = "93_Female_Meshy_Soccer_hw2",
        },
        new Job
        {
            fbx = "Assets/MeshyModels/HumanRig/zombie_hw1.fbx",
            originalFbx = ZombieWomanFbx,
            originalPrefab = "Assets/Resources/Models/Human/20_Female_Meshy_Zombie.prefab",
            outPrefab = PilotFolder + "/94_Female_Meshy_Zombie_hw1.prefab",
            name = "94_Female_Meshy_Zombie_hw1",
        },
        new Job
        {
            fbx = "Assets/MeshyModels/HumanRig/zombie_hw2.fbx",
            originalFbx = ZombieWomanFbx,
            originalPrefab = "Assets/Resources/Models/Human/20_Female_Meshy_Zombie.prefab",
            outPrefab = PilotFolder + "/95_Female_Meshy_Zombie_hw2.prefab",
            name = "95_Female_Meshy_Zombie_hw2",
        },
    };

    // バッチ: -executeMethod HumanRigPilotTools.CreateNewWomenSkinPrefabs（-quit 付き）
    [MenuItem("Tools/VisionGraft/Human Rig Pilot/Create New Women Skin Prefabs")]
    public static void CreateNewWomenSkinPrefabs()
    {
        if (!AssetDatabase.IsValidFolder(PilotFolder))
        {
            AssetDatabase.CreateFolder(Path.GetDirectoryName(PilotFolder).Replace('\\', '/'), Path.GetFileName(PilotFolder));
        }

        foreach (Job job in NewWomenSkinJobs)
        {
            CreatePrefab(job.fbx, job.originalFbx, job.originalPrefab, job.outPrefab, job.name);
            ReportSkinImportSettings(job.name, job.fbx, job.originalFbx);
        }

        AssetDatabase.SaveAssets();
    }

    // 2026-10-25: サッカーの女性の腕を上げた左脇の直し 2 案（Workflow finish-rigs-and-armpit。ユーザー 10/24「結局女性サッカーの人の手上げたところの
    // 左脇おかしくない？」で hw2 の勧めを取り下げた）。どちらも hw2 の重みから両袖・両脇だけを変えた 1 頂点 2 本の重みで、骨は元のまま。
    //   96_Female_Meshy_Soccer_ax1c  袖口の色の境目を上腕と一緒に固く動かし、伸びを袖の下側の青へ逃がす
    //   97_Female_Meshy_Soccer_ax2c  色の境目をまたぐ伸び・折れが小さくなるよう重みを当てはめ直す
    private static readonly Job[] SoccerArmpitJobs =
    {
        new Job
        {
            fbx = "Assets/MeshyModels/HumanRig/soccer_ax1c.fbx",
            originalFbx = SoccerWomanFbx,
            originalPrefab = "Assets/Resources/Models/Human/19_Female_Meshy_Soccer.prefab",
            outPrefab = PilotFolder + "/96_Female_Meshy_Soccer_ax1c.prefab",
            name = "96_Female_Meshy_Soccer_ax1c",
        },
        new Job
        {
            fbx = "Assets/MeshyModels/HumanRig/soccer_ax2c.fbx",
            originalFbx = SoccerWomanFbx,
            originalPrefab = "Assets/Resources/Models/Human/19_Female_Meshy_Soccer.prefab",
            outPrefab = PilotFolder + "/97_Female_Meshy_Soccer_ax2c.prefab",
            name = "97_Female_Meshy_Soccer_ax2c",
        },
    };

    // バッチ: -executeMethod HumanRigPilotTools.CreateSoccerArmpitPrefabs（-quit 付き）
    [MenuItem("Tools/VisionGraft/Human Rig Pilot/Create Soccer Armpit Prefabs")]
    public static void CreateSoccerArmpitPrefabs()
    {
        foreach (Job job in SoccerArmpitJobs)
        {
            CreatePrefab(job.fbx, job.originalFbx, job.originalPrefab, job.outPrefab, job.name);
            ReportSkinImportSettings(job.name, job.fbx, job.originalFbx);
        }

        AssetDatabase.SaveAssets();
    }

    // 2026-10-27: テレビの女性 18 の膝の関節を、すねの線の上へ動かした FBX（ユーザー「歩くときなんか脛めっちゃ後ろについてない？そのモデル？」）。
    // Meshy の自動のリグは膝（LeftLeg / RightLeg の頭）を y −0.0625（すねの真ん中 +0.085 より 14.7 cm 前、ワンピースの真ん中）に置いていて、
    // 膝を曲げるとすねが前の点を中心に回り後ろへ振れた。Blender で膝だけ y +0.07 へ動かした（高さ・左右・股・足首・重み・メッシュは同じ。
    // scratchpad validate/combined/legs/blender/knee_fix.py）。骨の基準の位置が変わるので CreatePrefab と 2 か所違う:
    //   (1) Avatar は元の Avatar から写さず、元の FBX と同じく Unity の自動の対応・T ポーズに任せる（ReportAvatarSkeleton で対応が同じか確かめる）
    //   (2) 元の prefab の Transform を写すのは、新しい FBX と元の FBX で局所の位置・回転が同じ骨だけ（動かした骨は新しい FBX の値のまま）
    //   98_Female_Meshy_knee     膝だけ y +0.07（太ももの骨が基準の姿勢で 10.5° 後ろへ傾き、動画に合わせる歩きでワンピースの前が前へふくらんだ）
    //   99_Female_Meshy_kneehip  膝と股を y +0.07（太ももの骨が基準の姿勢で垂直）
    private const string WomanFbx = "Assets/MeshyModels/Models/woman/Meshy_AI_Gray_Stillness_biped_Character_output.fbx";

    private static readonly Job[] WomanKneeJobs =
    {
        new Job
        {
            fbx = "Assets/MeshyModels/HumanRig/woman_knee.fbx",
            originalFbx = WomanFbx,
            originalPrefab = "Assets/Resources/Models/Human/18_Female_Meshy.prefab",
            outPrefab = PilotFolder + "/98_Female_Meshy_knee.prefab",
            name = "98_Female_Meshy_knee",
        },
        new Job
        {
            fbx = "Assets/MeshyModels/HumanRig/woman_knee_hip.fbx",
            originalFbx = WomanFbx,
            originalPrefab = "Assets/Resources/Models/Human/18_Female_Meshy.prefab",
            outPrefab = PilotFolder + "/99_Female_Meshy_kneehip.prefab",
            name = "99_Female_Meshy_kneehip",
        },
    };

    // バッチ: -executeMethod HumanRigPilotTools.CreateWomanKneePrefab（-quit 付き）
    [MenuItem("Tools/VisionGraft/Human Rig Pilot/Create Woman Knee Prefab")]
    public static void CreateWomanKneePrefab()
    {
        if (!AssetDatabase.IsValidFolder(PilotFolder))
        {
            AssetDatabase.CreateFolder(Path.GetDirectoryName(PilotFolder).Replace('\\', '/'), Path.GetFileName(PilotFolder));
        }

        foreach (Job job in WomanKneeJobs)
        {
            if (AssetImporter.GetAtPath(job.fbx) == null)
            {
                Debug.LogWarning($"{LogTag} {job.name}: FBX が無いので飛ばす {job.fbx}");
                continue;
            }

            CopyHumanImportSettings(job.fbx, job.originalFbx, true);
            ReportAvatarSkeleton(job.name, job.originalFbx, job.fbx);
            CreatePrefab(job.fbx, job.originalFbx, job.originalPrefab, job.outPrefab, job.name, true);
            ReportSkinImportSettings(job.name, job.fbx, job.originalFbx);
        }

        AssetDatabase.SaveAssets();
    }

    // 2026-10-27: 男性 17 の口を開けられるようにした FBX（ユーザー「男性の口パクについては，今回の被験者実験だけの合わせなので
    // bundleデータ追加で増やさないようにして，また，モデルに関してはblenderでうまく調整して動かせるようにして」）。
    // hw2（man_hw2_leaf.fbx）の唇の間のくぼみの辺でメッシュを切り、頭の子にあごの骨 Jaw（耳の前の蝶番）とその子の重みの無い JawTip を足し、
    // 口の中（唇の内側・歯・舌・袋）を足した（scratchpad mouth17/tools/build_mouth3.py、骨・元の頂点・UV・頭のまわり以外の重みは hw2 と同じ）。
    // 材質は元の 1 つのまま（口の中は元の画像の髪の黒・靴の白・唇の色を UV で指す）。メッシュの取り込みの設定（溶接・法線など）は hw2 の FBX から写し、
    // Humanoid の対応は元の Avatar から写す（Humanoid の Jaw は headfront のまま、新しい Jaw は対応に入らない）。
    //   89_Male_Meshy_mouth
    private const string MaleHw2Fbx = "Assets/MeshyModels/HumanRig/man_hw2_leaf.fbx";

    private static readonly Job[] MaleMouthJobs =
    {
        new Job
        {
            fbx = "Assets/MeshyModels/HumanRig/man_mouth.fbx",
            originalFbx = "Assets/MeshyModels/Models/man/Meshy_AI_Open_Stance_biped_Character_output.fbx",
            originalPrefab = "Assets/Resources/Models/Human/17_Male_Meshy.prefab",
            outPrefab = PilotFolder + "/89_Male_Meshy_mouth.prefab",
            name = "89_Male_Meshy_mouth",
        },
    };

    // 2026-10-27 診断（採らない）: 口あり 89 で、口を閉じている瞬間に首の襟元の日なたが影になった原因を部品で切り分ける。
    //   88_Male_Meshy_mouth_noint  口の中（唇の内側・歯・袋・舌）を全部外した版（切った唇・あごの骨・重みは 89 と同じ）
    //   87_Male_Meshy_mouth_nobag  袋だけ外した版
    private static readonly Job[] MaleMouthDiagJobs =
    {
        new Job
        {
            fbx = "Assets/MeshyModels/HumanRig/man_mouth_noint.fbx",
            originalFbx = "Assets/MeshyModels/Models/man/Meshy_AI_Open_Stance_biped_Character_output.fbx",
            originalPrefab = "Assets/Resources/Models/Human/17_Male_Meshy.prefab",
            outPrefab = PilotFolder + "/88_Male_Meshy_mouth_noint.prefab",
            name = "88_Male_Meshy_mouth_noint",
        },
        new Job
        {
            fbx = "Assets/MeshyModels/HumanRig/man_mouth_nobag.fbx",
            originalFbx = "Assets/MeshyModels/Models/man/Meshy_AI_Open_Stance_biped_Character_output.fbx",
            originalPrefab = "Assets/Resources/Models/Human/17_Male_Meshy.prefab",
            outPrefab = PilotFolder + "/87_Male_Meshy_mouth_nobag.prefab",
            name = "87_Male_Meshy_mouth_nobag",
        },
    };

    // バッチ: -executeMethod HumanRigPilotTools.CreateMaleMouthDiagPrefabs（-quit 付き）
    public static void CreateMaleMouthDiagPrefabs()
    {
        CreateMouthPrefabs(MaleMouthDiagJobs);
    }

    // バッチ: -executeMethod HumanRigPilotTools.ReportMouthRendererSettings（-quit 付き）。17・89・88 の描画の部品の設定（範囲・影・更新）を並べる。
    public static void ReportMouthRendererSettings()
    {
        string[] paths =
        {
            "Assets/Resources/Models/Human/17_Male_Meshy.prefab",
            PilotFolder + "/89_Male_Meshy_mouth.prefab",
            PilotFolder + "/88_Male_Meshy_mouth_noint.prefab",
        };
        foreach (string path in paths)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                Debug.LogError($"{LogTag} renderer settings: missing {path}");
                continue;
            }

            GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                foreach (SkinnedMeshRenderer r in inst.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    Mesh m = r.sharedMesh;
                    Debug.Log($"{LogTag} renderer settings {prefab.name}: {r.name} localBounds c={r.localBounds.center.ToString("F4")} e={r.localBounds.extents.ToString("F4")} " +
                              $"bounds c={r.bounds.center.ToString("F4")} e={r.bounds.extents.ToString("F4")} meshBounds c={(m != null ? m.bounds.center.ToString("F4") : "-")} " +
                              $"e={(m != null ? m.bounds.extents.ToString("F4") : "-")} rootBone={(r.rootBone != null ? r.rootBone.name : "-")} quality={r.quality} " +
                              $"offscreen={r.updateWhenOffscreen} cast={r.shadowCastingMode} receive={r.receiveShadows} bones={r.bones.Length} " +
                              $"lossyScale={r.transform.lossyScale.ToString("F4")} probes={r.lightProbeUsage}/{r.reflectionProbeUsage} motion={r.skinnedMotionVectors}");
                }
            }
            finally
            {
                Object.DestroyImmediate(inst);
            }
        }
    }

    // バッチ: -executeMethod HumanRigPilotTools.CreateMaleMouthPrefab（-quit 付き）
    [MenuItem("Tools/VisionGraft/Human Rig Pilot/Create Male Mouth Prefab")]
    public static void CreateMaleMouthPrefab()
    {
        CreateMouthPrefabs(MaleMouthJobs);
    }

    private static void CreateMouthPrefabs(Job[] jobs)
    {
        if (!AssetDatabase.IsValidFolder(PilotFolder))
        {
            AssetDatabase.CreateFolder(Path.GetDirectoryName(PilotFolder).Replace('\\', '/'), Path.GetFileName(PilotFolder));
        }

        foreach (Job job in jobs)
        {
            if (AssetImporter.GetAtPath(job.fbx) == null)
            {
                Debug.LogWarning($"{LogTag} {job.name}: FBX が無いので飛ばす {job.fbx}");
                continue;
            }

            CopyMeshImportSettings(job.fbx, MaleHw2Fbx);
            CreatePrefab(job.fbx, job.originalFbx, job.originalPrefab, job.outPrefab, job.name);
            AssignMouthInnerMaterial(job.name, job.outPrefab);
            ReportSkinImportSettings(job.name, job.fbx, MaleHw2Fbx);
            ReportMouthRig(job.name, job.outPrefab, job.originalPrefab);
        }

        AssetDatabase.SaveAssets();
    }

    // 2026-10-27: 口の中（Blender の材質の枠 1）に、体の材質の複製で影を落とさないもの（ShadowCaster のパスを切る）を当てる。
    // 口の中は皮膚の内側にあって外からは見えないが、影の計算に入ると、口を閉じていても首の襟元の日なたが変わった
    // （口の中を外した版は今の 17 と差 0、Docs/tmp/survey_20261025/README.md）。CreatePrefab は元の 17 の材質 1 つだけを写す。
    private const string MouthInnerMaterialPath = PilotFolder + "/MaleMouth_InnerNoShadow.mat";

    private static void AssignMouthInnerMaterial(string name, string prefabPath)
    {
        GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            bool changed = false;
            foreach (SkinnedMeshRenderer r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (r.sharedMesh == null || r.sharedMesh.subMeshCount < 2 || r.sharedMaterials.Length == 0 || r.sharedMaterials[0] == null)
                {
                    continue;
                }

                Material body = r.sharedMaterials[0];
                Material inner = AssetDatabase.LoadAssetAtPath<Material>(MouthInnerMaterialPath);
                if (inner == null)
                {
                    inner = new Material(body) { name = Path.GetFileNameWithoutExtension(MouthInnerMaterialPath) };
                    AssetDatabase.CreateAsset(inner, MouthInnerMaterialPath);
                }
                else
                {
                    inner.shader = body.shader;
                    inner.CopyPropertiesFromMaterial(body);
                }

                // URP Lit の _CastShadows（材質の画面で切り替えるとパスの有効・無効が付け直される）も 0 にそろえる
                if (inner.HasProperty("_CastShadows"))
                {
                    inner.SetFloat("_CastShadows", 0f);
                }

                inner.SetShaderPassEnabled("ShadowCaster", false);
                EditorUtility.SetDirty(inner);
                var mats = new Material[r.sharedMesh.subMeshCount];
                mats[0] = body;
                for (int i = 1; i < mats.Length; i++)
                {
                    mats[i] = inner;
                }

                r.sharedMaterials = mats;
                changed = true;
                Debug.Log($"{LogTag} {name}: mouth inner material {MouthInnerMaterialPath} on {mats.Length - 1} sub-mesh(es) of {r.name}, " +
                          $"shadowCaster={inner.GetShaderPassEnabled("ShadowCaster")} body={body.name}");
            }

            if (changed)
            {
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // メッシュの取り込みの設定（溶接・法線・接線・圧縮・皮膚）を写す。CopyHumanImportSettings は Humanoid・倍率・材質だけ写す。
    private static void CopyMeshImportSettings(string path, string like)
    {
        var target = AssetImporter.GetAtPath(path) as ModelImporter;
        var source = AssetImporter.GetAtPath(like) as ModelImporter;
        if (target == null || source == null)
        {
            Debug.LogError($"{LogTag} importer missing: {path} / {like}");
            return;
        }

        target.weldVertices = source.weldVertices;
        target.importNormals = source.importNormals;
        target.normalCalculationMode = source.normalCalculationMode;
        target.normalSmoothingAngle = source.normalSmoothingAngle;
        target.normalSmoothingSource = source.normalSmoothingSource;
        target.importTangents = source.importTangents;
        target.optimizeMeshPolygons = source.optimizeMeshPolygons;
        target.optimizeMeshVertices = source.optimizeMeshVertices;
        target.meshCompression = source.meshCompression;
        target.isReadable = source.isReadable;
        target.keepQuads = source.keepQuads;
        target.indexFormat = source.indexFormat;
        target.skinWeights = source.skinWeights;
        target.maxBonesPerVertex = source.maxBonesPerVertex;
        target.minBoneWeight = source.minBoneWeight;
        target.optimizeBones = source.optimizeBones;
        target.importBlendShapes = source.importBlendShapes;
        target.importVisibility = source.importVisibility;
        target.importCameras = source.importCameras;
        target.importLights = source.importLights;
        target.preserveHierarchy = source.preserveHierarchy;
        target.sortHierarchyByName = source.sortHierarchyByName;
        target.generateSecondaryUV = source.generateSecondaryUV;
        target.swapUVChannels = source.swapUVChannels;
        target.addCollider = source.addCollider;
        target.SaveAndReimport();
        Debug.Log($"{LogTag} mesh import settings copied {like} -> {path} weld={target.weldVertices} normals={target.importNormals} " +
                  $"tangents={target.importTangents} compression={target.meshCompression} keepQuads={target.keepQuads}");
    }

    // 口の骨と皮膚の確かめ: 頭の子に Jaw・JawTip・head_end があるか、Humanoid の Jaw の対応（headfront のまま）、1 頂点の骨の本数、
    // あごを 12° 開けたときに「閉じた形では同じ位置で、開くと離れる頂点の組」（= 切った唇の上下。取り込みの溶接でくっついていない印）の数、
    // 閉じた形の皮膚が元の 17 と同じか（頂点の番号は違うので、元の各頂点の一番近い頂点までの距離）。
    private static void ReportMouthRig(string name, string prefabPath, string originalPrefab)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        GameObject orig = AssetDatabase.LoadAssetAtPath<GameObject>(originalPrefab);
        if (prefab == null || orig == null)
        {
            Debug.LogError($"{LogTag} {name}: mouth check: prefab missing {prefabPath} / {originalPrefab}");
            return;
        }

        GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        GameObject origInst = (GameObject)PrefabUtility.InstantiatePrefab(orig);
        try
        {
            Animator a = inst.GetComponentInChildren<Animator>(true);
            Transform head = a != null && a.isHuman ? a.GetBoneTransform(HumanBodyBones.Head) : null;
            Transform humanJaw = a != null && a.isHuman ? a.GetBoneTransform(HumanBodyBones.Jaw) : null;
            Transform jaw = head != null ? head.Find("Jaw") : null;
            Transform tip = jaw != null ? jaw.Find("JawTip") : null;
            Transform top = head != null ? head.Find("head_end") : null;
            SkinnedMeshRenderer smr = inst.GetComponentInChildren<SkinnedMeshRenderer>(true);
            SkinnedMeshRenderer osmr = origInst.GetComponentInChildren<SkinnedMeshRenderer>(true);
            Debug.Log($"{LogTag} {name}: mouth rig head={(head != null ? head.name : "-")} jaw={(jaw != null ? jaw.name : "-")} tip={(tip != null ? tip.name : "-")} " +
                      $"top={(top != null ? top.name : "-")} humanoidJaw={(humanJaw != null ? humanJaw.name : "-")} jawInSkin={(smr != null && jaw != null && System.Array.IndexOf(smr.bones, jaw) >= 0)}");
            if (smr == null || osmr == null || jaw == null || tip == null || top == null)
            {
                return;
            }

            Mesh mesh = smr.sharedMesh;
            byte[] perVertex = mesh.GetBonesPerVertex().ToArray();
            int maxBones = 0;
            foreach (byte b in perVertex)
            {
                maxBones = Mathf.Max(maxBones, b);
            }

            Debug.Log($"{LogTag} {name}: mouth mesh verts={mesh.vertexCount} (orig {osmr.sharedMesh.vertexCount}) subMeshes={mesh.subMeshCount} " +
                      $"bones={smr.bones.Length} maxBonesPerVertex={maxBones} materials={smr.sharedMaterials.Length}");

            // world の座標で比べる（BakeMesh は smr の倍率込みで焼き = useScale true、smr の localToWorld で world へ。
            // false で焼いて localToWorld を掛けると倍率が 2 回掛かる。この prefab はメッシュの部品に 100 倍が掛かっている）。
            // しきい値は人の背の高さ（world の bounds）に対する割合で決める（1.72 m の人で 3 mm・2 mm）。
            float height = smr.bounds.size.y;
            float unit = Mathf.Max(1e-6f, height / 1.72f);
            var rest = new Mesh();
            smr.BakeMesh(rest, true);
            Vector3[] vr = ToWorld(smr, rest.vertices);
            Quaternion bind = jaw.localRotation;
            Vector3 up = top.position - head.position;
            Vector3 jawDir = tip.position - jaw.position;
            Vector3 axis = Vector3.Cross(up, jawDir).normalized;
            Vector3 turned = Quaternion.AngleAxis(10f, axis) * jawDir;
            if (Vector3.Dot(turned - jawDir, up) > 0f)
            {
                axis = -axis;
            }

            // Jaw と Jaw_NN（開く角度の NN%）を、それぞれの割合で回す（実行時の HumanLipSync と同じ）
            var jawBones = new List<Transform>();
            var jawFractions = new List<float>();
            var jawBinds = new List<Quaternion>();
            for (int c = 0; c < head.childCount; c++)
            {
                Transform child = head.GetChild(c);
                if (StreamingStereoVideoPlayer.TryParseJawFraction(child.name, out float frac))
                {
                    jawBones.Add(child);
                    jawFractions.Add(frac);
                    jawBinds.Add(child.localRotation);
                }
            }

            for (int c = 0; c < jawBones.Count; c++)
            {
                jawBones[c].rotation = Quaternion.AngleAxis(12f * jawFractions[c], axis) * jawBones[c].rotation;
            }

            Vector3 tipOpen = tip.position;
            var open = new Mesh();
            smr.BakeMesh(open, true);
            for (int c = 0; c < jawBones.Count; c++)
            {
                jawBones[c].localRotation = jawBinds[c];
            }

            jaw.localRotation = bind;
            Vector3[] vo = ToWorld(smr, open.vertices);
            int moved = 0;
            float maxMove = 0f;
            for (int i = 0; i < vr.Length; i++)
            {
                float d = (vo[i] - vr[i]).magnitude;
                maxMove = Mathf.Max(maxMove, d);
                if (d > 0.003f * unit)
                {
                    moved++;
                }
            }

            // 閉じた形で同じ位置（0.01 mm 相当以内）の頂点の組のうち、開いた形で 2 mm 相当以上離れる組。格子に分けて近い点だけ比べる。
            var cells = new Dictionary<Vector3Int, List<int>>();
            float cell = 0.002f * unit;
            for (int i = 0; i < vr.Length; i++)
            {
                Vector3Int k = Vector3Int.FloorToInt(vr[i] / cell);
                if (!cells.TryGetValue(k, out List<int> list))
                {
                    list = new List<int>();
                    cells[k] = list;
                }

                list.Add(i);
            }

            int pairs = 0;
            float same2 = (0.00001f * unit) * (0.00001f * unit);
            float apart2 = (0.002f * unit) * (0.002f * unit);
            foreach (var kv in cells)
            {
                List<int> list = kv.Value;
                for (int p = 0; p < list.Count; p++)
                {
                    for (int q = p + 1; q < list.Count; q++)
                    {
                        int i = list[p];
                        int j = list[q];
                        if ((vr[i] - vr[j]).sqrMagnitude < same2 && (vo[i] - vo[j]).sqrMagnitude > apart2)
                        {
                            pairs++;
                        }
                    }
                }
            }

            // 閉じた形の皮膚が元の 17 と同じか: 元の各頂点（world）の一番近い新しい頂点（world）までの距離の最大
            var origBaked = new Mesh();
            osmr.BakeMesh(origBaked, true);
            Vector3[] vOrig = ToWorld(osmr, origBaked.vertices);
            var newCells = new Dictionary<Vector3Int, List<Vector3>>();
            for (int i = 0; i < vr.Length; i++)
            {
                Vector3Int k = Vector3Int.FloorToInt(vr[i] / cell);
                if (!newCells.TryGetValue(k, out List<Vector3> list))
                {
                    list = new List<Vector3>();
                    newCells[k] = list;
                }

                list.Add(vr[i]);
            }

            float worst = 0f;
            for (int i = 0; i < vOrig.Length; i++)
            {
                Vector3 w = vOrig[i];
                Vector3Int k = Vector3Int.FloorToInt(w / cell);
                float best = float.MaxValue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (!newCells.TryGetValue(k + new Vector3Int(dx, dy, dz), out List<Vector3> list))
                            {
                                continue;
                            }

                            foreach (Vector3 c in list)
                            {
                                best = Mathf.Min(best, (c - w).sqrMagnitude);
                            }
                        }
                    }
                }

                worst = Mathf.Max(worst, best < float.MaxValue ? Mathf.Sqrt(best) : cell * 3f);
            }

            // 動きの大きい頂点 5 つ（位置は頭からの差、1.72 m 相当の mm）と重み
            var order = new List<int>();
            for (int i = 0; i < vr.Length; i++)
            {
                order.Add(i);
            }

            order.Sort((x, y) => (vo[y] - vr[y]).sqrMagnitude.CompareTo((vo[x] - vr[x]).sqrMagnitude));
            BoneWeight[] bw = mesh.boneWeights;
            for (int n = 0; n < Mathf.Min(5, order.Count); n++)
            {
                int i = order[n];
                BoneWeight w = bw[i];
                string bn(int bi) => bi >= 0 && bi < smr.bones.Length && smr.bones[bi] != null ? smr.bones[bi].name : "-";
                Debug.Log($"{LogTag} {name}: mouth top move #{n} v={i} move={(vo[i] - vr[i]).magnitude / unit * 1000f:F1}mm " +
                          $"rest-head={((vr[i] - head.position) / unit * 1000f).ToString("F1")}mm open-head={((vo[i] - head.position) / unit * 1000f).ToString("F1")}mm " +
                          $"w={bn(w.boneIndex0)}:{w.weight0:F3} {bn(w.boneIndex1)}:{w.weight1:F3} {bn(w.boneIndex2)}:{w.weight2:F3} {bn(w.boneIndex3)}:{w.weight3:F3}");
            }

            // 頭を首に対して傾けても、閉じた形が元の 17 と同じか（頭の分の合計が元と同じなら同じ。2 本へ減らす規則で頭の分を変えると違う）。
            // 両方の頭を、首に対して横の軸まわりに 15°、縦の軸まわりに 10° 回して焼き、元の各頂点の一番近い頂点までの距離の最大を見る。
            float tiltWorst = TiltedClosedSkinDifference(inst, origInst, smr, osmr, cell);
            Debug.Log($"{LogTag} {name}: mouth closed with the head tilted 15deg/10deg vs orig 17: maxNearest={tiltWorst / unit * 1000f:F4}mm jawBones={jawBones.Count}");
            Debug.Log($"{LogTag} {name}: mouth open 12deg: height={height:F3}m jawLen={jawDir.magnitude / unit:F4}m(1.72m 相当) " +
                      $"tipMoved={(tipOpen - tip.position).magnitude / unit * 1000f:F1}mm maxMove={maxMove / unit * 1000f:F1}mm moved>3mm={moved} " +
                      $"ripPairs(separate>2mm)={pairs} closedSkinVsOrig17 maxNearest={worst / unit * 1000f:F4}mm (orig verts {vOrig.Length})");
            Object.DestroyImmediate(rest);
            Object.DestroyImmediate(open);
            Object.DestroyImmediate(origBaked);
        }
        finally
        {
            Object.DestroyImmediate(inst);
            Object.DestroyImmediate(origInst);
        }
    }

    private static float TiltedClosedSkinDifference(GameObject inst, GameObject origInst, SkinnedMeshRenderer smr, SkinnedMeshRenderer osmr, float cell)
    {
        Animator a = inst.GetComponentInChildren<Animator>(true);
        Animator oa = origInst.GetComponentInChildren<Animator>(true);
        Transform h = a.GetBoneTransform(HumanBodyBones.Head);
        Transform oh = oa.GetBoneTransform(HumanBodyBones.Head);
        Quaternion hb = h.localRotation;
        Quaternion ohb = oh.localRotation;
        Quaternion tilt = Quaternion.AngleAxis(15f, h.parent.right) * Quaternion.AngleAxis(10f, h.parent.up);
        Quaternion otilt = Quaternion.AngleAxis(15f, oh.parent.right) * Quaternion.AngleAxis(10f, oh.parent.up);
        h.rotation = tilt * h.rotation;
        oh.rotation = otilt * oh.rotation;
        var mNew = new Mesh();
        var mOrig = new Mesh();
        smr.BakeMesh(mNew, true);
        osmr.BakeMesh(mOrig, true);
        h.localRotation = hb;
        oh.localRotation = ohb;
        Vector3[] vn = ToWorld(smr, mNew.vertices);
        Vector3[] vo = ToWorld(osmr, mOrig.vertices);
        Object.DestroyImmediate(mNew);
        Object.DestroyImmediate(mOrig);
        var cells = new Dictionary<Vector3Int, List<Vector3>>();
        foreach (Vector3 w in vn)
        {
            Vector3Int k = Vector3Int.FloorToInt(w / cell);
            if (!cells.TryGetValue(k, out List<Vector3> list))
            {
                list = new List<Vector3>();
                cells[k] = list;
            }

            list.Add(w);
        }

        float worst = 0f;
        foreach (Vector3 w in vo)
        {
            Vector3Int k = Vector3Int.FloorToInt(w / cell);
            float best = float.MaxValue;
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (cells.TryGetValue(k + new Vector3Int(dx, dy, dz), out List<Vector3> list))
                        {
                            foreach (Vector3 c in list)
                            {
                                best = Mathf.Min(best, (c - w).sqrMagnitude);
                            }
                        }
                    }
                }
            }

            worst = Mathf.Max(worst, best < float.MaxValue ? Mathf.Sqrt(best) : cell * 3f);
        }

        return worst;
    }

    private static Vector3[] ToWorld(SkinnedMeshRenderer smr, Vector3[] local)
    {
        var world = new Vector3[local.Length];
        Matrix4x4 m = smr.transform.localToWorldMatrix;
        for (int i = 0; i < local.Length; i++)
        {
            world[i] = m.MultiplyPoint3x4(local[i]);
        }

        return world;
    }

    // CopyHumanImportSettings が写さない皮膚の取り込みの設定が元と同じか（新しい FBX は既定の値で取り込まれる）。
    private static void ReportSkinImportSettings(string name, string path, string like)
    {
        var target = AssetImporter.GetAtPath(path) as ModelImporter;
        var source = AssetImporter.GetAtPath(like) as ModelImporter;
        if (target == null || source == null)
        {
            Debug.LogError($"{LogTag} {name}: importer missing {path} / {like}");
            return;
        }

        bool same = target.optimizeBones == source.optimizeBones && target.maxBonesPerVertex == source.maxBonesPerVertex &&
                    Mathf.Approximately(target.minBoneWeight, source.minBoneWeight) && target.weldVertices == source.weldVertices &&
                    target.skinWeights == source.skinWeights;
        Debug.Log($"{LogTag} {name}: import settings same={same} optimizeBones={target.optimizeBones}/{source.optimizeBones} " +
                  $"maxBonesPerVertex={target.maxBonesPerVertex}/{source.maxBonesPerVertex} minBoneWeight={target.minBoneWeight}/{source.minBoneWeight} " +
                  $"weldVertices={target.weldVertices}/{source.weldVertices} skinWeights={target.skinWeights}/{source.skinWeights}");
    }

    // 2026-10-24: 男性は hw2 を採った（ユーザー「5.いいよ」、「7.そうですね」= 元のモデルに上書きし名前は変えない。参加者はまだいない）。
    // 17_Male_Meshy.prefab の中身を、Unity で確かめた候補 90_Male_Meshy_hw2.prefab と同じにする（.meta と GUID は 17 のまま、名前の
    // override だけ 17_Male_Meshy）。CreatePrefab で 17 を入力と出力の両方にすると、保存した瞬間に元の側のインスタンスも新しい中身に
    // なり比べる相手が無くなるので、確かめた prefab の文面を写す。
    private const string AdoptedMalePrefab = "Assets/Resources/Models/Human/17_Male_Meshy.prefab";
    private const string AdoptedMaleSource = "Assets/Resources/Models/Human/90_Male_Meshy_hw2.prefab";
    private const string AuthorMalePrefab = "Assets/MeshyModels/Prefabs/man_run.prefab";

    // 2026-10-24 ユーザー「8.いいよ」: 確かめが済んだ往復の 2 つは消し（ごみ箱へ）、残りの試作 5 つは PilotFolder へ移す。
    // 往復の FBX（HumanRig/*_roundtrip.fbx）は消さない（消すことは聞いていない）。
    private static readonly string[] PilotPrefabsToTrash =
    {
        "Assets/Resources/Models/Human/19_Male_Meshy_RT.prefab",
        "Assets/Resources/Models/Human/20_Female_Meshy_RT.prefab",
    };

    private static readonly string[] PilotPrefabsToMove =
    {
        "Assets/Resources/Models/Animal/54_MeshyDogSmal.prefab",
        "Assets/Resources/Models/Animal/55_MeshyCatSmal.prefab",
        "Assets/Resources/Models/Human/90_Male_Meshy_hw2.prefab",
        "Assets/Resources/Models/Human/91_Female_Meshy_hw1.prefab",
        "Assets/Resources/Models/Human/92_Female_Meshy_hw2.prefab",
    };

    // 2026-10-25: サッカーの女性は脇の直しの案2（ax2c）を採った（ユーザー「サッカーの女性の左脇は案2で全然いいね」「３．案２」）。
    // 男性と同じく、19_Female_Meshy_Soccer.prefab の中身を Unity で確かめた候補 97_Female_Meshy_Soccer_ax2c.prefab と同じにする
    // （.meta と GUID は 19 のまま、名前の override だけ 19_Female_Meshy_Soccer）。
    private const string AdoptedSoccerPrefab = "Assets/Resources/Models/Human/19_Female_Meshy_Soccer.prefab";
    private const string AdoptedSoccerSource = PilotFolder + "/97_Female_Meshy_Soccer_ax2c.prefab";
    private const string AuthorSoccerPrefab = "Assets/MeshyModels/Prefabs/soccer_woman_run.prefab";

    // バッチ: -executeMethod HumanRigPilotTools.AdoptSoccerAx2c（-quit 付き）
    [MenuItem("Tools/VisionGraft/Human Rig Pilot/Adopt Soccer ax2c As 19_Female_Meshy_Soccer")]
    public static void AdoptSoccerAx2c()
    {
        string guidBefore = AssetDatabase.AssetPathToGUID(AdoptedSoccerPrefab);
        if (!File.Exists(AdoptedSoccerSource))
        {
            Debug.LogError($"{LogTag} adopt: 候補の prefab が無い {AdoptedSoccerSource}");
            return;
        }

        string text = File.ReadAllText(AdoptedSoccerSource);
        var nameLine = new Regex(@"(?m)^(\s+value: )97_Female_Meshy_Soccer_ax2c(\r?)$");
        int hits = nameLine.Matches(text).Count;
        if (hits != 1)
        {
            Debug.LogError($"{LogTag} adopt: 名前の override が 1 行でない（{hits} 行）。写さない");
            return;
        }

        File.WriteAllText(AdoptedSoccerPrefab, nameLine.Replace(text, "${1}19_Female_Meshy_Soccer$2"), new UTF8Encoding(false));
        AssetDatabase.ImportAsset(AdoptedSoccerPrefab, ImportAssetOptions.ForceUpdate);
        string guidAfter = AssetDatabase.AssetPathToGUID(AdoptedSoccerPrefab);

        GameObject adopted = AssetDatabase.LoadAssetAtPath<GameObject>(AdoptedSoccerPrefab);
        GameObject candidate = AssetDatabase.LoadAssetAtPath<GameObject>(AdoptedSoccerSource);
        GameObject author = AssetDatabase.LoadAssetAtPath<GameObject>(AuthorSoccerPrefab);
        if (adopted == null || candidate == null || author == null)
        {
            Debug.LogError($"{LogTag} adopt: 読めない adopted={adopted != null} candidate={candidate != null} author={author != null}");
            return;
        }

        GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(adopted);
        Debug.Log($"{LogTag} adopt: {AdoptedSoccerPrefab} guid {guidBefore} -> {guidAfter} same={guidAfter == guidBefore} name={adopted.name} " +
                  $"source={(source != null ? AssetDatabase.GetAssetPath(source) : "-")}");

        GameObject adoptedInst = (GameObject)PrefabUtility.InstantiatePrefab(adopted);
        GameObject candidateInst = (GameObject)PrefabUtility.InstantiatePrefab(candidate);
        GameObject authorInst = (GameObject)PrefabUtility.InstantiatePrefab(author);
        try
        {
            Report("19_Female_Meshy_Soccer vs 97_Female_Meshy_Soccer_ax2c", candidateInst, adoptedInst);
            Report("19_Female_Meshy_Soccer vs soccer_woman_run", authorInst, adoptedInst);
            Animator a = adoptedInst.GetComponentInChildren<Animator>(true);
            Debug.Log($"{LogTag} adopt: isHuman={(a != null && a.isHuman)} avatarValid={(a != null && a.avatar != null && a.avatar.isValid)} " +
                      $"controller={(a != null && a.runtimeAnimatorController != null ? a.runtimeAnimatorController.name : "-")}");
        }
        finally
        {
            Object.DestroyImmediate(adoptedInst);
            Object.DestroyImmediate(candidateInst);
            Object.DestroyImmediate(authorInst);
        }

        AssetDatabase.SaveAssets();
        ModelResourceIndexGenerator.Generate();
    }

    // バッチ: -executeMethod HumanRigPilotTools.AdoptMaleHw2AndTidyPilots（-quit 付き）。写す → 確かめる → 片付ける → 一覧を作り直す。
    public static void AdoptMaleHw2AndTidyPilots()
    {
        if (AdoptMaleHw2())
        {
            TidyPilotPrefabs();
        }
    }

    [MenuItem("Tools/VisionGraft/Human Rig Pilot/Adopt Male hw2 As 17_Male_Meshy")]
    public static void AdoptMaleHw2Menu()
    {
        AdoptMaleHw2();
    }

    private static bool AdoptMaleHw2()
    {
        string guidBefore = AssetDatabase.AssetPathToGUID(AdoptedMalePrefab);
        if (!File.Exists(AdoptedMaleSource))
        {
            Debug.LogError($"{LogTag} adopt: 候補の prefab が無い {AdoptedMaleSource}");
            return false;
        }

        string text = File.ReadAllText(AdoptedMaleSource);
        var nameLine = new Regex(@"(?m)^(\s+value: )90_Male_Meshy_hw2(\r?)$");
        int hits = nameLine.Matches(text).Count;
        if (hits != 1)
        {
            Debug.LogError($"{LogTag} adopt: 名前の override が 1 行でない（{hits} 行）。写さない");
            return false;
        }

        File.WriteAllText(AdoptedMalePrefab, nameLine.Replace(text, "${1}17_Male_Meshy$2"), new UTF8Encoding(false));
        AssetDatabase.ImportAsset(AdoptedMalePrefab, ImportAssetOptions.ForceUpdate);
        string guidAfter = AssetDatabase.AssetPathToGUID(AdoptedMalePrefab);

        GameObject adopted = AssetDatabase.LoadAssetAtPath<GameObject>(AdoptedMalePrefab);
        GameObject candidate = AssetDatabase.LoadAssetAtPath<GameObject>(AdoptedMaleSource);
        GameObject author = AssetDatabase.LoadAssetAtPath<GameObject>(AuthorMalePrefab);
        if (adopted == null || candidate == null || author == null)
        {
            Debug.LogError($"{LogTag} adopt: 読めない adopted={adopted != null} candidate={candidate != null} author={author != null}");
            return false;
        }

        GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(adopted);
        Debug.Log($"{LogTag} adopt: {AdoptedMalePrefab} guid {guidBefore} -> {guidAfter} name={adopted.name} " +
                  $"source={(source != null ? AssetDatabase.GetAssetPath(source) : "-")}");

        GameObject adoptedInst = (GameObject)PrefabUtility.InstantiatePrefab(adopted);
        GameObject candidateInst = (GameObject)PrefabUtility.InstantiatePrefab(candidate);
        GameObject authorInst = (GameObject)PrefabUtility.InstantiatePrefab(author);
        try
        {
            // 確かめた候補と同じか（差 0 のはず）と、作者の prefab と Humanoid の対応が同じか（bind の差は作者の向きの調整の分）。
            Report("17_Male_Meshy vs 90_Male_Meshy_hw2", candidateInst, adoptedInst);
            Report("17_Male_Meshy vs man_run", authorInst, adoptedInst);
            Animator a = adoptedInst.GetComponentInChildren<Animator>(true);
            Debug.Log($"{LogTag} adopt: isHuman={(a != null && a.isHuman)} avatarValid={(a != null && a.avatar != null && a.avatar.isValid)} " +
                      $"controller={(a != null && a.runtimeAnimatorController != null ? a.runtimeAnimatorController.name : "-")}");
        }
        finally
        {
            Object.DestroyImmediate(adoptedInst);
            Object.DestroyImmediate(candidateInst);
            Object.DestroyImmediate(authorInst);
        }

        return guidAfter == guidBefore && !string.IsNullOrEmpty(guidAfter);
    }

    [MenuItem("Tools/VisionGraft/Human Rig Pilot/Move Pilot Prefabs Out Of Resources")]
    public static void TidyPilotPrefabs()
    {
        if (!AssetDatabase.IsValidFolder(PilotFolder))
        {
            AssetDatabase.CreateFolder(Path.GetDirectoryName(PilotFolder).Replace('\\', '/'), Path.GetFileName(PilotFolder));
        }

        foreach (string path in PilotPrefabsToTrash)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
            {
                Debug.Log($"{LogTag} tidy: 無い（済み）{path}");
                continue;
            }

            bool ok = AssetDatabase.MoveAssetToTrash(path);
            Debug.Log($"{LogTag} tidy: ごみ箱へ {(ok ? "ok" : "FAILED")} {path}");
        }

        foreach (string path in PilotPrefabsToMove)
        {
            string to = PilotFolder + "/" + Path.GetFileName(path);
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
            {
                Debug.Log($"{LogTag} tidy: 無い（済み？）{path} -> {to} exists={AssetDatabase.LoadAssetAtPath<GameObject>(to) != null}");
                continue;
            }

            string error = AssetDatabase.MoveAsset(path, to);
            Debug.Log($"{LogTag} tidy: 移す {(string.IsNullOrEmpty(error) ? "ok" : "FAILED " + error)} {path} -> {to}");
        }

        AssetDatabase.SaveAssets();
        ModelResourceIndexGenerator.Generate();
    }

    // keepMovedBones（2026-10-27、膝を動かした FBX 用）: 取り込みの設定は呼ぶ側が写す（CopyHumanImportSettings の ownSkeleton）。元の prefab の
    // Transform を写すのは、新しい FBX と元の FBX で局所の位置・回転が同じ骨だけ（Blender で動かした骨は新しい FBX の基準の値のまま残す）。
    public static void CreatePrefab(string fbxPath, string originalFbx, string originalPrefab, string outPrefab, string name, bool keepMovedBones = false)
    {
        if (!keepMovedBones)
        {
            CopyHumanImportSettings(fbxPath, originalFbx);
        }

        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
        GameObject orig = AssetDatabase.LoadAssetAtPath<GameObject>(originalPrefab);
        GameObject origModel = keepMovedBones ? AssetDatabase.LoadAssetAtPath<GameObject>(originalFbx) : null;
        if (model == null || orig == null || (keepMovedBones && origModel == null))
        {
            Debug.LogError($"{LogTag} missing {fbxPath} or {originalPrefab} or {originalFbx}");
            return;
        }

        var origModelByPath = new Dictionary<string, Transform>();
        if (origModel != null)
        {
            foreach (Transform t in origModel.GetComponentsInChildren<Transform>(true))
            {
                origModelByPath[PathOf(origModel.transform, t)] = t;
            }
        }

        GameObject origInst = (GameObject)PrefabUtility.InstantiatePrefab(orig);
        GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
        try
        {
            inst.name = name;
            var origByPath = new Dictionary<string, Transform>();
            foreach (Transform t in origInst.GetComponentsInChildren<Transform>(true))
            {
                origByPath[PathOf(origInst.transform, t)] = t;
            }

            int copied = 0;
            int missing = 0;
            int kept = 0;
            foreach (Transform t in inst.GetComponentsInChildren<Transform>(true))
            {
                if (t == inst.transform)
                {
                    continue;
                }

                string path = PathOf(inst.transform, t);
                if (keepMovedBones && origModelByPath.TryGetValue(path, out Transform om) &&
                    ((t.localPosition - om.localPosition).sqrMagnitude > 1e-10f || Quaternion.Angle(t.localRotation, om.localRotation) > 0.01f))
                {
                    kept++;
                    Debug.Log($"{LogTag} {name}: keep moved bone {path} pos {om.localPosition.ToString("F4")} -> {t.localPosition.ToString("F4")} " +
                              $"rot {Quaternion.Angle(t.localRotation, om.localRotation):F2}deg");
                    continue;
                }

                if (origByPath.TryGetValue(path, out Transform o))
                {
                    t.localPosition = o.localPosition;
                    t.localRotation = o.localRotation;
                    t.localScale = o.localScale;
                    copied++;
                }
                else
                {
                    missing++;
                    Debug.LogWarning($"{LogTag} {name}: no original transform at {PathOf(inst.transform, t)}");
                }
            }

            inst.transform.localScale = origInst.transform.localScale;
            Animator a = inst.GetComponentInChildren<Animator>(true);
            Animator oa = origInst.GetComponentInChildren<Animator>(true);
            if (a != null && oa != null)
            {
                a.runtimeAnimatorController = oa.runtimeAnimatorController;
                a.applyRootMotion = oa.applyRootMotion;
                a.cullingMode = oa.cullingMode;
            }

            var origSmr = new Dictionary<string, SkinnedMeshRenderer>();
            foreach (SkinnedMeshRenderer r in origInst.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                origSmr[PathOf(origInst.transform, r.transform)] = r;
            }

            foreach (SkinnedMeshRenderer r in inst.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (origSmr.TryGetValue(PathOf(inst.transform, r.transform), out SkinnedMeshRenderer o))
                {
                    r.sharedMaterials = o.sharedMaterials;
                }
                else
                {
                    Debug.LogWarning($"{LogTag} {name}: no original renderer at {PathOf(inst.transform, r.transform)}");
                }
            }

            PrefabUtility.SaveAsPrefabAsset(inst, outPrefab, out bool ok);
            if (keepMovedBones)
            {
                ReportRestSkin(name, origInst, inst);
            }

            Debug.Log($"{LogTag} prefab {(ok ? "saved" : "FAILED")} {outPrefab} transforms copied={copied} kept={kept} missing={missing} " +
                      $"isHuman={(a != null && a.isHuman)} avatarValid={(a != null && a.avatar != null && a.avatar.isValid)} " +
                      $"origAvatarValid={(oa != null && oa.avatar != null && oa.avatar.isValid)}");
            Report(name, origInst, inst);
        }
        finally
        {
            Object.DestroyImmediate(inst);
            Object.DestroyImmediate(origInst);
        }
    }

    // autoAvatar（2026-10-27、骨を動かした FBX 用）: 骨の対応と基準の姿勢を元の Avatar から写さず、元の FBX と同じく Unity の自動に任せる
    // （元の FBX の .meta も human: [] の自動）。元の Avatar の skeleton は自動の T ポーズ（腕を水平・脚をまっすぐにした局所の回転）で、
    // 骨を動かした FBX に写すと膝の位置が古いままになり、新しい FBX の基準の姿勢で置き換えると腕まで 63° ずれた T ポーズになる（10/27 に確かめた）。
    // 重みは元と同じなので、自動の対応が元と同じになるかを Report で確かめる（10/24 に headfront を取り違えたのは重みを変えた FBX）。
    private static void CopyHumanImportSettings(string path, string like, bool autoAvatar = false)
    {
        var target = AssetImporter.GetAtPath(path) as ModelImporter;
        var source = AssetImporter.GetAtPath(like) as ModelImporter;
        if (target == null || source == null)
        {
            Debug.LogError($"{LogTag} importer missing: {path} / {like}");
            return;
        }

        target.animationType = source.animationType;
        target.avatarSetup = source.avatarSetup;
        // 骨の対応（Humanoid）を、元の FBX が実際に作った Avatar のものにそろえる（2026-10-24）。元は自動の対応（.meta の human: []）で、
        // 重みを変えた FBX を自動に任せると、重みの付き方で headfront を「あご」ではなく「左目」に対応付けた（男性 hw2）・対応から外した（女性 hw2）。
        Avatar sourceAvatar = null;
        foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(like))
        {
            if (asset is Avatar avatar && avatar.isHuman)
            {
                sourceAvatar = avatar;
                break;
            }
        }

        if (autoAvatar)
        {
            // 自動の対応（human・skeleton が空 = .meta の human: []）。筋肉の設定だけ元から写す。
            HumanDescription auto = sourceAvatar != null ? sourceAvatar.humanDescription : new HumanDescription();
            auto.human = new HumanBone[0];
            auto.skeleton = new SkeletonBone[0];
            target.humanDescription = auto;
        }
        else if (sourceAvatar != null)
        {
            target.humanDescription = sourceAvatar.humanDescription;
        }
        else
        {
            Debug.LogWarning($"{LogTag} 元の Avatar が見つからないので骨の対応は自動のまま: {like}");
        }

        target.globalScale = source.globalScale;
        target.useFileScale = source.useFileScale;
        target.bakeAxisConversion = source.bakeAxisConversion;
        target.materialImportMode = source.materialImportMode;
        target.importAnimation = false;
        target.SaveAndReimport();
    }

    // 2 つの FBX の Avatar の基準の姿勢（自動の T ポーズ、skeleton の局所の回転）が骨ごとにどれだけ違うか（動かしていない骨は 0 のはず）。
    private static void ReportAvatarSkeleton(string name, string fbxA, string fbxB)
    {
        Avatar a = null;
        Avatar b = null;
        foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(fbxA)) { if (asset is Avatar av && av.isHuman) { a = av; } }
        foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(fbxB)) { if (asset is Avatar av && av.isHuman) { b = av; } }
        if (a == null || b == null)
        {
            Debug.LogError($"{LogTag} {name}: avatar missing a={a != null} b={b != null}");
            return;
        }

        var byName = new Dictionary<string, SkeletonBone>();
        foreach (SkeletonBone s in a.humanDescription.skeleton)
        {
            byName[s.name] = s;
        }

        int differ = 0;
        foreach (SkeletonBone s in b.humanDescription.skeleton)
        {
            if (!byName.TryGetValue(s.name, out SkeletonBone o))
            {
                continue;
            }

            float deg = Quaternion.Angle(o.rotation, s.rotation);
            float mm = Vector3.Distance(o.position, s.position) * 1000f;
            if (deg > 0.05f || mm > 0.01f)
            {
                differ++;
                Debug.Log($"{LogTag} {name}: T-pose {s.name} rot {deg:F2}deg pos {mm:F2}mm（局所、モデルの単位）");
            }
        }

        var mapA = new Dictionary<string, string>();
        foreach (HumanBone h in a.humanDescription.human) { mapA[h.humanName] = h.boneName; }
        int mapDiffer = 0;
        foreach (HumanBone h in b.humanDescription.human)
        {
            if (!mapA.TryGetValue(h.humanName, out string boneA) || boneA != h.boneName)
            {
                mapDiffer++;
                Debug.LogWarning($"{LogTag} {name}: mapping {h.humanName} orig={(boneA ?? "-")} new={h.boneName}");
            }
        }

        Debug.Log($"{LogTag} {name}: T-pose bones differ {differ} of {b.humanDescription.skeleton.Length}; mapping differ {mapDiffer} " +
                  $"(orig {a.humanDescription.human.Length} / new {b.humanDescription.human.Length})");
    }

    // 基準の姿勢の皮膚が元と同じか（2026-10-27、骨を動かした FBX: 動かしたのは骨の基準の位置だけなので、焼いた頂点は元と同じはず）。
    private static void ReportRestSkin(string name, GameObject a, GameObject b)
    {
        SkinnedMeshRenderer[] ra = a.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        SkinnedMeshRenderer[] rb = b.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int i = 0; i < Mathf.Min(ra.Length, rb.Length); i++)
        {
            var ma = new Mesh();
            var mb = new Mesh();
            ra[i].BakeMesh(ma, true);
            rb[i].BakeMesh(mb, true);
            Vector3[] va = ma.vertices;
            Vector3[] vb = mb.vertices;
            float max = 0f;
            for (int v = 0; v < Mathf.Min(va.Length, vb.Length); v++)
            {
                Vector3 wa = ra[i].transform.TransformPoint(va[v]);
                Vector3 wb = rb[i].transform.TransformPoint(vb[v]);
                max = Mathf.Max(max, Vector3.Distance(wa, wb));
            }

            Debug.Log($"{LogTag} {name}: rest skin {ra[i].name} verts {va.Length}/{vb.Length} max diff {max * 1000f:F4}mm");
            Object.DestroyImmediate(ma);
            Object.DestroyImmediate(mb);
        }
    }

    // 骨ごとの bind の world の位置・回転の差と、Humanoid の対応が同じか（Animator.GetBoneTransform の名前）。
    private static void Report(string name, GameObject a, GameObject b)
    {
        Animator aa = a.GetComponentInChildren<Animator>(true);
        Animator ba = b.GetComponentInChildren<Animator>(true);
        float maxPos = 0f;
        float maxRot = 0f;
        string worst = "";
        int mismatch = 0;
        for (HumanBodyBones hb = 0; hb < HumanBodyBones.LastBone; hb++)
        {
            Transform x = aa != null && aa.isHuman ? aa.GetBoneTransform(hb) : null;
            Transform y = ba != null && ba.isHuman ? ba.GetBoneTransform(hb) : null;
            if (x == null && y == null)
            {
                continue;
            }

            if (x == null || y == null || x.name != y.name)
            {
                mismatch++;
                Debug.LogWarning($"{LogTag} {name}: humanoid {hb} orig={(x != null ? x.name : "-")} rt={(y != null ? y.name : "-")}");
                continue;
            }

            float dp = Vector3.Distance(x.position, y.position);
            float dr = Quaternion.Angle(x.rotation, y.rotation);
            if (dp > maxPos) { maxPos = dp; worst = hb.ToString(); }
            maxRot = Mathf.Max(maxRot, dr);
        }

        int va = 0;
        int vb = 0;
        foreach (SkinnedMeshRenderer r in a.GetComponentsInChildren<SkinnedMeshRenderer>(true)) { va += r.sharedMesh != null ? r.sharedMesh.vertexCount : 0; }
        foreach (SkinnedMeshRenderer r in b.GetComponentsInChildren<SkinnedMeshRenderer>(true)) { vb += r.sharedMesh != null ? r.sharedMesh.vertexCount : 0; }
        Debug.Log($"{LogTag} {name}: humanoid mapping mismatches={mismatch} bind maxPos={maxPos * 1000f:F3}mm ({worst}) maxRot={maxRot:F3}deg " +
                  $"verts orig={va} rt={vb}");
    }

    private static string PathOf(Transform root, Transform t)
    {
        var parts = new List<string>();
        for (Transform c = t; c != null && c != root; c = c.parent)
        {
            parts.Add(c.name);
        }

        parts.Reverse();
        return string.Join("/", parts);
    }
}
