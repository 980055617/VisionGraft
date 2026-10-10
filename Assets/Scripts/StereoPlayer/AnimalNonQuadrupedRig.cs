using System.Collections.Generic;

// 非四足モード（smalNonQuadrupedRig、2026-10-08、2026-10-09 にユーザーが採用して既定 ON）の役の表。鳥 3 体（30_Goose・32_Guineafowl・46_Pheasant）とカンガルー（35_Kangaroo）を
// 既存の SMAL FK の部品（根の写像・首と頭の体の写像 B・首の鎖・後肢の体の写像・受け身の骨）で動かすための役の付け替え。新しい式は無い。
//
// AnimalPoseApplier.ApplyNonQuadrupedRoles がリグのキャッシュを作るとき（GetOrBuildAnimalRigCache で役の骨を決めた直後、bind を控える前）に 1 回だけ読む。
// 効くのは smalNonQuadrupedRig が ON で、名簿 smalNonQuadrupedRigModels に載り、この表に行のあるモデルだけ。
// キーは AnimalLegMappingFix.Key（prefab 名から先頭の「数字_」を外した名前、完全一致）。値は AnimalBoneMappingOverride のフィールド名 = 骨名 の組
// （; 区切り、骨名は完全一致）。**値が空ならその役を null にする**（名前は探さない）。全部か何もしないか: 空でない骨名が 1 つでも見つからなければ
// 1 行ログを出してモデルは今のまま。既定 ON の AnimalLegMappingFix.Table（fixAnimalLegMapping、被験者にも効く）には入れない。
//
// 鳥: spine = Pelvis（両脚・尾羽 3 本・Spine の親。その親の Root は skin の重み 0）。neck = Neck3（既定 ON の首の鎖が Neck1・Neck2 に 1/3・2/3 を配る）。
//   尾の役は 3 つとも null（今は名前の 'tail' の同点で左の尾羽 TailL1 が選ばれていた。null なら尾羽は Pelvis に剛体のまま、ジェスチャも null の骨を飛ばす）。
//   前肢の役は prefab の上書きが空なので null のまま（翼はたたんだ bind のまま）。
// カンガルー: spine = Hips（今の Spine の親。誰も書かず root の向きに残り、腰が p50 115〜154° ねじれていた）。neck = Neck02（首の鎖が Neck01 に 1/2 を配る）。
//   尾の付け根は今のまま Tail01（名前の 'tail'）で、非四足モードでは関節 25/26 を bind に保つ（AnimalSmalFkApplier の主ループ、腰に剛体）。
public static class AnimalNonQuadrupedRig
{
    private const string Bird = "spine=Pelvis;neck=Neck3;tailBase=;tailMid=;tailTip=";

    public static readonly Dictionary<string, string> Table = new Dictionary<string, string>(System.StringComparer.Ordinal)
    {
        { "Goose", Bird },
        { "Guineafowl", Bird },
        { "Pheasant", Bird },
        { "Kangaroo", "spine=Hips;neck=Neck02" },
    };

    // 行を「フィールド名, 骨名」の組に分ける。空の組（末尾の ; など）は飛ばし、前後の空白は外す。骨名が空 = その役を null にする。
    // '=' が無い・フィールド名が空・AnimalBoneMappingOverride に無いフィールド名・同じフィールド名が 2 回・組が 1 つも無い なら false（error に理由）。
    public static bool TryParse(string spec, List<KeyValuePair<string, string>> pairs, out string error)
    {
        error = string.Empty;
        if (pairs == null)
        {
            error = "pairs が null";
            return false;
        }

        pairs.Clear();
        if (string.IsNullOrEmpty(spec))
        {
            error = "空の行";
            return false;
        }

        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (string raw in spec.Split(';'))
        {
            string pair = raw.Trim();
            if (pair.Length == 0)
            {
                continue;
            }

            int eq = pair.IndexOf('=');
            string key = eq > 0 ? pair.Substring(0, eq).Trim() : string.Empty;
            if (key.Length == 0)
            {
                error = $"'{pair}' にフィールド名と '=' が無い";
                pairs.Clear();
                return false;
            }

            if (!AnimalLegMappingFix.IsOverrideKey(key))
            {
                error = $"知らないフィールド名 '{key}'";
                pairs.Clear();
                return false;
            }

            if (!seen.Add(key))
            {
                error = $"フィールド名 '{key}' が 2 回ある";
                pairs.Clear();
                return false;
            }

            pairs.Add(new KeyValuePair<string, string>(key, pair.Substring(eq + 1).Trim()));
        }

        if (pairs.Count == 0)
        {
            error = "組が無い";
            return false;
        }

        return true;
    }
}
