"""D-005 の裏付けを再現するスクリプト。

`source/placement_observations.json` の depthStats を集計し、
`meta.bin` の keypoints3d と突き合わせて「対象物が体に埋もれる量」を出す。

    python anchor_quality_check.py path/to/bundle.svb

出力:
  - depth サンプル数 validCount の分布（bbox サイズに追従しているか）
  - 対象物の見かけサイズ別の埋もれ率
  - depthStats.iqr 別の埋もれ率

「埋もれ量」の定義:
  対象物（other）の anchor_z と、その対象物に画面上で最も近い人体主要関節
  （keypoints3d の OpenPose25 0-24）の深度を比べ、対象物の方が奥にある量。
  正の値が大きいほど、対象物が人体の内部に食い込む。

  人体側の深度は keypoints3d（HMR2 由来、depth map を使わない独立した推定）から
  取っている。

**重要な但し書き（2026-08-20 追記）**

  この指標は「利用側で対象物がモデルに埋もれるか」を測るものであって、
  **anchor_z の抽出品質を測るものではない。** 値の改善・悪化を抽出方法の
  良し悪しとして読んではいけない。

  理由: 「対象物と人の深度差」は anchor_z 由来で popout レンジ（0.35m）へ
  圧縮された値だが、「接触部位の深度」は keypoints3d の実寸をモデルスケール
  （実測 0.1953）倍した実距離空間の値であり、両者は別の空間にある。

  実測（bundle_human.svb 全編）:
      実距離レンジ         2.607〜5.641m（幅 3.034m）
      配置深度レンジ       0.630〜0.980m（幅 0.350m）
      d(配置深度)/d(実距離) median 0.0000  p25 -0.0420  p75 +0.0757

  実距離が変わっても配置深度がほとんど動かず符号すらばらつくので、抽出方法を
  どう改善しても、anchor_z 自体が実距離を反映しない限りこの指標は改善しない。
  D-005（サンプリング窓）ではなく D-004（anchor_z の距離再現性）の問題。
"""
import json
import math
import statistics as st
import sys
import zipfile

from bundle_depth_check import Bundle

PELVIS = 39          # keypoints3d(hmr2_openpose25_extra19) の骨盤
MAIN_JOINTS = range(0, 25)   # OpenPose25 の主要関節だけを接触部位の候補にする
DEFAULT_MODEL_HEIGHT_M = 1.7026


def decode_depth(z01, larger_is_farther, screen=1.0, popout=0.35, eps=0.02,
                 rng=None):
    """anchor_z01 を配置深度[m]へ。利用側 (Unity) の変換に合わせた簡易版。

    rng = (dmin, dmax) を渡すと逆数変換を行う（Unity の ResolvePopoutFraction 相当）。
    """
    nearness = (1.0 - z01) if larger_is_farther else z01
    if rng and rng[0] > 0.01:
        dmin, dmax = rng
        d = min(max(nearness, dmin), dmax)
        far = (1.0 / d - 1.0 / dmax) / (1.0 / dmin - 1.0 / dmax)
        frac = 1.0 - min(1.0, max(0.0, far))
    else:
        frac = min(1.0, max(0.0, nearness))
    return max(0.001, min(screen - eps - popout * frac, screen - 0.0001))


def main(path):
    b = Bundle(path)
    m = b.manifest
    eye_h, fy = m["eye_h"], m["fy_norm"]
    larger_far = m.get("depth_policy") is not None and bool(m["depth_policy"].get("convention"))

    # 利用側と同じ 2%/98% レンジ（120 サンプル）
    step = max(1, b.h["numFrames"] // 120)
    samples = sorted(o["z01"] for f in range(0, b.h["numFrames"], step) for o in b.frame(f))
    rng = None
    if len(samples) >= 8:
        lo = samples[min(max(round(len(samples) * 0.02), 0), len(samples) - 1)]
        hi = samples[min(max(round(len(samples) * 0.98), 0), len(samples) - 1)]
        if hi - lo >= 0.02:
            a = (1.0 - lo) if larger_far else lo
            c = (1.0 - hi) if larger_far else hi
            rng = (min(a, c), max(a, c))

    with zipfile.ZipFile(path) as z:
        names = z.namelist()
        if "source/placement_observations.json" not in names:
            print("source/placement_observations.json が無いので depthStats を評価できません")
            return
        obs = json.loads(z.read("source/placement_observations.json"))

    rows = []
    for fr in obs["frames"]:
        f = fr["frameIndex"]
        target = next((o for o in fr["objects"] if o["category"] == "other"), None)
        if not target:
            continue
        stats = target.get("depthStats") or {}

        objs = b.frame(f)
        person = next((x for x in objs if x["catName"] == "person" and "joints" in x), None)
        other = next((x for x in objs if x["catName"] == "other"), None)
        if not person or not other or person["bboxH"] <= 0 or other["bboxH"] <= 0:
            continue

        joints = person["joints"]
        if len(joints) <= PELVIS:
            continue
        pel = joints[PELVIS]
        span = max(j[1] - pel[1] for j in joints) - min(j[1] - pel[1] for j in joints)
        if span < 1e-6:
            continue

        # keypoints を bbox 高さに合わせて投影し、対象物に最も近い関節を選ぶ
        k = person["bboxH"] / span
        cu, cv = person["anchorU"], person["anchorV"]
        _, idx = min(
            (math.dist((other["anchorU"], other["anchorV"]),
                       (cu + (joints[i][0] - pel[0]) * k, cv - (joints[i][1] - pel[1]) * k)), i)
            for i in MAIN_JOINTS)

        zp = decode_depth(person["z01"], larger_far, rng=rng)
        zq = decode_depth(other["z01"], larger_far, rng=rng)
        height = (2 * person["bboxH"] / eye_h) * (zp / fy)
        scale = height / DEFAULT_MODEL_HEIGHT_M
        part_dz = (joints[idx][2] - pel[2]) * scale        # 接触部位の骨盤基準 z [m]
        buried = ((zq - zp) - part_dz) * 1000.0            # + = 対象物が部位より奥

        rows.append(dict(
            f=f,
            buried=buried,
            diameter=0.5 * (other["bboxW"] + other["bboxH"]),
            valid=stats.get("validCount", 0),
            iqr=stats.get("iqr", 0.0),
            source=(target.get("usedAnchor") or {}).get("source", ""),
            status=target.get("placementStatus", ""),
        ))

    if not rows:
        print("評価できるフレームがありません")
        return

    print(f"=== {path} ===")
    print(f"照合フレーム: {len(rows)}\n")

    vals = [r["valid"] for r in rows]
    vs = sorted(vals)
    print("--- depth サンプル数 validCount ---")
    print(f"  median {st.median(vals)}  p10 {vs[len(vs) // 10]}  p90 {vs[-len(vs) // 10]}  "
          f"min {min(vals)}  max {max(vals)}")
    if min(vals) == max(vals):
        print(f"  ** 全フレームで {min(vals)} 固定。bbox サイズに追従していない **")
    print()

    from collections import Counter
    print(f"  anchor source   : {dict(Counter(r['source'] for r in rows))}")
    print(f"  placementStatus : {dict(Counter(r['status'] for r in rows))}\n")

    buried = [r["buried"] for r in rows]
    bs = sorted(buried)
    print("--- 埋もれ量（+ = 対象物が接触部位より奥）---")
    print(f"  median {st.median(buried):+.1f}mm  p10 {bs[len(bs) // 10]:+.1f}mm  "
          f"p90 {bs[-len(bs) // 10]:+.1f}mm")
    print(f"  埋もれ率（>0）: {100 * sum(1 for x in buried if x > 0) / len(buried):.1f}%\n")

    print("--- 対象物の見かけサイズ別 ---")
    for lo, hi in ((0, 35), (35, 50), (50, 10 ** 9)):
        v = [r["buried"] for r in rows if lo <= r["diameter"] < hi]
        if len(v) < 15:
            continue
        label = f"{lo}-{hi if hi < 10 ** 9 else 'inf'}px"
        print(f"  bbox 径 {label:>10}: {len(v):>5}f  median {st.median(v):+7.1f}mm  "
              f"埋もれ率 {100 * sum(1 for x in v if x > 0) / len(v):5.1f}%")
    print()

    print("--- depthStats.iqr 別（サンプルのばらつき）---")
    for lo, hi in ((0, 0.002), (0.002, 0.01), (0.01, 0.03), (0.03, 10)):
        v = [r["buried"] for r in rows if lo <= r["iqr"] < hi]
        if len(v) < 15:
            continue
        label = f"{lo:.3f}-{hi if hi < 10 else 'inf'}"
        print(f"  iqr {label:>12}: {len(v):>5}f  median {st.median(v):+7.1f}mm  "
              f"埋もれ率 {100 * sum(1 for x in v if x > 0) / len(v):5.1f}%")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    main(sys.argv[1])
