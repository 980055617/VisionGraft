using NUnit.Framework;
using UnityEngine;
using UnityEngine.Playables;

public class PoseAnimatorWriterTests
{
    [Test]
    public void ApplyEnabledSetsAnimatorEnabled()
    {
        GameObject go = new GameObject("Animator", typeof(Animator));
        try
        {
            Animator animator = go.GetComponent<Animator>();

            PoseAnimatorWriter.ApplyEnabled(animator, false);

            Assert.That(animator.enabled, Is.False);
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void ApplyRootMotionSetsApplyRootMotion()
    {
        GameObject go = new GameObject("Animator", typeof(Animator));
        try
        {
            Animator animator = go.GetComponent<Animator>();
            animator.applyRootMotion = true;

            PoseAnimatorWriter.ApplyRootMotion(animator, false);

            Assert.That(animator.applyRootMotion, Is.False);
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void ApplyPlayIgnoresInvalidGraph()
    {
        Assert.DoesNotThrow(() => PoseAnimatorWriter.ApplyPlay(default(PlayableGraph)));
    }
}
