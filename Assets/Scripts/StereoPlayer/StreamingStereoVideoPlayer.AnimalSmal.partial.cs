using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // meta.bin SMAL cache: populated per-frame during TryReadFrameObjects
    private readonly Dictionary<int, Dictionary<uint, AnimalSmalPose>> animalSmalPosesMetaBin
        = new Dictionary<int, Dictionary<uint, AnimalSmalPose>>();

    private bool TryGetAnimalSmalPose(int frameIndex, uint trackId, out AnimalSmalPose pose)
    {
        pose = default(AnimalSmalPose);
        return animalSmalPosesMetaBin.TryGetValue(frameIndex, out Dictionary<uint, AnimalSmalPose> byTrack)
            && byTrack.TryGetValue(trackId, out pose);
    }

    // Called from TryReadFrameObjects after consuming block_version (uint16).
    // Reads rotation_count, beta_count, then all SMAL floats, and stores the pose.
    // Always advances the stream by exactly (rotCount*9 + betaCount + 3) floats.
    internal void StoreSmalBlockFromBin(System.IO.BinaryReader br, int frameIndex, uint trackId)
    {
        ushort rotCount = br.ReadUInt16();
        ushort betaCount = br.ReadUInt16();

        long dataStart = br.BaseStream.Position;
        long dataBytes = (long)(rotCount * 9 + betaCount + 3) * sizeof(float);

        if (rotCount >= 1 && rotCount <= 64 && betaCount <= 64)
        {
            var pose = new AnimalSmalPose { bodyPose = new Quaternion[34] };

            // rotations[0] = global_orient: flipCameraY=true (D*R, same as SMPL)
            pose.hasGlobalOrient = TryReadRotationMatrixFromBin(br, flipCameraY: true, out pose.globalOrient);

            // rotations[1..34] = body_pose: flipCameraY=false
            int bodyCount = Mathf.Min(34, rotCount - 1);
            for (int i = 0; i < bodyCount; i++)
            {
                if (!TryReadRotationMatrixFromBin(br, flipCameraY: false, out pose.bodyPose[i]))
                    pose.bodyPose[i] = Quaternion.identity;
            }

            int extraRot = rotCount - 1 - bodyCount;
            if (extraRot > 0)
                br.BaseStream.Seek(extraRot * 9 * sizeof(float), System.IO.SeekOrigin.Current);

            if (betaCount > 0)
            {
                pose.betas = new float[betaCount];
                for (int i = 0; i < betaCount; i++)
                    pose.betas[i] = br.ReadSingle();
            }

            float tx = br.ReadSingle();
            float ty = br.ReadSingle();
            float tz = br.ReadSingle();
            pose.transl = new Vector3(tx, ty, tz);
            pose.hasTransl = IsFinite(pose.transl) && Mathf.Abs(tz) > 0.0001f;

            if (!animalSmalPosesMetaBin.TryGetValue(frameIndex, out Dictionary<uint, AnimalSmalPose> byTrack))
            {
                byTrack = new Dictionary<uint, AnimalSmalPose>();
                animalSmalPosesMetaBin[frameIndex] = byTrack;
            }
            byTrack[trackId] = pose;
        }

        long expectedEnd = dataStart + dataBytes;
        if (br.BaseStream.Position != expectedEnd)
            br.BaseStream.Seek(expectedEnd, System.IO.SeekOrigin.Begin);
    }
}
