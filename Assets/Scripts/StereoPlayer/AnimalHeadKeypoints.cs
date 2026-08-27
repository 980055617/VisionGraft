// AniMer 26 関節のうち、頭まわりで使う番号。
//
// 2026-08-28 に生成側から実測ベースの対応表を受領して訂正した
// （docs/bundle-shared/README.md の D-007 回答）。AniMer の
// my_smpl_00781_4_all.pkl を rest pose で最近傍マッチングした結果。
//
//   kp18: Head（頭）          — **「き甲」ではない。** 旧コメントは誤り
//   kp24: 鼻先端（最前方）     — SMAL の追加マーカー EndNose 相当
//   kp2 : 鼻筋・マズル中央
//   kp20 / kp21: 左耳 / 右耳
//   kp0  / kp1 : 左目 / 右目（生成側も推定と明記、確度は下がる）
//   kp22 / kp23: 口角 左 / 右（同上）
//   kp7 : 尾の付け根（骨盤）    — root は kp7 と kp18 の中点 = 体幹中心
//   kp19: 尾の先端、kp25: 尾の中間（SMAL Tail4 と完全一致）
//
// **kp24 と kp2 はどちらも顔の中の点**で、体長 0.756m に対し 8.5cm しか離れて
// いない（2026-08-28 実測）。首の向きとしては使えない。首を正しく取るなら
// midpoint(kp12 左肩, kp13 右肩) → kp18(頭) だが、これを使う keypoint 経路は
// SMAL block を持つ bundle では実行されない（docs/smpl-retargeting.md）。
public static class AnimalHeadKeypoints
{
    // 頭。root（kp7 と kp18 の中点）の前側の端点でもある。
    public const int Head = 18;

    // 顔の最前方（鼻先端）。
    public const int Nose = 24;

    // 鼻筋・マズル中央。Nose の少し後ろ。
    public const int Muzzle = 2;

    // 左肩 / 右肩。首の向きを取るならこの 2 点の中点から Head へ。
    public const int LeftShoulder = 12;
    public const int RightShoulder = 13;
}
