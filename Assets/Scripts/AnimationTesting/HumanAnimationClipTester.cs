using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public sealed class HumanAnimationClipTester : MonoBehaviour
{
    public Animator animator;
    public AnimationClip[] clips;
    public int clipIndex;
    public bool autoPlay = true;
    public bool loop = true;
    public float playbackSpeed = 1f;
    public bool disableRootMotion = true;

    private PlayableGraph graph;
    private AnimationClipPlayable clipPlayable;
    private AnimationPlayableOutput output;
    private AnimationClip currentClip;

    private void Reset()
    {
        animator = GetComponentInChildren<Animator>();
    }

    private void OnEnable()
    {
        if (autoPlay)
        {
            PlayCurrentClip();
        }
    }

    private void OnDisable()
    {
        DestroyGraph();
    }

    private void Update()
    {
        if (WasNextPressed())
        {
            StepClip(1);
        }
        if (WasPreviousPressed())
        {
            StepClip(-1);
        }
        if (WasReplayPressed())
        {
            PlayCurrentClip();
        }

        if (!graph.IsValid() || currentClip == null)
        {
            return;
        }

        double nextTime = clipPlayable.GetTime() + Time.deltaTime * Mathf.Max(0f, playbackSpeed);
        if (loop && currentClip.length > 0.0001f)
        {
            nextTime %= currentClip.length;
        }
        clipPlayable.SetTime(nextTime);
        graph.Evaluate(Time.deltaTime);
    }

    public void StepClip(int delta)
    {
        if (clips == null || clips.Length == 0)
        {
            return;
        }

        clipIndex = (clipIndex + delta) % clips.Length;
        if (clipIndex < 0)
        {
            clipIndex += clips.Length;
        }
        PlayCurrentClip();
    }

    public void PlayCurrentClip()
    {
        if (animator == null)
        {
            animator = GetComponentInChildren<Animator>();
        }
        if (animator == null || clips == null || clips.Length == 0)
        {
            return;
        }

        clipIndex = Mathf.Clamp(clipIndex, 0, clips.Length - 1);
        AnimationClip clip = clips[clipIndex];
        if (clip == null)
        {
            return;
        }

        DestroyGraph();
        if (disableRootMotion)
        {
            animator.applyRootMotion = false;
        }
        animator.enabled = true;

        graph = PlayableGraph.Create("HumanAnimationClipTester");
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
        clipPlayable = AnimationClipPlayable.Create(graph, clip);
        clipPlayable.SetApplyFootIK(false);
        clipPlayable.SetApplyPlayableIK(false);
        output = AnimationPlayableOutput.Create(graph, "Animation", animator);
        output.SetSourcePlayable(clipPlayable);
        currentClip = clip;
        graph.Play();

        Debug.Log("HumanAnimationClipTester playing clip: " + clip.name + " length=" + clip.length.ToString("F2") + "s humanoidAnimator=" + animator.isHuman);
    }

    public void SetClips(IEnumerable<AnimationClip> sourceClips)
    {
        List<AnimationClip> list = new List<AnimationClip>();
        if (sourceClips != null)
        {
            foreach (AnimationClip clip in sourceClips)
            {
                if (clip != null)
                {
                    list.Add(clip);
                }
            }
        }
        clips = list.ToArray();
        clipIndex = 0;
    }

    private void DestroyGraph()
    {
        if (graph.IsValid())
        {
            graph.Destroy();
        }
        currentClip = null;
    }

    private static bool WasNextPressed()
    {
#if ENABLE_INPUT_SYSTEM
        Keyboard kb = Keyboard.current;
        return kb != null && kb.rightArrowKey != null && kb.rightArrowKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKeyDown(KeyCode.RightArrow);
#else
        return false;
#endif
    }

    private static bool WasPreviousPressed()
    {
#if ENABLE_INPUT_SYSTEM
        Keyboard kb = Keyboard.current;
        return kb != null && kb.leftArrowKey != null && kb.leftArrowKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKeyDown(KeyCode.LeftArrow);
#else
        return false;
#endif
    }

    private static bool WasReplayPressed()
    {
#if ENABLE_INPUT_SYSTEM
        Keyboard kb = Keyboard.current;
        return kb != null && kb.spaceKey != null && kb.spaceKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKeyDown(KeyCode.Space);
#else
        return false;
#endif
    }
}
