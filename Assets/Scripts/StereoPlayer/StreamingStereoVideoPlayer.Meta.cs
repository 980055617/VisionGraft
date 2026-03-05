using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private const string MetaMagic = "SVB1";

    private struct MetaHeader
    {
        public string magic;
        public ushort version;
        public ushort compressId;
        public ushort width;
        public ushort height;
        public float fps;
        public uint numFrames;
        public ushort eyeWidth;
        public ushort reserved;
        public float fovxDeg;
        public float quantPosScale;
        public float quantJointScale;
        public ulong categoryTableOffset;
        public uint categoryTableSize;
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
        public float anchorZRaw01;
        public float anchorZ;
        public bool hasSkeleton;
        public ushort skeletonKpCount;
        public Vector3[] jointsCam;
        public byte[] jointsVis;
    }

    private bool metaLoaded;
    private MetaHeader metaHeader;
    private readonly Dictionary<ushort, ushort> categoryKpCounts = new Dictionary<ushort, ushort>();
    private readonly Dictionary<byte, string> categoryNames = new Dictionary<byte, string>();
    private readonly Dictionary<byte, ushort[]> categoryEdges = new Dictionary<byte, ushort[]>();
    private ulong[] frameOffsets;
    private string metaFilePath;
    private readonly List<MetaObj> metaFrameObjects = new List<MetaObj>(64);
    private float GetQuantPosScale()
    {
        float manifestScale = GetManifestQuantPosScale();
        if (manifestScale > 0f)
        {
            if (verboseLog && !loggedQuantSource)
            {
                LogMeta($"QuantPosScale source=manifest quant_pos_scale={manifestScale}");
                loggedQuantSource = true;
            }
            return manifestScale;
        }

        if (metaHeader.quantPosScale > 0f)
        {
            if (verboseLog && !loggedQuantSource)
            {
                LogMeta($"QuantPosScale source=metaHeader quant_pos_scale={metaHeader.quantPosScale}");
                loggedQuantSource = true;
            }
            return metaHeader.quantPosScale;
        }

        Debug.LogWarning("QuantPosScale not available; anchorZ will be zero.");
        return 0f;
    }

    private float GetQuantJointScale()
    {
        if (metaHeader.quantJointScale > 0f)
        {
            return metaHeader.quantJointScale;
        }

        if (manifest != null && manifest.joints_quant_scale > 0f)
        {
            return manifest.joints_quant_scale;
        }

        if (manifest != null && manifest.quant_joint_scale > 0f)
        {
            return manifest.quant_joint_scale;
        }

        if (fallbackQuantJointScale > 0f)
        {
            return fallbackQuantJointScale;
        }

        return 0f;
    }

    private void LoadMeta(string metaPath)
    {
        if (!File.Exists(metaPath))
        {
            Debug.LogWarning($"Meta not found. path={metaPath}");
            return;
        }

        metaFilePath = metaPath;
        metaLoaded = false;
        categoryKpCounts.Clear();
        categoryNames.Clear();
        categoryEdges.Clear();
        frameOffsets = null;

        try
        {
            using (var fs = new FileStream(metaPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var br = new BinaryReader(fs))
            {
                metaHeader = ReadHeaderV2(br);
                if (metaHeader.magic != MetaMagic || metaHeader.version != 2)
                {
                    Debug.LogWarning($"Meta header unexpected. magic={metaHeader.magic} version={metaHeader.version}");
                }

                ReadCategoryTable(br, metaHeader.categoryTableOffset);
                ReadIndexTable(br, metaHeader.indexTableOffset, metaHeader.numFrames);
            }

            metaLoaded = frameOffsets != null && frameOffsets.Length > 0;
            LogMeta($"Meta loaded. compress={metaHeader.compressId} frames={metaHeader.numFrames} eyeW={metaHeader.eyeWidth} fps={metaHeader.fps:F2}");
            LogMeta($"Meta categories={categoryKpCounts.Count} indexTable={(frameOffsets != null ? frameOffsets.Length : 0)}");

            if (metaLoaded && TryReadFrameObjects(0, metaFrameObjects))
            {
                LogMeta($"Meta frame0 obj_count={metaFrameObjects.Count}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Meta load failed. path={metaPath} ({ex.Message})");
        }
    }

    private MetaHeader ReadHeaderV2(BinaryReader br)
    {
        MetaHeader h = new MetaHeader
        {
            magic = new string(br.ReadChars(4)),
            version = br.ReadUInt16(),
            compressId = br.ReadUInt16(),
            width = br.ReadUInt16(),
            height = br.ReadUInt16(),
            fps = br.ReadSingle(),
            numFrames = br.ReadUInt32(),
            eyeWidth = br.ReadUInt16(),
            reserved = br.ReadUInt16(),
            fovxDeg = br.ReadSingle(),
            quantPosScale = br.ReadSingle(),
            quantJointScale = br.ReadSingle(),
            categoryTableOffset = br.ReadUInt64(),
            categoryTableSize = br.ReadUInt32(),
            indexTableOffset = br.ReadUInt64()
        };
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
            ushort[] edges = null;
            if (edgeBytes > 0)
            {
                edges = new ushort[edgeCount * 2];
                for (int e = 0; e < edgeCount; e++)
                {
                    edges[e * 2] = br.ReadUInt16();
                    edges[e * 2 + 1] = br.ReadUInt16();
                }
            }

            categoryKpCounts[catId] = kpCount;
            categoryNames[(byte)catId] = catName;
            categoryEdges[(byte)catId] = edges ?? Array.Empty<ushort>();
        }
    }

    private bool TryGetCategoryEdges(byte categoryId, out ushort[] edges)
    {
        if (categoryEdges.TryGetValue(categoryId, out edges) && edges != null && edges.Length >= 2)
        {
            return true;
        }

        edges = null;
        return false;
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
        if (vp != null && vp.frame >= 0)
        {
            return (int)Mathf.Clamp((float)vp.frame, 0f, metaHeader.numFrames - 1);
        }

        float fps = metaHeader.fps > 0f ? metaHeader.fps : (manifest != null ? manifest.fps : 0f);
        if (vp != null && fps > 0f)
        {
            int frame = Mathf.FloorToInt((float)vp.time * fps);
            return Mathf.Clamp(frame, 0, (int)metaHeader.numFrames - 1);
        }

        return 0;
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

                        float anchorZRaw01 = anchorZq * GetQuantPosScale();
                        float anchorZ = DecodeAnchorDepthMetersFromBundle(anchorZRaw01);
                        outObjs.Add(new MetaObj
                        {
                            trackId = trackId,
                            categoryId = categoryId,
                            bboxX = bboxX,
                            bboxY = bboxY,
                            bboxW = bboxW,
                            bboxH = bboxH,
                            anchorU = anchorU,
                            anchorV = anchorV,
                            anchorZRaw01 = anchorZRaw01,
                            anchorZ = anchorZ,
                            hasSkeleton = hasSkeleton,
                            skeletonKpCount = kpCount,
                            jointsCam = jointsCam,
                            jointsVis = jointsVis
                        });
                    }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Meta frame read failed. frame={frameIndex} ({ex.Message})");
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
                Debug.LogError($"Meta decompress unsupported. compress_id={compressId}");
                return null;
        }
    }

    private byte[] DecompressZlib(byte[] input)
    {
        if (input == null || input.Length < 6)
        {
            Debug.LogError("Meta zlib data too small.");
            return null;
        }

        int deflateLen = input.Length - 6;
        if (deflateLen <= 0)
        {
            Debug.LogError("Meta zlib data length invalid.");
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
            Debug.LogError("Meta lz4 requested but K4os.Compression.LZ4.Streams not found.");
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

        Debug.LogError("Meta lz4 decode method not found or failed.");
        return null;
    }

    private void TrySelectFollowTrackFromPick(PickResult pick)
    {
        if (!useMetaFollow || !followNearestToClick || !metaLoaded)
        {
            return;
        }
        if (followTrackId >= 0)
        {
            return;
        }

        int frame = GetCurrentFrameIndex();
        if (!TryReadFrameObjects(frame, metaFrameObjects) || metaFrameObjects.Count == 0)
        {
            return;
        }

        float bestDistSq = followSelectThresholdPixels * followSelectThresholdPixels;
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
            followTrackId = bestTrack;
            LogMeta($"Meta pick track: trackId={followTrackId} frame={frame}");
        }
        else
        {
            LogMeta("Meta pick track: no match within threshold.");
        }
    }
}
