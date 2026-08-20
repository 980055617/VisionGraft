#!/usr/bin/env python3
"""bundle (.svb) の anchor_z が被写体の距離を表しているかを検証する。

Unity 側（VisionGraft）で「ボールが人モデルに埋もれる」症状を追った結果、
Human track の anchor_z が距離とまったく相関していないことが分かった。
その再現・回帰確認用。生成側・利用側の両方で走らせることを想定している。

同じフォルダの README.md（bundle 共有ノート）の D-001 とセットで運用する。
依存なし（標準ライブラリのみ）。

使い方:
    python bundle_depth_check.py path/to/bundle.svb [more.svb ...]

判定基準（最重要）:
    corr(z01, 経過時間) が 0 に近いこと。
    これが大きいと、クリップ全体を通して深度が一方向へ流れている
    （チャンク分割のアフィン合わせで蓄積するドリフト）。
    2026-08-17 時点の bundle_human.svb は person で +0.657。

補助的な指標:
    corr(SMPL/SMAL transl.z, z01)  → +1 に近いべき（符号は bundle 世代で正規化済み）
    corr(bbox 径, z01)             → -1 に近いべき（実サイズ一定の物体なら径 ∝ 1/距離）
    corr(transl.z, 1/bboxH)        → +1 に近いべき。これは 2 つの独立推定の一致度なので、
                                     ここが高いのに transl.z ↔ z01 が低ければ z01 側の問題。
                                     ここ自体が低い場合は transl.z を基準にできない
                                     （フレーム数が少ない、推定が不安定 等）。
"""
import json
import math
import statistics as st
import struct
import sys
import zipfile
import zlib

MIN_SAMPLES = 60          # これ未満のフレーム数の track は報告しない
DRIFT_LIMIT = 0.20        # |corr(z01, 時間)| がこれを超えたらドリフトありとみなす


# ---------------------------------------------------------------- bundle 読み込み

class Bundle:
    """meta.bin + manifest.json を読む最小のパーサ。"""

    def __init__(self, path):
        z = zipfile.ZipFile(path)
        self.path = path
        self.meta = z.read("meta.bin")
        self.manifest = json.loads(z.read("manifest.json"))
        self._read_header()
        self._read_categories()
        self._read_index()
        self.qpos = self.manifest.get("quant_pos_scale", self.h["quantPosScale"])
        self.qjoint = self.manifest.get("quant_joint_scale", self.h["quantJointScale"])
        self.fps = self.manifest.get("fps", self.h["fps"]) or 30.0
        # depth_policy を持つ世代は anchor_z が larger=farther に統一されている
        self.larger_is_farther = "depth_policy" in self.manifest

    def _u(self, fmt):
        n = struct.calcsize("<" + fmt)
        v = struct.unpack_from("<" + fmt, self.meta, self.o)
        self.o += n
        return v[0] if len(v) == 1 else v

    def _read_header(self):
        self.o = 6  # magic(4) + version(2)
        h = {}
        for key, fmt in (("compressId", "H"), ("width", "H"), ("height", "H"),
                         ("fps", "f"), ("numFrames", "I"), ("eyeWidth", "H")):
            h[key] = self._u(fmt)
        self.o += 2  # reserved
        for key, fmt in (("fovxDeg", "f"), ("quantPosScale", "f"),
                         ("quantJointScale", "f"), ("categoryTableOffset", "Q")):
            h[key] = self._u(fmt)
        self.o += 4  # categoryTableSize
        h["indexTableOffset"] = self._u("Q")
        self.h = h

    def _read_categories(self):
        self.o = self.h["categoryTableOffset"]
        self.cats = {}
        for _ in range(self._u("H")):
            cid, kp, name_len = self._u("H"), self._u("H"), self._u("H")
            name = bytes(self.meta[self.o:self.o + name_len]).decode("utf-8") if name_len else ""
            self.o += name_len
            # self.o += self._u(...) と書くと左辺が先に読まれて _u の前進が消える
            edge_count = self._u("H")
            self.o += edge_count * 4
            self.cats[cid] = (kp, name)

    def _read_index(self):
        self.o = self.h["indexTableOffset"]
        self.offsets = [self._u("Q") for _ in range(self.h["numFrames"])]

    def frame(self, index):
        offset = self.offsets[index]
        (length,) = struct.unpack_from("<I", self.meta, offset)
        if length == 0:
            return []
        payload = zlib.decompress(self.meta[offset + 4: offset + 4 + length])
        pos = 0

        def u(fmt):
            nonlocal pos
            n = struct.calcsize("<" + fmt)
            v = struct.unpack_from("<" + fmt, payload, pos)
            pos += n
            return v[0] if len(v) == 1 else v

        objects = []
        for _ in range(u("H")):
            obj = dict(trackId=u("I"), cat=u("B"))
            flags = u("B")
            for key in ("bboxX", "bboxY", "bboxW", "bboxH", "anchorU", "anchorV"):
                obj[key] = u("H")
            zq = u("h")
            pos += 2 + 8  # anchor_scale_q, rot_q0..3
            if flags & 0x1:
                kp = self.cats.get(obj["cat"], (0, ""))[0]
                if kp:
                    raw = struct.unpack_from("<%dh" % (kp * 3), payload, pos)
                    pos += kp * 6
                    obj["joints"] = [
                        (raw[i * 3] * self.qjoint,
                         raw[i * 3 + 1] * self.qjoint,
                         raw[i * 3 + 2] * self.qjoint) for i in range(kp)]
                    pos += kp  # visibility
            for bit in (0x2, 0x4):     # SMPL block / SMAL block
                if flags & bit:
                    pos += 2  # block_version
                    rot_count, beta_count = u("H"), u("H")
                    body = rot_count * 36 + beta_count * 4
                    obj["transl"] = struct.unpack_from("<3f", payload, pos + body)
                    pos += body + 12
            obj["z01"] = zq * self.qpos
            obj["catName"] = self.cats.get(obj["cat"], (0, "?"))[1]
            objects.append(obj)
        return objects


# ---------------------------------------------------------------- 統計

def corr(xs, ys):
    if len(xs) < 3:
        return float("nan")
    mx, my = st.mean(xs), st.mean(ys)
    num = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    dx = math.sqrt(sum((x - mx) ** 2 for x in xs))
    dy = math.sqrt(sum((y - my) ** 2 for y in ys))
    return num / (dx * dy) if dx and dy else float("nan")


def second_median(samples, key):
    """[(t, rec), ...] を 1 秒ごとの median に落とす。フレーム単位のノイズを潰す。"""
    buckets = {}
    for t, rec in samples:
        if key in rec:
            buckets.setdefault(int(t), []).append(rec[key])
    return [(k, st.median(v)) for k, v in sorted(buckets.items())]


def aligned(a, b):
    da, db = dict(a), dict(b)
    keys = sorted(set(da) & set(db))
    return [da[k] for k in keys], [db[k] for k in keys]


# ---------------------------------------------------------------- 本体

def check(path):
    b = Bundle(path)
    n = b.h["numFrames"]
    sign = 1.0 if b.larger_is_farther else -1.0

    shots = b.manifest.get("shots") or []
    print("=" * 78)
    print(path)
    print(f"  frames={n}  fps={b.fps:g}  {n / b.fps:.0f}s  "
          f"shots={len(shots) if shots else 'なし（旧世代 = 全編 1 shot 扱い）'}")
    print(f"  anchor_z: larger = "
          f"{'farther' if b.larger_is_farther else 'NEARER (旧世代)'}")
    print(f"  ※ 相関の符号は世代差を吸収済み（transl.z とは正、bbox 径とは負が正しい）")

    tracks = {}
    for f in range(n):
        t = f / b.fps
        for o in b.frame(f):
            rec = {"z01": o["z01"], "bboxH": o["bboxH"],
                   "d": 0.5 * (o["bboxW"] + o["bboxH"])}
            if "transl" in o:
                rec["tz"] = o["transl"][2]
            if "joints" in o and o["joints"]:
                ys = [p[1] for p in o["joints"]]
                rec["kp"] = max(ys) - min(ys)
            tracks.setdefault((o["trackId"], o["catName"]), []).append((t, rec))

    for (tid, cat), samples in sorted(tracks.items()):
        if len(samples) < MIN_SAMPLES:
            continue
        z = second_median(samples, "z01")
        if len(z) < 5:
            continue
        print(f"\n  --- track {tid} ({cat})  {len(samples)} frames / {len(z)} sec ---")

        # [判定] 距離との相関。これが主判定。
        # SMPL/SMAL の transl.z が最も信頼できる基準だが、それ自体が妥当かどうかを
        # corr(transl.z, 1/bboxH) で先に確かめる（独立推定同士の一致度）。
        tz = second_median(samples, "tz")
        judged = False
        if len(tz) >= 5:
            bh = second_median(samples, "bboxH")
            xs2, ys2 = aligned(tz, bh)
            ref = corr(xs2, [1.0 / v if v else 0.0 for v in ys2])
            if ref > 0.5:
                xs, ys = aligned(tz, z)
                c = sign * corr(xs, ys)
                print(f"    [判定] corr(transl.z, z01)        = {c:+.3f}   "
                      f"{'OK' if c > 0.5 else 'NG  <-- 距離を表していない'}")
                print(f"           （基準の妥当性 corr(transl.z, 1/bboxH) = {ref:+.3f}）")
                judged = True
            else:
                print(f"    [判定不能] corr(transl.z, 1/bboxH) = {ref:+.3f}")
                print(f"           独立推定同士が一致しないので transl.z を基準にできない"
                      f"（フレーム数不足・推定が不安定など）")
                judged = True
        if not judged:
            # 実サイズが一定の物体なら bbox 径 ∝ 1/距離。ただし画面外への切れや
            # 隠れで bbox が欠けると崩れるので、transl.z より弱い基準。
            d = second_median(samples, "d")
            xs, ys = aligned(d, z)
            c = sign * corr(xs, ys)
            print(f"    [判定] corr(bbox 径, z01)         = {c:+.3f}   "
                  f"{'OK' if c < -0.3 else 'NG  <-- 距離を表していない'}")
            print(f"           （bbox 径は画面外への切れ・隠れに弱いので参考値）")

        # [参考] 時間ドリフト。被写体が一方向へ動き続ける映像（電車が遠ざかる等）では
        # 大きくて当然なので、単独では判定に使えない。上の [判定] が NG のときに
        # 「狂い方が単調ドリフトなのか」を見分けるために使う。
        drift = corr([k for k, _ in z], [v for _, v in z])
        flag = "" if abs(drift) <= DRIFT_LIMIT else "  <-- 単調に流れている"
        print(f"    [参考] corr(z01, 経過時間)        = {drift:+.3f}{flag}")

        # 姿勢と距離を揃えたフレーム同士で z01 がどれだけ散るか
        stand = [(t, r) for t, r in samples
                 if "kp" in r and 1.45 <= r["kp"] <= 1.75 and "tz" in r]
        if len(stand) > 50:
            tzs = sorted(r["tz"] for _, r in stand)
            lo, hi = tzs[len(tzs) // 3], tzs[len(tzs) * 2 // 3]
            band = [r for _, r in stand if lo <= r["tz"] <= hi]
            if len(band) > 20:
                zs = [r["z01"] for r in band]
                print(f"    [実害] 立位 {len(stand)}f のうち transl.z が {lo:.1f}-{hi:.1f}"
                      f"（ほぼ同じ距離）の {len(band)}f:")
                print(f"           z01 が {min(zs):.3f}..{max(zs):.3f} に散る"
                      f"（幅 {max(zs) - min(zs):.3f}）")
                print(f"           = 同じ場所にいるのに深度が動く"
                      f" → 他オブジェクトとの前後関係が壊れる")

        stride = max(1, len(z) // 12)
        cells = " ".join(f"{k}s:{v:.3f}" for k, v in z[::stride])
        print(f"    [推移] {cells}")
    print()


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    for p in sys.argv[1:]:
        try:
            check(p)
        except Exception as exc:
            print(f"{p}: 読めず ({exc})")
