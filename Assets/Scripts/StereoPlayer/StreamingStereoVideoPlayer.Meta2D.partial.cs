using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: metaFrameObjects/debug overlay state and manifest helpers
    // Provides: meta 2D capture, joints 2D projection/logging, GUI-space mapping

    private void LogMeta2DFrameSummaryOnce(int frame)
    {
        if (!debugDrawMeta2D || frame == lastMeta2DLogFrame || metaFrameObjects == null || metaFrameObjects.Count == 0 || manifest == null)
        {
            return;
        }

        lastMeta2DLogFrame = frame;
        int minU = int.MaxValue;
        int maxU = int.MinValue;
        int minV = int.MaxValue;
        int maxV = int.MinValue;
        for (int i = 0; i < metaFrameObjects.Count; i++)
        {
            MetaObj obj = metaFrameObjects[i];
            minU = Mathf.Min(minU, obj.anchorU);
            maxU = Mathf.Max(maxU, obj.anchorU);
            minV = Mathf.Min(minV, obj.anchorV);
            maxV = Mathf.Max(maxV, obj.anchorV);
        }

        Debug.Log(
            $"META_2D frame={frame} eye=({manifest.eye_w},{manifest.eye_h}) crop=({GetCropW()},{GetCropH()}) cropXY=({GetCropX()},{GetCropY()}) " +
            $"anchorU=[{minU},{maxU}] anchorV=[{minV},{maxV}] toggles(norm={uvIsNormalized},flipU={flipU},flipV={flipV},applyCropScale={applyCropScale})");
    }


    private void BuildJoints2DOverlayAndLog(int frame)
    {
        if (!debugDrawJoints2D || manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0 || metaFrameObjects == null || metaFrameObjects.Count == 0)
        {
            return;
        }

        bool hasFxFy = TryGetProjectionIntrinsics(out float fx, out float fy, out float cx, out float cy);
        bool hasManifestNormIntrinsics = TryGetManifestNormalizedIntrinsics(out float fxNorm, out float fyNorm, out int intrEyeW, out int intrEyeH);
        int totalKp = 0;
        int totalValid = 0;
        int insideCount = 0;
        int zNonPositiveSkipped = 0;
        int zEq0Skipped = 0;
        float minAnchorZ = float.MaxValue;
        float maxAnchorZ = float.MinValue;
        float minX = float.MaxValue;
        float maxX = float.MinValue;
        float minY = float.MaxValue;
        float maxY = float.MinValue;
        float minZ = float.MaxValue;
        float maxZ = float.MinValue;
        bool hasJointSamples = false;
        float projMinX = float.MaxValue;
        float projMaxX = float.MinValue;
        float projMinY = float.MaxValue;
        float projMaxY = float.MinValue;
        float projMinZ = float.MaxValue;
        float projMaxZ = float.MinValue;
        bool hasProjectedSourceSamples = false;
        int processedSourceObjCount = 0;
        int rawSourceObjCount = 0;
        bool loggedRefSet = false;
        float logRefBboxW = 0f;
        float logRefBboxH = 0f;
        float logRefAnchorU = 0f;
        float logRefAnchorV = 0f;

        for (int i = 0; i < metaFrameObjects.Count; i++)
        {
            MetaObj obj = metaFrameObjects[i];
            minAnchorZ = Mathf.Min(minAnchorZ, obj.anchorZ);
            maxAnchorZ = Mathf.Max(maxAnchorZ, obj.anchorZ);

            if (!obj.hasSkeleton || obj.jointsCam == null || obj.jointsVis == null || obj.skeletonKpCount <= 0)
            {
                continue;
            }

            if (!ResolveAnchorToScreen(obj.anchorU, out Transform screen, out int anchorUEye, out bool isRightEye))
            {
                continue;
            }
            if (!TryGetEyeScreenRect(screen, out Rect eyeRect))
            {
                continue;
            }

            float anchorUBase = anchorUEye;
            float anchorVBase = obj.anchorV;

            float bboxX = obj.bboxX;
            if (isRightEye && bboxX >= manifest.eye_w)
            {
                bboxX -= manifest.eye_w;
            }
            float bboxY = obj.bboxY;
            float bboxX2 = bboxX + obj.bboxW;
            float bboxY2 = bboxY + obj.bboxH;
            if (!TryMapMetaUvToEyePixel(ref bboxX, ref bboxY))
            {
                continue;
            }
            if (!TryMapMetaUvToEyePixel(ref bboxX2, ref bboxY2))
            {
                continue;
            }

            float bboxMinU = Mathf.Min(bboxX, bboxX2);
            float bboxMaxU = Mathf.Max(bboxX, bboxX2);
            float bboxMinV = Mathf.Min(bboxY, bboxY2);
            float bboxMaxV = Mathf.Max(bboxY, bboxY2);

            if (!loggedRefSet)
            {
                loggedRefSet = true;
                logRefBboxW = obj.bboxW;
                logRefBboxH = obj.bboxH;
                logRefAnchorU = obj.anchorU;
                logRefAnchorV = obj.anchorV;
            }

            Vector3[] drawJoints = obj.jointsCam;
            byte[] drawVis = obj.jointsVis;
            bool usingProcessedSource = false;
            if (joints2DMode == Joints2DMode.ProjectXYZ && !debugProjectXYZUseRaw &&
                debugProcessedJointsByTrack.TryGetValue(obj.trackId, out DebugProcessedJointState procState) &&
                procState != null && procState.frame == frame && procState.jointsCamProcessed != null)
            {
                drawJoints = procState.jointsCamProcessed;
                if (procState.jointsVis != null)
                {
                    drawVis = procState.jointsVis;
                }
                usingProcessedSource = true;
            }
            if (joints2DMode == Joints2DMode.ProjectXYZ)
            {
                if (usingProcessedSource)
                {
                    processedSourceObjCount++;
                }
                else
                {
                    rawSourceObjCount++;
                }
            }

            int kp = Mathf.Min((int)obj.skeletonKpCount, Mathf.Min(obj.jointsCam.Length, Mathf.Min(drawJoints.Length, drawVis.Length)));
            totalKp += kp;
            Color jointColor = (obj.trackId % 2u == 0u) ? new Color(1f, 1f, 0f, 0.95f) : new Color(1f, 0.35f, 0.15f, 0.95f);

            for (int j = 0; j < kp; j++)
            {
                Vector3 rawJ = obj.jointsCam[j];
                Vector3 jc = drawJoints[j];
                minX = Mathf.Min(minX, rawJ.x);
                maxX = Mathf.Max(maxX, rawJ.x);
                minY = Mathf.Min(minY, rawJ.y);
                maxY = Mathf.Max(maxY, rawJ.y);
                minZ = Mathf.Min(minZ, rawJ.z);
                maxZ = Mathf.Max(maxZ, rawJ.z);
                hasJointSamples = true;
                if (joints2DMode == Joints2DMode.ProjectXYZ)
                {
                    projMinX = Mathf.Min(projMinX, jc.x);
                    projMaxX = Mathf.Max(projMaxX, jc.x);
                    projMinY = Mathf.Min(projMinY, jc.y);
                    projMaxY = Mathf.Max(projMaxY, jc.y);
                    projMinZ = Mathf.Min(projMinZ, jc.z);
                    projMaxZ = Mathf.Max(projMaxZ, jc.z);
                    hasProjectedSourceSamples = true;
                }

                if (drawVis[j] == 0)
                {
                    continue;
                }

                totalValid++;
                float u;
                float v;
                if (joints2DMode == Joints2DMode.AsUV)
                {
                    u = jc.x;
                    v = jc.y;
                }
                else if (joints2DMode == Joints2DMode.UV01)
                {
                    u = jc.x * manifest.eye_w;
                    v = jc.y * manifest.eye_h;
                }
                else if (joints2DMode == Joints2DMode.NDC)
                {
                    u = (jc.x * 0.5f + 0.5f) * manifest.eye_w;
                    v = (0.5f - jc.y * 0.5f) * manifest.eye_h;
                }
                else if (joints2DMode == Joints2DMode.REL_PIX)
                {
                    u = anchorUBase + jc.x;
                    v = anchorVBase + (relFlipY ? -jc.y : jc.y);
                }
                else if (joints2DMode == Joints2DMode.REL_BBOX01)
                {
                    u = anchorUBase + jc.x * obj.bboxW;
                    v = anchorVBase + (relFlipY ? -jc.y : jc.y) * obj.bboxH;
                }
                else if (joints2DMode == Joints2DMode.REL_BBOXNDC)
                {
                    u = anchorUBase + jc.x * (obj.bboxW * 0.5f);
                    v = anchorVBase + (relFlipY ? -jc.y : jc.y) * (obj.bboxH * 0.5f);
                }
                else
                {
                    if (!hasFxFy)
                    {
                        continue;
                    }

                    bool zIsZero = Mathf.Approximately(jc.z, 0f);
                    bool shouldSkipForZ = debugSkipOnlyZeq0 ? zIsZero : jc.z <= 0f;
                    if (shouldSkipForZ)
                    {
                        if (jc.z <= 0f)
                        {
                            zNonPositiveSkipped++;
                        }
                        if (zIsZero)
                        {
                            zEq0Skipped++;
                        }
                        continue;
                    }

                    if (hasManifestNormIntrinsics)
                    {
                        // Match PC-side normalized pinhole projection.
                        float eyeW = intrEyeW;
                        float eyeH = intrEyeH;
                        u = (((jc.x / jc.z) * fxNorm) * 0.5f + 0.5f) * eyeW;
                        v = (0.5f - ((jc.y / jc.z) * fyNorm) * 0.5f) * eyeH;
                    }
                    else
                    {
                        u = fx * (jc.x / jc.z) + cx;
                        v = fy * (jc.y / jc.z) + cy;
                    }
                }

                if (joints2DMode == Joints2DMode.REL_PIX || joints2DMode == Joints2DMode.REL_BBOX01 || joints2DMode == Joints2DMode.REL_BBOXNDC)
                {
                    if (!TryMapMetaUvToEyePixel(ref u, ref v))
                    {
                        continue;
                    }
                }

                if (u >= bboxMinU && u <= bboxMaxU && v >= bboxMinV && v <= bboxMaxV)
                {
                    insideCount++;
                }

                Vector2 p = EyePixelToRectPixel(eyeRect, u, v);
                joints2DOverlayPoints.Add(new Joints2DOverlayPoint { pos = p, color = jointColor });
            }
        }

        if (lastJoints2DLogFrame == frame)
        {
            return;
        }

        lastJoints2DLogFrame = frame;
        string anchorRange = minAnchorZ <= maxAnchorZ ? $"[{minAnchorZ:F3},{maxAnchorZ:F3}]" : "[n/a,n/a]";
        string xRange = hasJointSamples ? $"[{minX:F3},{maxX:F3}]" : "[n/a,n/a]";
        string yRange = hasJointSamples ? $"[{minY:F3},{maxY:F3}]" : "[n/a,n/a]";
        string zRange = hasJointSamples ? $"[{minZ:F3},{maxZ:F3}]" : "[n/a,n/a]";
        string projXRange = hasProjectedSourceSamples ? $"[{projMinX:F3},{projMaxX:F3}]" : "[n/a,n/a]";
        string projYRange = hasProjectedSourceSamples ? $"[{projMinY:F3},{projMaxY:F3}]" : "[n/a,n/a]";
        string projZRange = hasProjectedSourceSamples ? $"[{projMinZ:F3},{projMaxZ:F3}]" : "[n/a,n/a]";
        string projectSource = "n/a";
        if (joints2DMode == Joints2DMode.ProjectXYZ)
        {
            if (debugProjectXYZUseRaw)
            {
                projectSource = "raw";
            }
            else if (processedSourceObjCount > 0 && rawSourceObjCount == 0)
            {
                projectSource = "processed";
            }
            else if (processedSourceObjCount > 0 && rawSourceObjCount > 0)
            {
                projectSource = "processed+raw_fallback";
            }
            else
            {
                projectSource = "raw_fallback";
            }
        }
        string zSkipText = joints2DMode == Joints2DMode.ProjectXYZ
            ? $" zNonPositiveSkipped={zNonPositiveSkipped} zEq0Skipped={zEq0Skipped} projectSource={projectSource} skipOnlyZeq0={debugSkipOnlyZeq0} projX={projXRange} projY={projYRange} projZ={projZRange}"
            : string.Empty;
        string bboxAnchorText = loggedRefSet
            ? $" bboxW={logRefBboxW:F1} bboxH={logRefBboxH:F1} anchorU={logRefAnchorU:F1} anchorV={logRefAnchorV:F1}"
            : " bboxW=n/a bboxH=n/a anchorU=n/a anchorV=n/a";
        Debug.Log(
            $"JOINTS_2D frame={frame} mode={joints2DMode} kpCount={totalKp} validCount={totalValid} insideCount={insideCount} " +
            $"anchorZ={anchorRange} jX={xRange} jY={yRange} jZ={zRange}{bboxAnchorText}{zSkipText}");
    }


    private void CaptureMeta2DOverlay(MetaObj obj, Transform screen, bool isRightEye, int uEyeFromResolve)
    {
        if (!debugDrawMeta2D || manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0 || screen == null)
        {
            return;
        }

        if (!TryGetEyeScreenRect(screen, out Rect eyeRect))
        {
            return;
        }

        float anchorU = uEyeFromResolve;
        float anchorV = obj.anchorV;
        if (!TryMapMetaUvToEyePixel(ref anchorU, ref anchorV))
        {
            return;
        }

        float bboxX = obj.bboxX;
        if (isRightEye && bboxX >= manifest.eye_w)
        {
            bboxX -= manifest.eye_w;
        }
        float bboxY = obj.bboxY;
        float bboxX2 = bboxX + obj.bboxW;
        float bboxY2 = bboxY + obj.bboxH;
        if (!TryMapMetaUvToEyePixel(ref bboxX, ref bboxY))
        {
            return;
        }
        if (!TryMapMetaUvToEyePixel(ref bboxX2, ref bboxY2))
        {
            return;
        }

        Vector2 anchorPx = EyePixelToRectPixel(eyeRect, anchorU, anchorV);
        Vector2 p0 = EyePixelToRectPixel(eyeRect, bboxX, bboxY);
        Vector2 p1 = EyePixelToRectPixel(eyeRect, bboxX2, bboxY2);
        Rect bboxRect = Rect.MinMaxRect(Mathf.Min(p0.x, p1.x), Mathf.Min(p0.y, p1.y), Mathf.Max(p0.x, p1.x), Mathf.Max(p0.y, p1.y));

        meta2DOverlayItems.Add(new Meta2DOverlayItem
        {
            trackId = obj.trackId,
            eyeRect = eyeRect,
            anchor = anchorPx,
            bbox = bboxRect
        });
    }


    private bool TryMapMetaUvToEyePixel(ref float u, ref float v)
    {
        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return false;
        }

        // Order of operations:
        // a) normalized -> pixel
        // b) flip
        // c) crop remap (offset-only or ApplyCropToEyePixel)
        // d) map to eye rect in OnGUI
        if (uvIsNormalized)
        {
            u *= manifest.eye_w;
            v *= manifest.eye_h;
        }

        if (flipU)
        {
            u = manifest.eye_w - u;
        }
        if (flipV)
        {
            v = manifest.eye_h - v;
        }

        if (applyCropScale)
        {
            ApplyCropToEyePixel(ref u, ref v);
        }
        else
        {
            u -= GetCropX();
            v -= GetCropY();
        }

        return true;
    }


    private bool TryGetEyeScreenRect(Transform screen, out Rect rect)
    {
        rect = Rect.zero;
        Camera cam = GetViewCamera() ?? Camera.main;
        if (cam == null || screen == null)
        {
            return false;
        }

        GetScreenMeshLocalBounds(screen, out Vector3 center, out Vector3 size);
        Vector3 e = size * 0.5f;
        Vector3[] local = new Vector3[4]
        {
            center + new Vector3(-e.x, -e.y, 0f),
            center + new Vector3( e.x, -e.y, 0f),
            center + new Vector3( e.x,  e.y, 0f),
            center + new Vector3(-e.x,  e.y, 0f),
        };

        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;
        for (int i = 0; i < local.Length; i++)
        {
            Vector3 world = screen.TransformPoint(local[i]);
            Vector3 s = cam.WorldToScreenPoint(world);
            if (s.z <= 0f)
            {
                return false;
            }

            float gx = s.x;
            float gy = Screen.height - s.y;
            minX = Mathf.Min(minX, gx);
            minY = Mathf.Min(minY, gy);
            maxX = Mathf.Max(maxX, gx);
            maxY = Mathf.Max(maxY, gy);
        }

        rect = Rect.MinMaxRect(minX, minY, maxX, maxY);
        return rect.width > 1f && rect.height > 1f;
    }


    private Vector2 EyePixelToRectPixel(Rect eyeRect, float u, float v)
    {
        float x = eyeRect.xMin + (u / manifest.eye_w) * eyeRect.width;
        float y = eyeRect.yMin + (v / manifest.eye_h) * eyeRect.height;
        return new Vector2(x, y);
    }


    private static void DrawRectOutline(Rect rect, Color color, float thickness)
    {
        Color old = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(new Rect(rect.xMin, rect.yMin, rect.width, thickness), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rect.xMin, rect.yMax - thickness, rect.width, thickness), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rect.xMin, rect.yMin, thickness, rect.height), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.yMin, thickness, rect.height), Texture2D.whiteTexture);
        GUI.color = old;
    }

}

