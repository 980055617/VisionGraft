"""D-008 の再現スクリプト。

同一フレーム内で `anchor_z` が近接物体を分離できているかを、
**Unity runtime と同じ式で** 3D 位置に直して確かめる。

移植元（読んで写したもの。記憶から再構成していない）:
  Assets/Scripts/StereoPlayer/PinholePlacementSpace.cs
      TryResolveProjectionIntrinsics   … fx_norm / fy_norm があればそれが最優先
      ReconstructCamLocalFromEyePixel  … x = xNdc * z / fx, y = yNdc * z / fy, z = z
  Assets/Scripts/StereoPlayer/StreamingStereoVideoPlayer.Playback.partial.cs
      ApplyMetaTarget                  … anchor_z をそのまま zMeters として渡す

つまり **anchor_z は線形にカメラ空間の Z（大きいほど遠い）として使われる。**

使い方:
    python train_depth_order.py <bundle.svb> [フレーム番号...]

出力の「視点から」は原点からのユークリッド距離。

**最後の判定行は「track 番号順＝手前から奥」を前提にしている。**
train は奥から一車両ずつ現れるのでこの前提が成り立つが、
人とボールのように無関係な対象が並ぶ bundle では意味を持たない。
その場合は camZ の値そのものを見ること。
"""
import json
import math
import struct
import sys
import zipfile
import zlib

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")


class Reader:
    def __init__(self, buf, offset=0):
        self.b = buf
        self.o = offset

    def u(self, fmt):
        n = struct.calcsize("<" + fmt)
        v = struct.unpack_from("<" + fmt, self.b, self.o)
        self.o += n
        return v[0] if len(v) == 1 else v

    def skip(self, n):
        self.o += n


def principal_norm(value, dimension):
    """PinholePlacementSpace.ResolvePrincipalPointNorm と同じ。"""
    if not value or value <= 0:
        return 0.5
    return value / dimension if value > 1 else value


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return

    path = sys.argv[1]
    frames = [int(a) for a in sys.argv[2:]] or [0, 40, 80, 120, 160, 200, 260, 320]

    b = _load_full(path)
    m = b["manifest"]
    eye_w, eye_h = m["eye_w"], m["eye_h"]
    cx = principal_norm(m.get("cx", 0), eye_w)
    cy = principal_norm(m.get("cy", 0), eye_h)

    # TryResolveProjectionIntrinsics: fx_norm / fy_norm があればそれが最優先で、
    # fovx_deg は使われない。
    if m.get("fx_norm", 0) > 0 and m.get("fy_norm", 0) > 0:
        fx, fy = m["fx_norm"], m["fy_norm"]
        src = "fx_norm/fy_norm"
    else:
        fovx = math.radians(m["fovx_deg"])
        fx = 1.0 / math.tan(fovx * 0.5)
        fy = fx * (eye_w / float(eye_h))
        src = "fovx_deg"

    print("%s" % path)
    print("eye=%dx%d fx=%.6f fy=%.6f (%s) cx=%.4f cy=%.4f quant_pos_scale=%g"
          % (eye_w, eye_h, fx, fy, src, cx, cy, b["qpos"]))
    print()
    print("%6s %6s %8s %8s %9s %9s %9s %11s"
          % ("frame", "track", "anchorU", "anchor_z", "camX", "camY", "camZ", "視点から"))

    for fi in frames:
        objs = sorted(b["frames"][fi], key=lambda o: o["trackId"])
        rows = []
        for o in objs:
            z = o["anchorZq"] * b["qpos"]
            x = ((o["anchorU"] / eye_w) - cx) * 2.0 * z / fx
            y = (cy - (o["anchorV"] / eye_h)) * 2.0 * z / fy
            rows.append((o["trackId"], o["anchorU"], z, x, y, math.sqrt(x * x + y * y + z * z)))

        for t, u, z, x, y, d in rows:
            print("%6d %6d %8d %8.4f %9.4f %9.4f %9.4f %11.4f" % (fi, t, u, z, x, y, z, d))

        near_first = [r[0] for r in sorted(rows, key=lambda r: r[5])]
        by_id = [r[0] for r in rows]
        # 前提: track 番号順＝手前から奥（train のように順に現れる場合のみ）
        print("      近い順 = %s  （track 番号順 = %s） %s"
              % (near_first, by_id,
                 "一致" if near_first == by_id else "**逆転**（track 番号順＝手前から奥の場合）"))
        print()


def _load_full(path):
    """可変長ブロックを飛ばしながら全フレームを読む。"""
    z = zipfile.ZipFile(path)
    meta = z.read("meta.bin")
    manifest = json.loads(z.read("manifest.json"))

    r = Reader(meta)
    r.skip(4)
    r.u("H"); r.u("H"); r.u("H"); r.u("H"); r.u("f")
    num_frames = r.u("I")
    r.u("H"); r.skip(2); r.u("f")
    qpos = r.u("f")
    r.u("f")  # quant_joint_scale（ここでは使わない）
    cat_off = r.u("Q")
    r.skip(4)
    idx_off = r.u("Q")

    qpos = manifest.get("quant_pos_scale", qpos)

    rc = Reader(meta, cat_off)
    cats = {}
    for _ in range(rc.u("H")):
        cid = rc.u("H"); kp = rc.u("H"); nl = rc.u("H")
        rc.skip(nl)
        rc.skip(rc.u("H") * 4)
        cats[cid] = kp

    ri = Reader(meta, idx_off)
    offsets = [ri.u("Q") for _ in range(num_frames)]

    frames = []
    for off in offsets:
        (clen,) = struct.unpack_from("<I", meta, off)
        if clen == 0:
            frames.append([])
            continue
        p = zlib.decompress(meta[off + 4: off + 4 + clen])
        r = Reader(p)
        objs = []
        for _ in range(r.u("H")):
            o = {"trackId": r.u("I"), "cat": r.u("B")}
            flags = r.u("B")
            o["bboxX"] = r.u("H"); o["bboxY"] = r.u("H")
            o["bboxW"] = r.u("H"); o["bboxH"] = r.u("H")
            o["anchorU"] = r.u("H"); o["anchorV"] = r.u("H")
            o["anchorZq"] = r.u("h")
            r.skip(2)      # anchor_scale_q
            r.skip(8)      # rot_q0..3
            if flags & 0x1:
                kp = cats.get(o["cat"], 0)
                if kp:
                    r.skip(kp * 3 * 2)   # joints
                    r.skip(kp)           # vis
            if flags & 0x2:
                r.skip(2)                # block_version
                rot_count = r.u("H")
                beta_count = r.u("H")
                r.skip(rot_count * 9 * 4)
                r.skip(beta_count * 4)
                r.skip(3 * 4)            # transl
            objs.append(o)
        frames.append(objs)

    return {"manifest": manifest, "frames": frames, "qpos": qpos}


if __name__ == "__main__":
    main()
