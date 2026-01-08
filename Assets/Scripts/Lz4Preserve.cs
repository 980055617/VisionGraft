using UnityEngine;
using UnityEngine.Scripting;
using System;
using System.IO;

[Preserve]
public static class Lz4Preserve
{
    // Force the LZ4 Streams assembly to be referenced so IL2CPP doesn't strip it.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Touch()
    {
        Type t = Type.GetType("K4os.Compression.LZ4.Streams.LZ4Stream, K4os.Compression.LZ4.Streams");
        if (t == null)
        {
            return;
        }

        // Access a method via reflection to ensure metadata is kept.
        _ = t.GetMethod("Decode", new[] { typeof(Stream) });
    }
}
