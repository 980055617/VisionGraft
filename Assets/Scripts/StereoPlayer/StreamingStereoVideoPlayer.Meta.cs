using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private struct MetaHeader
    {
        public ushort compressId;
        public ushort width;
        public ushort height;
        public float fps;
        public uint numFrames;
        public ushort eyeWidth;
        public float fovxDeg;
        public float quantPosScale;
        public float quantJointScale;
        public ulong categoryTableOffset;
        public ulong indexTableOffset;
    }

    public struct MetaObj
    {
        public uint trackId;
        public byte categoryId;
        public ushort bboxX;
        public ushort bboxY;
        public ushort bboxW;
        public ushort bboxH;
        public ushort anchorU;
        public ushort anchorV;
        public float anchorZ;  // camera-space depth in meters (anchorZq * quant_pos_scale)
        public bool hasSkeleton;
        public ushort skeletonKpCount;
        public Vector3[] jointsCam;
        public byte[] jointsVis;
        public bool hasSkeletonRootCam;
        public Vector3 skeletonRootCam;
        public bool hasOtherProxy;
        public bool hasOtherProxyCenter;
        public bool hasOtherProxySize;
        public Vector3 otherAnchorCameraXyz;
        public Vector3 otherProxyCenter;
        public Vector3 otherProxySize;
    }

    private bool metaLoaded;
    private MetaHeader metaHeader;
    private readonly Dictionary<ushort, ushort> categoryKpCounts = new Dictionary<ushort, ushort>();
    private readonly Dictionary<byte, string> categoryNames = new Dictionary<byte, string>();
    private ulong[] frameOffsets;
    private string metaFilePath;
    private readonly List<MetaObj> metaFrameObjects = new List<MetaObj>(64);
    private float GetQuantPosScale()
    {
        return GetManifestQuantPosScale();
    }

    private float GetQuantJointScale()
    {
        return manifest != null && manifest.quant_joint_scale > 0f
            ? manifest.quant_joint_scale
            : 0f;
    }

    private void LoadMeta(string metaPath)
    {
        if (!File.Exists(metaPath))
        {
            return;
        }

        metaFilePath = metaPath;
        metaLoaded = false;
        categoryKpCounts.Clear();
        categoryNames.Clear();
        frameOffsets = null;
        humanSmplPosesMetaBin.Clear();

        try
        {
            using (var fs = new FileStream(metaPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var br = new BinaryReader(fs))
            {
                metaHeader = ReadHeaderV2(br);

                ReadCategoryTable(br, metaHeader.categoryTableOffset);
                ReadIndexTable(br, metaHeader.indexTableOffset, metaHeader.numFrames);
            }

            metaLoaded = frameOffsets != null && frameOffsets.Length > 0;
        }
        catch
        {
        }
    }

    private MetaHeader ReadHeaderV2(BinaryReader br)
    {
        _ = br.ReadChars(4);   // magic
        _ = br.ReadUInt16();   // version

        MetaHeader h = new MetaHeader
        {
            compressId = br.ReadUInt16(),
            width = br.ReadUInt16(),
            height = br.ReadUInt16(),
            fps = br.ReadSingle(),
            numFrames = br.ReadUInt32(),
            eyeWidth = br.ReadUInt16()
        };

        _ = br.ReadUInt16(); // reserved
        h.fovxDeg = br.ReadSingle();
        h.quantPosScale = br.ReadSingle();
        h.quantJointScale = br.ReadSingle();
        h.categoryTableOffset = br.ReadUInt64();
        _ = br.ReadUInt32(); // categoryTableSize
        h.indexTableOffset = br.ReadUInt64();
        return h;
    }

    private void ReadCategoryTable(BinaryReader br, ulong offset)
    {
        br.BaseStream.Seek((long)offset, SeekOrigin.Begin);
        ushort entryCount = br.ReadUInt16();
        for (int i = 0; i < entryCount; i++)
        {
            ushort catId = br.ReadUInt16();
            ushort kpCount = br.ReadUInt16();
            ushort nameLen = br.ReadUInt16();
            string catName = string.Empty;
            if (nameLen > 0)
            {
                byte[] nameBytes = br.ReadBytes(nameLen);
                catName = System.Text.Encoding.UTF8.GetString(nameBytes);
            }

            ushort edgeCount = br.ReadUInt16();
            int edgeBytes = edgeCount * sizeof(ushort) * 2;
            if (edgeBytes > 0)
            {
                br.BaseStream.Seek(edgeBytes, SeekOrigin.Current);
            }

            categoryKpCounts[catId] = kpCount;
            categoryNames[(byte)catId] = catName;
        }
    }

    private void ReadIndexTable(BinaryReader br, ulong offset, uint numFrames)
    {
        br.BaseStream.Seek((long)offset, SeekOrigin.Begin);
        frameOffsets = new ulong[numFrames];
        for (int i = 0; i < numFrames; i++)
        {
            frameOffsets[i] = br.ReadUInt64();
        }
    }

    private int GetCurrentFrameIndex()
    {
        return GetCurrentPlaybackFrame();
    }

    private int GetCurrentPlaybackFrame()
    {
        return GetPlaybackFrameSnapshot().currentFrame;
    }

    private RuntimePlaybackTimeline.FrameSnapshot GetPlaybackFrameSnapshot()
    {
        float fps = metaHeader.fps > 0f ? metaHeader.fps : (manifest != null ? manifest.fps : 0f);
        return RuntimePlaybackTimeline.ResolveFrameSnapshot(
            vp != null ? vp.frame : -1L,
            vp != null ? vp.time : 0d,
            fps,
            (int)metaHeader.numFrames,
            lastFrameReadyFrame,
            UseFrameReadySync);
    }

    public bool TryReadFrameObjects(int frameIndex, List<MetaObj> outObjs)
    {
        if (!metaLoaded || frameOffsets == null || frameOffsets.Length == 0)
        {
            return false;
        }

        if (frameIndex < 0 || frameIndex >= frameOffsets.Length)
        {
            return false;
        }

        outObjs.Clear();

        try
        {
            using (var fs = new FileStream(metaFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var br = new BinaryReader(fs))
            {
                fs.Seek((long)frameOffsets[frameIndex], SeekOrigin.Begin);
                uint compressedLen = br.ReadUInt32();
                if (compressedLen == 0)
                {
                    return true;
                }

                byte[] compressed = br.ReadBytes((int)compressedLen);
                byte[] payload = DecompressPayload(compressed, metaHeader.compressId);
                if (payload == null)
                {
                    return false;
                }

                using (var ms = new MemoryStream(payload))
                using (var pr = new BinaryReader(ms))
                {
                    humanSmplPosesMetaBin.Remove(frameIndex);
                    ushort objCount = pr.ReadUInt16();
                    for (int i = 0; i < objCount; i++)
                    {
                        uint trackId = pr.ReadUInt32();
                        byte categoryId = pr.ReadByte();
                        byte flags = pr.ReadByte();
                        ushort bboxX = pr.ReadUInt16();
                        ushort bboxY = pr.ReadUInt16();
                        ushort bboxW = pr.ReadUInt16();
                        ushort bboxH = pr.ReadUInt16();
                        ushort anchorU = pr.ReadUInt16();
                        ushort anchorV = pr.ReadUInt16();
                        short anchorZq = pr.ReadInt16();
                        pr.ReadUInt16(); // anchor_scale_q
                        pr.ReadInt16(); // rot_q0
                        pr.ReadInt16(); // rot_q1
                        pr.ReadInt16(); // rot_q2
                        pr.ReadInt16(); // rot_q3

                        bool hasSkeleton = (flags & 0x1) != 0;
                        ushort kpCount = 0;
                        Vector3[] jointsCam = null;
                        byte[] jointsVis = null;
                        if (hasSkeleton)
                        {
                            if (categoryKpCounts.TryGetValue(categoryId, out ushort count))
                            {
                                kpCount = count;
                            }

                            int posBytes = kpCount * 3 * sizeof(short);
                            if (posBytes > 0)
                            {
                                float quantScale = GetQuantJointScale();
                                if (quantScale > 0f)
                                {
                                    jointsCam = new Vector3[kpCount];
                                    short qZMin = short.MaxValue;
                                    short qZMax = short.MinValue;
                                    float decZMin = float.MaxValue;
                                    float decZMax = float.MinValue;
                                    for (int p = 0; p < kpCount; p++)
                                    {
                                        short xq = pr.ReadInt16();
                                        short yq = pr.ReadInt16();
                                        short zq = pr.ReadInt16();

                                        // Quantization in bundle build is q = round(value / quantScale),
                                        // so decode must be value = q * quantScale.
                                        Vector3 decoded = new Vector3(xq * quantScale, yq * quantScale, zq * quantScale);
                                        decoded = DecodeJointCamFromBundle(decoded);
                                        jointsCam[p] = decoded;

                                        if (zq < qZMin) qZMin = zq;
                                        if (zq > qZMax) qZMax = zq;
                                        if (decoded.z < decZMin) decZMin = decoded.z;
                                        if (decoded.z > decZMax) decZMax = decoded.z;

                                    }
                                }
                                else
                                {
                                    pr.BaseStream.Seek(posBytes, SeekOrigin.Current);
                                }
                            }

                            if (kpCount > 0)
                            {
                                jointsVis = pr.ReadBytes(kpCount);
                            }

                        }

                        bool hasSmplBlock = (flags & 0x02) != 0;
                        if (hasSmplBlock)
                        {
                            pr.ReadUInt16(); // block_version
                            StoreSmplBlockFromBin(pr, frameIndex, trackId);
                        }

                        // anchor_z = anchorZq * quant_pos_scale gives camera-space depth in meters directly
                        float anchorZ = anchorZq * GetQuantPosScale();
                        MetaObj obj = new MetaObj
                        {
                            trackId = trackId,
                            categoryId = categoryId,
                            bboxX = bboxX,
                            bboxY = bboxY,
                            bboxW = bboxW,
                            bboxH = bboxH,
                            anchorU = anchorU,
                            anchorV = anchorV,
                            anchorZ = anchorZ,
                            hasSkeleton = hasSkeleton,
                            skeletonKpCount = kpCount,
                            jointsCam = jointsCam,
                            jointsVis = jointsVis
                        };

                        ApplySidecarsToMetaObject(ref obj, frameIndex);
                        outObjs.Add(obj);
                    }
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private byte[] DecompressPayload(byte[] compressed, ushort compressId)
    {
        switch (compressId)
        {
            case 0:
                return compressed;
            case 1:
                return DecompressZlib(compressed);
            case 2:
                return DecompressLz4Frame(compressed);
            default:
                return null;
        }
    }

    private byte[] DecompressZlib(byte[] input)
    {
        if (input == null || input.Length < 6)
        {
            return null;
        }

        int deflateLen = input.Length - 6;
        if (deflateLen <= 0)
        {
            return null;
        }

        using (var ms = new MemoryStream(input, 2, deflateLen))
        using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
        using (var outMs = new MemoryStream())
        {
            ds.CopyTo(outMs);
            return outMs.ToArray();
        }
    }

    private byte[] DecompressLz4Frame(byte[] input)
    {
        Type lz4StreamType = Type.GetType("K4os.Compression.LZ4.Streams.LZ4Stream, K4os.Compression.LZ4.Streams");
        if (lz4StreamType == null)
        {
            return null;
        }

        using (var inputStream = new MemoryStream(input))
        using (var outStream = new MemoryStream())
        {
            var methods = lz4StreamType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            object decoded = null;
            foreach (var method in methods)
            {
                if (method.Name != "Decode")
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (parameters.Length < 1 || !typeof(Stream).IsAssignableFrom(parameters[0].ParameterType))
                {
                    continue;
                }

                object[] args = new object[parameters.Length];
                args[0] = inputStream;
                for (int i = 1; i < parameters.Length; i++)
                {
                    var p = parameters[i];
                    if (p.HasDefaultValue)
                    {
                        args[i] = p.DefaultValue;
                    }
                    else if (p.ParameterType == typeof(bool))
                    {
                        args[i] = true;
                    }
                    else if (p.ParameterType == typeof(int))
                    {
                        args[i] = 0;
                    }
                    else if (p.ParameterType.IsEnum)
                    {
                        args[i] = Activator.CreateInstance(p.ParameterType);
                    }
                    else
                    {
                        args[i] = null;
                    }
                }

                try
                {
                    decoded = method.Invoke(null, args);
                }
                catch
                {
                    decoded = null;
                }

                if (decoded is Stream)
                {
                    break;
                }
            }

            if (decoded is Stream decodedStream)
            {
                decodedStream.CopyTo(outStream);
                decodedStream.Dispose();
                return outStream.ToArray();
            }
        }

        return null;
    }

    private void TrySelectDisplayTrackFromPick(PickResult pick)
    {
        if (!displayModel || !SelectDisplayTrackFromClick || !metaLoaded)
        {
            return;
        }
        if (displayTrackIds != null && displayTrackIds.Length > 0)
        {
            return;
        }

        int frame = GetCurrentPlaybackFrame();
        if (!TryReadFrameObjects(frame, metaFrameObjects) || metaFrameObjects.Count == 0)
        {
            return;
        }

        float bestDistSq = DisplayTrackSelectThresholdPixels * DisplayTrackSelectThresholdPixels;
        int bestTrack = -1;
        for (int i = 0; i < metaFrameObjects.Count; i++)
        {
            MetaObj obj = metaFrameObjects[i];
            float dx = obj.anchorU - pick.pixel.x;
            float dy = obj.anchorV - pick.pixel.y;
            float distSq = dx * dx + dy * dy;
            if (distSq <= bestDistSq)
            {
                bestDistSq = distSq;
                bestTrack = (int)obj.trackId;
            }
        }

        if (bestTrack >= 0)
        {
            displayTrackIds = new[] { bestTrack };
        }
    }
}
