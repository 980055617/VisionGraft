import struct, zlib, sys

PATH = r"C:\Users\y9800\AppData\LocalLow\DefaultCompany\stereoCrafter\svb_cache\meta.bin"

def read_header(f):
    magic = f.read(4)
    version = struct.unpack('<H', f.read(2))[0]
    compress_id, width, height = struct.unpack('<HHH', f.read(6))
    fps = struct.unpack('<f', f.read(4))[0]
    num_frames = struct.unpack('<I', f.read(4))[0]
    eye_width = struct.unpack('<H', f.read(2))[0]
    f.read(2)  # reserved
    fovx = struct.unpack('<f', f.read(4))[0]
    quant_pos_scale = struct.unpack('<f', f.read(4))[0]
    quant_joint_scale = struct.unpack('<f', f.read(4))[0]
    category_table_offset = struct.unpack('<Q', f.read(8))[0]
    category_table_size = struct.unpack('<I', f.read(4))[0]
    index_table_offset = struct.unpack('<Q', f.read(8))[0]
    return dict(magic=magic, version=version, compress_id=compress_id, width=width, height=height,
                fps=fps, num_frames=num_frames, eye_width=eye_width, fovx=fovx,
                quant_pos_scale=quant_pos_scale, quant_joint_scale=quant_joint_scale,
                category_table_offset=category_table_offset, index_table_offset=index_table_offset)

def read_category_table(f, offset):
    f.seek(offset)
    entry_count = struct.unpack('<H', f.read(2))[0]
    cats = {}
    for _ in range(entry_count):
        cat_id, kp_count, name_len = struct.unpack('<HHH', f.read(6))
        name = f.read(name_len).decode('utf-8') if name_len else ''
        edge_count = struct.unpack('<H', f.read(2))[0]
        f.read(edge_count * 4)
        cats[cat_id] = (kp_count, name)
    return cats

def read_index_table(f, offset, num_frames):
    f.seek(offset)
    return struct.unpack('<%dQ' % num_frames, f.read(8 * num_frames))

def read_rotmat(pr, flip_camera_y):
    vals = struct.unpack('<9f', pr.read(36))
    m00,m01,m02,m10,m11,m12,m20,m21,m22 = vals
    if flip_camera_y:
        m10,m11,m12 = -m10,-m11,-m12
    return (m00,m01,m02,m10,m11,m12,m20,m21,m22)

def rotmat_to_angle_deg(m):
    # angle of rotation matrix via trace
    m00,m01,m02,m10,m11,m12,m20,m21,m22 = m
    trace = m00+m11+m22
    cos_theta = max(-1.0, min(1.0, (trace - 1.0) / 2.0))
    import math
    return math.degrees(math.acos(cos_theta))

class ByteReader:
    def __init__(self, data):
        self.data = data
        self.pos = 0
    def read(self, n):
        b = self.data[self.pos:self.pos+n]
        self.pos += n
        return b
    def u16(self):
        v = struct.unpack('<H', self.read(2))[0]
        return v
    def u32(self):
        return struct.unpack('<I', self.read(4))[0]
    def u8(self):
        return self.read(1)[0]
    def i16(self):
        return struct.unpack('<h', self.read(2))[0]

def main():
    with open(PATH, 'rb') as f:
        header = read_header(f)
        print("HEADER", header)
        cats = read_category_table(f, header['category_table_offset'])
        print("CATS", cats)
        offsets = read_index_table(f, header['index_table_offset'], header['num_frames'])
        print("num_frames", header['num_frames'], "first offsets", offsets[:5])

        sample_frames = list(range(0, min(header['num_frames'], 60), 6))
        for frame_idx in sample_frames:
            off = offsets[frame_idx]
            f.seek(off)
            compressed_len = struct.unpack('<I', f.read(4))[0]
            if compressed_len == 0:
                continue
            compressed = f.read(compressed_len)
            if header['compress_id'] == 0:
                payload = compressed
            elif header['compress_id'] == 1:
                payload = zlib.decompress(compressed)
            else:
                print("unsupported compress_id", header['compress_id'])
                return

            pr = ByteReader(payload)
            obj_count = pr.u16()
            for i in range(obj_count):
                track_id = pr.u32()
                category_id = pr.u8()
                flags = pr.u8()
                bbox = struct.unpack('<4H', pr.read(8))
                anchor_u, anchor_v = struct.unpack('<2H', pr.read(4))
                anchor_zq = pr.i16()
                pr.read(2)  # anchor_scale_q
                pr.read(8)  # rot_q0..3

                has_skeleton = (flags & 0x1) != 0
                kp_count = 0
                if has_skeleton:
                    kp_count = cats.get(category_id, (0,''))[0]
                    pos_bytes = kp_count * 3 * 2
                    pr.read(pos_bytes)
                    if kp_count > 0:
                        pr.read(kp_count)  # vis bytes

                has_smpl = (flags & 0x02) != 0
                if has_smpl:
                    block_version = pr.u16()
                    rot_count = pr.u16()
                    beta_count = pr.u16()
                    data_start = pr.pos
                    data_bytes = (rot_count*9 + beta_count + 3) * 4
                    pr.pos = data_start + data_bytes

                has_smal = (flags & 0x04) != 0
                if has_smal:
                    block_version = pr.u16()
                    rot_count = pr.u16()
                    beta_count = pr.u16()
                    data_start = pr.pos
                    print(f"frame={frame_idx} track={track_id} cat={category_id} SMAL rot_count={rot_count} beta_count={beta_count}")
                    # rotations[0] = global_orient
                    go = read_rotmat(pr, flip_camera_y=True)
                    print(f"  joint0(global_orient) angle={rotmat_to_angle_deg(go):.1f}deg")
                    body_count = min(34, rot_count - 1)
                    angles = []
                    for j in range(body_count):
                        m = read_rotmat(pr, flip_camera_y=False)
                        ang = rotmat_to_angle_deg(m)
                        angles.append(ang)
                    extra = rot_count - 1 - body_count
                    if extra > 0:
                        pr.read(extra*9*4)
                    if beta_count > 0:
                        pr.read(beta_count*4)
                    tx,ty,tz = struct.unpack('<3f', pr.read(12))
                    # print joint angle table (SMAL joint index = array index + 1)
                    label = ' '.join(f"j{idx+1}={a:5.1f}" for idx,a in enumerate(angles[:16]))
                    print("  " + label)
                    data_end = data_start + (rot_count*9 + beta_count + 3)*4
                    pr.pos = data_end

if __name__ == '__main__':
    main()
