// AniMer 26 関節の四肢チェーン。{ 近位, 中間, 遠位, 末端 } の順。
//
// 2026-08-28 に生成側から実測ベースの対応表を受領して**全面的に訂正**した
// （docs/bundle-shared/README.md の D-007 回答）。旧定義は
//   LeftFront { 18, 13, 9, 15 } / RightFront { 18, 12, 8, 14 }
//   LeftRear  {  7, 11, 17, 6 } / RightRear  {  7, 10, 16,  5 }
// で、**前肢は起点が誤り（kp18 は「き甲」ではなく頭）、前肢・後肢とも左右が逆**
// だった。この定義は一度も検証されておらず、これに乗った測定を 3 セッション読んで
// いた（docs/smpl-retargeting.md）。
//
// 対応表の出典: AniMer の my_smpl_00781_4_all.pkl を rest pose で最近傍マッチング。
// こちら側でも 2 つの独立な方法で検証済み:
//   - 主張するリンクが左右を入れ替えた対立候補より短いか … 10/10 で通過。
//     左右対称もきれい（肩→肘 0.229 / 0.231m、膝→飛節 0.161 / 0.157m）
//   - 前肢の 1:2 長さ比 … SMAL 0.85 に対し keypoints 0.76〜0.79 で通過
//
// 注意: 26 点は**メッシュ表面の頂点群の平均**で、SMAL の 35 骨格関節そのものでは
// ない。末端（前足 kp3/4・後足 kp5/6）は表面オフセットが大きく、距離の変動係数も
// 0.12〜0.18 と近位（0.042〜0.048）より一桁悪い。末端を含む量は割り引いて読む。
public static class AnimalPoseJointChains
{
    // kp12 左肩 → kp8 左肘 → kp14 左手根 → kp3 左前足
    public static readonly int[] LeftFront = { 12, 8, 14, 3 };

    // kp13 右肩 → kp9 右肘 → kp15 右手根 → kp4 右前足
    public static readonly int[] RightFront = { 13, 9, 15, 4 };

    // kp7 骨盤（尾の付け根）→ kp10 左膝 → kp16 左飛節 → kp5 左後足
    // kp7 は左右の後肢で共有されるハブ。前肢と違い左右別の起点が無い。
    public static readonly int[] LeftRear = { 7, 10, 16, 5 };

    // kp7 骨盤 → kp11 右膝 → kp17 右飛節 → kp6 右後足
    public static readonly int[] RightRear = { 7, 11, 17, 6 };
}
