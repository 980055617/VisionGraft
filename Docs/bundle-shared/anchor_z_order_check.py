"""D-008 の解析スクリプト。

同一フレーム内で `anchor_z` が前後関係をどれだけ保てているかを数える。
`train_depth_order.py` が個々のフレームを見るのに対し、こちらは全編を集計して
**「順序が壊れているのは量子化の前か後か」** を切り分ける。

判定に使う量:
  - `camZ`   … anchor_z_q * quant_pos_scale。Unity がそのまま zMeters にする値。
               ステレオ視差も深度バッファも **view 空間 Z** で決まるので、
               前後関係の正解はこちらで見る。
  - `視点からの距離` … sqrt(x^2+y^2+z^2)。横位置に強く引きずられるため、
               「どちらが手前に見えるか」の指標としては使えない（D-008 の回答参照）。

bundle に `source/placement_observations.json` があれば、量子化前の
`usedAnchor.z`（float）とも比較する。これが「量子化しなければどこまで正しいか」
の上限になる。

使い方:
    python anchor_z_order_check.py <bundle.svb> [--tracks 1,2,3,4,5,6,7]
                                                [--ranges 0-300,300-900,900-1830]

`--tracks` は「track 番号順＝手前から奥」が成り立つ track だけを指定する。
train なら車両 track のみ（静止物の track 0 は除く）。省略すると全 track を使う
ので、無関係な対象が混ざる bundle では数字に意味が無くなる。
"""
import argparse
import itertools
import json
import math
import sys
import zipfile

sys.path.insert(0, __file__.rsplit("/", 1)[0])
from train_depth_order import _load_full, principal_norm  # noqa: E402

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")


def intrinsics(m):
    eye_w, eye_h = m["eye_w"], m["eye_h"]
    if m.get("fx_norm", 0) > 0 and m.get("fy_norm", 0) > 0:
        fx, fy = m["fx_norm"], m["fy_norm"]
    else:
        fovx = math.radians(m["fovx_deg"])
        fx = 1.0 / math.tan(fovx * 0.5)
        fy = fx * (eye_w / float(eye_h))
    return eye_w, eye_h, fx, fy, principal_norm(m.get("cx", 0), eye_w), principal_norm(m.get("cy", 0), eye_h)


def tally(rows):
    """rows: フレームごとの値リスト（track 番号昇順）。昇順なら正解。"""
    n = len(rows)
    if n == 0:
        return None
    strict = tie = inv = 0
    pair_ok = pair_tot = 0
    for v in rows:
        k = len(v)
        if all(v[i] < v[i + 1] for i in range(k - 1)):
            strict += 1
        if any(v[i] == v[j] for i, j in itertools.combinations(range(k), 2)):
            tie += 1
        if any(v[i] > v[j] for i, j in itertools.combinations(range(k), 2)):
            inv += 1
        for i, j in itertools.combinations(range(k), 2):
            pair_tot += 1
            if v[i] < v[j]:
                pair_ok += 1
    return dict(frames=n, strict=100 * strict / n, tie=100 * tie / n, inv=100 * inv / n,
                pair=100 * pair_ok / pair_tot)


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("bundle")
    ap.add_argument("--tracks", default=None,
                    help="対象 track（カンマ区切り）。省略すると全 track。")
    ap.add_argument("--ranges", default=None,
                    help="区間別に集計する（例 0-300,300-900）。")
    args = ap.parse_args()

    b = _load_full(args.bundle)
    m = b["manifest"]
    eye_w, eye_h, fx, fy, cx, cy = intrinsics(m)
    qpos = b["qpos"]
    keep = None if not args.tracks else {int(t) for t in args.tracks.split(",")}

    try:
        obs = json.loads(zipfile.ZipFile(args.bundle).read("source/placement_observations.json"))
        pre = {(f["frameIndex"], o["trackId"]): o["usedAnchor"]["z"]
               for f in obs["frames"] for o in f["objects"]}
    except Exception:
        pre = None

    per_frame = []
    for fi, objs in enumerate(b["frames"]):
        o = sorted([x for x in objs if keep is None or x["trackId"] in keep],
                   key=lambda x: x["trackId"])
        if len(o) < 2:
            continue
        z = [x["anchorZq"] * qpos for x in o]
        dist = []
        for x, zz in zip(o, z):
            X = ((x["anchorU"] / eye_w) - cx) * 2.0 * zz / fx
            Y = (cy - (x["anchorV"] / eye_h)) * 2.0 * zz / fy
            dist.append(math.sqrt(X * X + Y * Y + zz * zz))
        raw = [pre.get((fi, x["trackId"])) for x in o] if pre else None
        if raw and any(v is None for v in raw):
            raw = None
        per_frame.append((fi, z, dist, raw))

    print("%s" % args.bundle)
    print("quant_pos_scale=%g  対象 track=%s  多対象フレーム=%d"
          % (qpos, "全部" if keep is None else sorted(keep), len(per_frame)))
    if not per_frame:
        return

    ranges = [(0, len(b["frames"]), "全体")]
    if args.ranges:
        ranges = []
        for r in args.ranges.split(","):
            lo, hi = r.split("-")
            ranges.append((int(lo), int(hi), r))
        ranges.append((0, len(b["frames"]), "全体"))

    print()
    print("%-12s %-22s %6s %9s %9s %9s %9s"
          % ("区間", "指標", "n", "完全順序", "ペア一致", "同値あり", "逆転あり"))
    for lo, hi, label in ranges:
        sub = [r for r in per_frame if lo <= r[0] < hi]
        if not sub:
            continue
        series = [("camZ (量子化後)", [r[1] for r in sub]),
                  ("視点からの距離", [r[2] for r in sub])]
        if all(r[3] for r in sub):
            series.insert(1, ("camZ (量子化前 float)", [r[3] for r in sub]))
        for name, rows in series:
            t = tally(rows)
            print("%-12s %-22s %6d %8.1f%% %8.1f%% %8.1f%% %8.1f%%"
                  % (label, name, t["frames"], t["strict"], t["pair"], t["tie"], t["inv"]))
        print()


if __name__ == "__main__":
    main()
