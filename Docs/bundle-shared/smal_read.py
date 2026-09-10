# -*- coding: utf-8 -*-
"""meta.bin の SMAL / SMPL block を読む。

移植元（読んで写した。記憶から再構成していない）:
  StreamingStereoVideoPlayer.Meta.cs      … flags 0x1=skeleton / 0x2=SMPL / 0x4=SMAL
  StreamingStereoVideoPlayer.AnimalSmal.partial.cs
      StoreSmalBlockFromBin … block_version(u16), rotCount(u16), betaCount(u16),
                              rotCount*9 float, betaCount float, transl 3 float
      rotations[0] = global_orient、rotations[1..34] = body_pose
      **bodyPose[i] は SMAL joint i+1**（AnimalSmalFkApplier: bodyPoseIdx = joint - 1）
"""
import json, math, struct, zipfile, zlib


class R:
    def __init__(s, b, o=0): s.b, s.o = b, o
    def u(s, f):
        n = struct.calcsize("<" + f); v = struct.unpack_from("<" + f, s.b, s.o); s.o += n
        return v[0] if len(v) == 1 else v
    def skip(s, n): s.o += n


def _block(r):
    r.u("H")                       # block_version
    rot = r.u("H"); beta = r.u("H")
    rots = [[r.u("f") for _ in range(9)] for _ in range(rot)]
    betas = [r.u("f") for _ in range(beta)]
    transl = [r.u("f") for _ in range(3)]
    return {"rots": rots, "betas": betas, "transl": transl}


def load(path):
    z = zipfile.ZipFile(path)
    meta = z.read("meta.bin"); manifest = json.loads(z.read("manifest.json"))
    r = R(meta); r.skip(4)
    r.u("H"); r.u("H"); r.u("H"); r.u("H"); r.u("f")
    num = r.u("I"); r.u("H"); r.skip(2); r.u("f")
    qpos = r.u("f"); qjoint = r.u("f")
    cat_off = r.u("Q"); r.skip(4); idx_off = r.u("Q")

    rc = R(meta, cat_off); cats = {}
    for _ in range(rc.u("H")):
        cid = rc.u("H"); kp = rc.u("H"); nl = rc.u("H")
        name = meta[rc.o:rc.o + nl].decode("utf-8", "replace"); rc.skip(nl)
        rc.skip(rc.u("H") * 4)
        cats[cid] = (kp, name)

    ri = R(meta, idx_off); offs = [ri.u("Q") for _ in range(num)]
    frames = []
    for off in offs:
        (clen,) = struct.unpack_from("<I", meta, off)
        if clen == 0:
            frames.append([]); continue
        p = zlib.decompress(meta[off + 4: off + 4 + clen]); r = R(p); objs = []
        for _ in range(r.u("H")):
            o = {"trackId": r.u("I"), "cat": r.u("B")}
            flags = r.u("B"); o["flags"] = flags
            o["bboxX"] = r.u("H"); o["bboxY"] = r.u("H")
            o["bboxW"] = r.u("H"); o["bboxH"] = r.u("H")
            o["anchorU"] = r.u("H"); o["anchorV"] = r.u("H"); o["anchorZq"] = r.u("h")
            r.skip(2); r.skip(8)
            if flags & 0x1:
                kp = cats.get(o["cat"], (0, ""))[0]
                if kp:
                    o["joints"] = [r.u("hhh") for _ in range(kp)]
                    o["vis"] = [r.u("B") for _ in range(kp)]
            if flags & 0x2:
                o["smpl"] = _block(r)
            if flags & 0x4:
                o["smal"] = _block(r)
            objs.append(o)
        frames.append(objs)
    return {"manifest": manifest, "frames": frames, "cats": cats,
            "qpos": qpos, "qjoint": qjoint}


def rot_angle_deg(m):
    """3x3 回転行列の回転角。trace = 1 + 2cos(theta)。"""
    c = max(-1.0, min(1.0, (m[0] + m[4] + m[8] - 1.0) / 2.0))
    return math.degrees(math.acos(c))


def rel_angle_deg(a, b):
    """a から b への相対回転の角度（a^T b）。"""
    at = [a[0], a[3], a[6], a[1], a[4], a[7], a[2], a[5], a[8]]
    p = [sum(at[i * 3 + k] * b[k * 3 + j] for k in range(3)) for i in range(3) for j in range(3)]
    return rot_angle_deg(p)
