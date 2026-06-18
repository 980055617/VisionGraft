"""
Standalone, Unity-independent sanity check: reconstruct real SMAL forward
kinematics (genuine LBS rotation+translation chain, using the actual SMAL
rest skeleton J positions) directly from meta.bin pose data, and report how
much each joint actually moves relative to the root across frames.

This bypasses every Unity-side axis-correction assumption we've been
debugging, so it answers one question cleanly: does the raw meta.bin SMAL
data, when forward-kinematic'd the way SMAL/SMPL actually define it, look
like a walking dog (large, coherent limb motion relative to the body) or not?
"""
import json
import struct
import zlib
import numpy as np

META_PATH = r"C:\Users\y9800\AppData\LocalLow\DefaultCompany\stereoCrafter\svb_cache\meta.bin"
SKELETON_PATH = r"C:\Users\y9800\Unity_project\VisionGraft\Docs\smal-rest-skeleton.json"


def read_header(f):
    f.read(4)  # magic
    struct.unpack('<H', f.read(2))[0]  # version
    compress_id, width, height = struct.unpack('<HHH', f.read(6))
    fps = struct.unpack('<f', f.read(4))[0]
    num_frames = struct.unpack('<I', f.read(4))[0]
    struct.unpack('<H', f.read(2))[0]  # eye_width
    f.read(2)  # reserved
    struct.unpack('<f', f.read(4))[0]  # fovx
    struct.unpack('<f', f.read(4))[0]  # quant_pos_scale
    struct.unpack('<f', f.read(4))[0]  # quant_joint_scale
    category_table_offset = struct.unpack('<Q', f.read(8))[0]
    struct.unpack('<I', f.read(4))[0]  # category_table_size
    index_table_offset = struct.unpack('<Q', f.read(8))[0]
    return dict(compress_id=compress_id, num_frames=num_frames,
                category_table_offset=category_table_offset,
                index_table_offset=index_table_offset)


def read_category_table(f, offset):
    f.seek(offset)
    entry_count = struct.unpack('<H', f.read(2))[0]
    cats = {}
    for _ in range(entry_count):
        cat_id, kp_count, name_len = struct.unpack('<HHH', f.read(6))
        f.read(name_len)
        edge_count = struct.unpack('<H', f.read(2))[0]
        f.read(edge_count * 4)
        cats[cat_id] = kp_count
    return cats


def read_index_table(f, offset, num_frames):
    f.seek(offset)
    return struct.unpack('<%dQ' % num_frames, f.read(8 * num_frames))


class ByteReader:
    def __init__(self, data):
        self.data = data
        self.pos = 0

    def read(self, n):
        b = self.data[self.pos:self.pos + n]
        self.pos += n
        return b

    def u16(self):
        return struct.unpack('<H', self.read(2))[0]

    def u32(self):
        return struct.unpack('<I', self.read(4))[0]

    def u8(self):
        return self.read(1)[0]

    def i16(self):
        return struct.unpack('<h', self.read(2))[0]


def read_rotmat(pr, flip_camera_y):
    vals = struct.unpack('<9f', pr.read(36))
    m = np.array(vals, dtype=np.float64).reshape(3, 3, order='F')
    # vals are m00,m01,m02,m10,m11,m12,m20,m21,m22 row-major
    m = np.array([[vals[0], vals[1], vals[2]],
                  [vals[3], vals[4], vals[5]],
                  [vals[6], vals[7], vals[8]]], dtype=np.float64)
    if flip_camera_y:
        m[1, :] = -m[1, :]
    return m


def get_smal_pose_for_frame(f, header, cats, offsets, frame_idx, target_track=None):
    off = offsets[frame_idx]
    f.seek(off)
    compressed_len = struct.unpack('<I', f.read(4))[0]
    if compressed_len == 0:
        return None
    compressed = f.read(compressed_len)
    payload = compressed if header['compress_id'] == 0 else zlib.decompress(compressed)

    pr = ByteReader(payload)
    obj_count = pr.u16()
    for _ in range(obj_count):
        track_id = pr.u32()
        category_id = pr.u8()
        flags = pr.u8()
        pr.read(8)   # bbox
        pr.read(4)   # anchor_u, anchor_v
        pr.i16()     # anchor_zq
        pr.read(2)   # anchor_scale_q
        pr.read(8)   # rot_q0..3

        has_skeleton = (flags & 0x1) != 0
        if has_skeleton:
            kp_count = cats.get(category_id, 0)
            pr.read(kp_count * 3 * 2)
            if kp_count > 0:
                pr.read(kp_count)

        has_smpl = (flags & 0x02) != 0
        if has_smpl:
            pr.u16()
            rot_count = pr.u16()
            beta_count = pr.u16()
            data_start = pr.pos
            pr.pos = data_start + (rot_count * 9 + beta_count + 3) * 4

        has_smal = (flags & 0x04) != 0
        rotmats = None
        if has_smal:
            pr.u16()
            rot_count = pr.u16()
            beta_count = pr.u16()
            data_start = pr.pos
            global_orient = read_rotmat(pr, flip_camera_y=True)
            body_count = min(34, rot_count - 1)
            body = [read_rotmat(pr, flip_camera_y=False) for _ in range(body_count)]
            extra = rot_count - 1 - body_count
            if extra > 0:
                pr.read(extra * 9 * 4)
            if beta_count > 0:
                pr.read(beta_count * 4)
            pr.read(12)  # transl
            data_end = data_start + (rot_count * 9 + beta_count + 3) * 4
            pr.pos = data_end
            rotmats = (global_orient, body)

        if (target_track is None or track_id == target_track) and rotmats is not None:
            return rotmats
    return None


def batch_rigid_transform(rot_mats, joints, parents):
    """Standard SMPL/SMPL-X LBS forward kinematics (rotation-only chain +
    rest-offset translation), mirroring smplx.lbs.batch_rigid_transform."""
    n_joints = joints.shape[0]
    rel_joints = joints.copy()
    rel_joints[1:] -= joints[parents[1:]]

    def transform_mat(R, t):
        M = np.zeros((4, 4))
        M[:3, :3] = R
        M[:3, 3] = t
        M[3, 3] = 1.0
        return M

    transforms_mat = [transform_mat(rot_mats[i], rel_joints[i]) for i in range(n_joints)]
    transform_chain = [transforms_mat[0]]
    for i in range(1, n_joints):
        curr_res = transform_chain[parents[i]] @ transforms_mat[i]
        transform_chain.append(curr_res)

    transforms = np.stack(transform_chain, axis=0)
    posed_joints = transforms[:, :3, 3]
    return posed_joints, transforms


def main():
    with open(SKELETON_PATH) as f:
        skel = json.load(f)
    n_joints = len(skel['joints'])
    J = np.array([j['position'] for j in skel['joints']], dtype=np.float64)
    parents = np.array(skel['kintree_parent_by_joint'], dtype=np.int64)
    parents[0] = 0  # root parent sentinel -> self, unused since loop starts at 1
    names = [j['name'] for j in skel['joints']]

    with open(META_PATH, 'rb') as f:
        header = read_header(f)
        cats = read_category_table(f, header['category_table_offset'])
        offsets = read_index_table(f, header['index_table_offset'], header['num_frames'])

        watch_names = ['LLeg1', 'LLeg2', 'LLeg3', 'LFoot', 'RLeg2', 'LLegBack2', 'Tail1', 'Head']
        watch_idx = [names.index(n) for n in watch_names]

        prev_pos = None
        for frame_idx in range(0, min(header['num_frames'], 90), 3):
            rotmats = get_smal_pose_for_frame(f, header, cats, offsets, frame_idx, target_track=1)
            if rotmats is None:
                continue
            global_orient, body = rotmats
            rot_mats = np.zeros((n_joints, 3, 3))
            # Force global_orient to identity so the reconstruction isolates pure local
            # articulation (body_pose only) - otherwise "root-relative" position still
            # rotates with global_orient (it rotates the offset vectors of every child),
            # which was swamping the signal we actually want to inspect here.
            rot_mats[0] = np.eye(3)
            for i in range(min(len(body), n_joints - 1)):
                rot_mats[i + 1] = body[i]
            for i in range(1 + len(body), n_joints):
                rot_mats[i] = np.eye(3)

            posed_joints, _ = batch_rigid_transform(rot_mats, J, parents)
            root_pos = posed_joints[0]
            rel = posed_joints - root_pos  # joint position relative to root, removes overall translation

            spine_angles = []
            for sj in range(1, 7):
                m = rot_mats[sj]
                tr = np.trace(m)
                cos_t = max(-1.0, min(1.0, (tr - 1.0) / 2.0))
                spine_angles.append(np.degrees(np.arccos(cos_t)))

            if prev_pos is not None:
                deltas = np.linalg.norm(rel[watch_idx] - prev_pos[watch_idx], axis=1)
                delta_str = ' '.join(f"{n}={d:.3f}" for n, d in zip(watch_names, deltas))
                spine_str = ' '.join(f"j{sj}={a:.1f}" for sj, a in zip(range(1, 7), spine_angles))
                print(f"frame={frame_idx} spineAngles[deg] {spine_str}  jointDeltaFromPrevSample(root-relative,SMALunits) {delta_str}")
            prev_pos = rel


if __name__ == '__main__':
    main()
