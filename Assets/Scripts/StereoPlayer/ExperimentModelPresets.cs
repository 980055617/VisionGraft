using System;
using System.Collections.Generic;

// 被験者実験の Model ボタンで切り替える 2 組のモデル（既定1・既定2）の表（2026-10-24 ユーザー「被験者実験に関しては
// modelを押すとそのwindowが出てくるのではなくモデルを既定1,既定2で切り替えれるようにするだけでいいです」）。
//   - 表は「動画（bundle のファイル名）× track」。試行は既定1 で始まり、Model を押すたびにその動画の表の track が全部
//     既定1 ⇄ 既定2 で入れ替わる（StreamingStereoVideoPlayer.ExperimentModelPreset.partial.cs）
//   - 既定1 は Meshy の前の 10 個、既定2 は「新しい10個」（前の 10 個の近しいもの。ユーザーの対応表、
//     Docs/tmp/meshy_models_20261024/README.md）
//   - モデルは prefab の名前で書く（index は試作の prefab の有無やモデルの増減でずれる）。category は名前を探す一覧
//     （Resources/Models/{Human,Animal,Else}。EditMode のテストが model_index.txt と突き合わせる）
//   - 練習（bundle_tutorial）の組は調整役が選んだ（人 00_Female_A_01 ⇄ 08_Male_A_01、犬 36_LabradorDog ⇄ 39_Lynx。
//     どちらも前の実験で使っていたモデル）。犬の既定1 は ExperimentController.tutorialAnimalModelName と同じにしておく
// 置換ありの条件（と、その練習）だけで使う。ほかの条件はモデルが出ない。
public readonly struct ExperimentModelPreset
{
    public const string Human = "Human";
    public const string Animal = "Animal";
    public const string Else = "Else";

    // bundle のファイル名（例 "bundle_dog.svb"）。大文字小文字は区別しない。
    public readonly string bundle;
    public readonly uint trackId;
    public readonly string category;
    public readonly string preset1;
    public readonly string preset2;

    public ExperimentModelPreset(string bundle, uint trackId, string category, string preset1, string preset2)
    {
        this.bundle = bundle;
        this.trackId = trackId;
        this.category = category;
        this.preset1 = preset1;
        this.preset2 = preset2;
    }

    // preset は 1 か 2。
    public string NameFor(int preset)
    {
        return preset == 2 ? preset2 : preset1;
    }
}

public static class ExperimentModelPresets
{
    public const int First = 1;

    // track の番号と種類は meta.bin で確かめた（2026-10-24。category 0 = Else、1 = Human、2 = Animal）。
    public static readonly ExperimentModelPreset[] Entries =
    {
        // Video1 風船（track 0 = 青い風船）
        new ExperimentModelPreset("bundle_balloon.svb", 0, ExperimentModelPreset.Else, "19_Balloon_Meshy", "25_Balloon_Meshy_Star"),
        // Video2 猫 → キツネ
        new ExperimentModelPreset("bundle_cat.svb", 0, ExperimentModelPreset.Animal, "53_MeshyCat", "55_MeshyFox"),
        // Video3 サッカー: 男性 → サッカーのユニフォームの女性、サッカーボール → バレーボール
        new ExperimentModelPreset("bundle_soccer.svb", 0, ExperimentModelPreset.Human, "17_Male_Meshy", "19_Female_Meshy_Soccer"),
        new ExperimentModelPreset("bundle_soccer.svb", 1, ExperimentModelPreset.Else, "20_Soccer_Meshy", "26_Volleyball_Meshy"),
        // Video4 廊下の女性 → ゾンビの女性
        new ExperimentModelPreset("bundle_tv.svb", 0, ExperimentModelPreset.Human, "18_Female_Meshy", "20_Female_Meshy_Zombie"),
        // Video5 車 3 台: 赤いセダン → 馬車、白い軽バン → トゥクトゥク、紺の車 → 空飛ぶ車
        new ExperimentModelPreset("bundle_car2.svb", 0, ExperimentModelPreset.Else, "16_Car_Meshy_Red", "22_Car_Meshy_Carriage"),
        new ExperimentModelPreset("bundle_car2.svb", 1, ExperimentModelPreset.Else, "18_Car_Meshy_Van", "24_Car_Meshy_TukTuk"),
        new ExperimentModelPreset("bundle_car2.svb", 2, ExperimentModelPreset.Else, "17_Car_Meshy_Navy", "23_Car_Meshy_Flying"),
        // Video6 犬 → 狼、テニスボール → 野球のボール
        new ExperimentModelPreset("bundle_dog.svb", 0, ExperimentModelPreset.Animal, "52_MeshyDog", "54_MeshyWolf"),
        new ExperimentModelPreset("bundle_dog.svb", 1, ExperimentModelPreset.Else, "21_Tennis_Meshy", "27_Baseball_Meshy"),
        // 練習（track 0 = 人、track 1 = 犬）
        new ExperimentModelPreset("bundle_tutorial.svb", 0, ExperimentModelPreset.Human, "00_Female_A_01", "08_Male_A_01"),
        new ExperimentModelPreset("bundle_tutorial.svb", 1, ExperimentModelPreset.Animal, "36_LabradorDog", "39_Lynx"),
    };

    public static List<ExperimentModelPreset> ForBundle(IReadOnlyList<ExperimentModelPreset> entries, string bundleName)
    {
        var result = new List<ExperimentModelPreset>();
        if (entries == null || string.IsNullOrEmpty(bundleName))
        {
            return result;
        }

        for (int i = 0; i < entries.Count; i++)
        {
            if (string.Equals(entries[i].bundle, bundleName, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(entries[i]);
            }
        }

        return result;
    }

    // 1 ⇄ 2。
    public static int Next(int preset)
    {
        return preset == 2 ? 1 : 2;
    }
}
