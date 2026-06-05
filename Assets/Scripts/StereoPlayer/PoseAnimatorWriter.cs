using UnityEngine;
using UnityEngine.Playables;

public static class PoseAnimatorWriter
{
    public static void ApplyEnabled(Animator animator, bool enabled)
    {
        if (animator == null)
        {
            return;
        }

        animator.enabled = enabled;
    }

    public static void ApplyRootMotion(Animator animator, bool applyRootMotion)
    {
        if (animator == null)
        {
            return;
        }

        animator.applyRootMotion = applyRootMotion;
    }

    public static void ApplyPlay(PlayableGraph graph)
    {
        if (!graph.IsValid())
        {
            return;
        }

        graph.Play();
    }
}
