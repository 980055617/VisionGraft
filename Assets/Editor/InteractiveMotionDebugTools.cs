using UnityEditor;
using UnityEngine;

// Editor-only entry points to force-trigger interactive motion events at will during Play
// Mode, instead of waiting on InteractiveMotionSchedule's random interval. See
// Docs/interactive-motion-events.md "Testing" section.
public static class InteractiveMotionDebugTools
{
    [MenuItem("VisionGraft/Interactive Motion/Force Static (All Active Tracks)")]
    public static void ForceStaticAll()
    {
        ForceAll(dynamicKind: false);
    }

    [MenuItem("VisionGraft/Interactive Motion/Force Dynamic (All Active Tracks)")]
    public static void ForceDynamicAll()
    {
        ForceAll(dynamicKind: true);
    }

    private static void ForceAll(bool dynamicKind)
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("Interactive motion can only be force-triggered in Play Mode.");
            return;
        }

        StreamingStereoVideoPlayer[] players = Object.FindObjectsByType<StreamingStereoVideoPlayer>(FindObjectsSortMode.None);
        if (players.Length == 0)
        {
            Debug.LogWarning("No StreamingStereoVideoPlayer found in the scene.");
            return;
        }

        foreach (StreamingStereoVideoPlayer player in players)
        {
            player.DebugForceInteractiveMotion(dynamicKind);
        }
    }
}
