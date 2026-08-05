using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

public class HumanSourcePoseSidecarTests
{
    [Test]
    public void LoadConvertsHmr2CropCoordinatesToSourcePixels()
    {
        const string json =
            "{\"meta\":{\"keypoints\":{\"format\":\"hmr2_openpose25_extra19\"}}," +
            "\"frames\":[{\"frame_index\":12,\"objects\":[{" +
            "\"trackId\":7,\"box_xywh\":[100,200,40,80]," +
            "\"predKeypoints2d\":[[0.25,-0.5],[-0.25,0.5]]}]}]}";

        Dictionary<int, Dictionary<uint, HumanSourcePose2D>> result;
        using (var reader = new StringReader(json))
        {
            result = HumanSourcePoseSidecar.Load(reader);
        }

        Assert.That(result.ContainsKey(12), Is.True);
        Assert.That(result[12].ContainsKey(7), Is.True);
        HumanSourcePose2D pose = result[12][7];
        Assert.That(
            pose.keypointFormat,
            Is.EqualTo(HumanSourcePoseSidecar.Hmr2OpenPose25Extra19));
        Assert.That(pose.sourceBox, Is.EqualTo(new Rect(100f, 200f, 40f, 80f)));
        Assert.That(pose.keypoints[0], Is.EqualTo(new Vector2(140f, 200f)));
        Assert.That(pose.keypoints[1], Is.EqualTo(new Vector2(100f, 280f)));
    }
}
