using System;
using System.Collections.Generic;
using ClientPlugin.Dlss;
using HarmonyLib;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Render11.RenderContext;
using VRage.Render11.Resources;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace ClientPlugin.Patches;

internal static class BillboardOutputPass
{
    private const int PostPpBucket = 4;

    [ThreadStatic]
    private static bool drawingPostPp;

    private static bool drewHudThisScene;
    private static readonly object snapshotLock = new object();
    private static readonly List<MyBillboard> pendingAdds = new List<MyBillboard>(512);
    private static readonly List<MyBillboard> uniqueScratch = new List<MyBillboard>(512);
    private static List<MyBillboard> published = new List<MyBillboard>(512);
    private static List<MyBillboard> publishScratch = new List<MyBillboard>(512);
    private static readonly List<MyBillboard> snapshot = new List<MyBillboard>(512);
    private static readonly HashSet<MyBillboard> seen = new HashSet<MyBillboard>();
    private static readonly int[] blendHistogram = new int[8];
    private static MatrixD pendingCameraWorld;
    private static MatrixD publishedCameraWorld;
    private static bool hasPendingCamera;
    private static bool hasPublishedCamera;

    public static void BeginDraw()
    {
        drewHudThisScene = false;
    }

    public static void PublishCompletedFrame()
    {
        if (!DlssRuntime.IsLive)
            return;

        int count;
        lock (snapshotLock)
        {
            if (pendingAdds.Count == 0)
                return;

            DedupeInto(pendingAdds, uniqueScratch);
            pendingAdds.Clear();
            FreezeInto(uniqueScratch, publishScratch);

            var swap = published;
            published = publishScratch;
            publishScratch = swap;

            publishedCameraWorld = pendingCameraWorld;
            hasPublishedCamera = hasPendingCamera;
            hasPendingCamera = false;
            count = published.Count;
        }

        LogHudOnce("Published complete PostPP frame count=" + count);
    }

    public static void NoteAdd(MyBillboard billboard)
    {
        if (!DlssRuntime.IsLive || !IsPostPp(billboard))
            return;
        lock (snapshotLock)
        {
            CapturePendingCameraIfNeeded();
            pendingAdds.Add(billboard);
        }
    }

    public static void NoteAdds(IEnumerable<MyBillboard> billboards)
    {
        if (!DlssRuntime.IsLive || billboards == null)
            return;
        lock (snapshotLock)
        {
            foreach (var billboard in billboards)
            {
                if (!IsPostPp(billboard))
                    continue;
                CapturePendingCameraIfNeeded();
                pendingAdds.Add(billboard);
            }
        }
    }

    public static bool TryRenderPostPp(MyRenderContext rc, IRtvBindable target)
    {
        LogHudOnce("RenderPostPP enter live=" + DlssRuntime.IsLive +
                   " target=" + (target != null ? target.Size.ToString() : "null") +
                   " pending=" + PendingCount());
        if (!DlssRuntime.IsLive || drawingPostPp || rc == null || target == null)
            return false;

        BindUnjitteredFrameConstants();
        return TryDrawCaptured(rc, target, "RenderPostPP");
    }

    public static void TryDrawAfterSceneBlit()
    {
        if (!DlssRuntime.IsLive || drewHudThisScene)
            return;
        var dest = MyRender11.Backbuffer;
        var rc = MyRender11.RC;
        if (dest == null || rc == null)
            return;
        BindUnjitteredFrameConstants();
        TryDrawCaptured(rc, dest, "CopyToRT");
    }

    private static bool TryDrawCaptured(MyRenderContext rc, IRtvBindable target, string reason)
    {
        if (drewHudThisScene || drawingPostPp || rc == null || target == null)
            return true;

        int captured = CaptureForDraw();
        if (captured == 0)
            return true;

        int prepared = PrepareFromSnapshot();
        if (prepared > 0)
        {
            MyBillboardRenderer.GatherInternal(rc);
            MyBillboardRenderer.TransferData(rc);
        }

        FillHistogram();
        LogHudOnce(reason + " captured=" + captured +
                   " indexed=" + snapshot.Count +
                   " postpp=" + blendHistogram[PostPpBucket] +
                   " dest=" + target.Size +
                   " " + DescribeSample() +
                   " " + DescribeBuckets());

        if (!HasBucket(PostPpBucket))
            return true;

        var dxgi = DlssRuntime.SwapchainBufferSize();
        var output = DlssRuntime.OutputResolution();
        int width = target.Size.X > 0 ? target.Size.X : (dxgi.X > 0 ? dxgi.X : output.X);
        int height = target.Size.Y > 0 ? target.Size.Y : (dxgi.Y > 0 ? dxgi.Y : output.Y);
        if (width <= 0 || height <= 0)
            return true;

        drawingPostPp = true;
        try
        {
            rc.SetViewport(0f, 0f, width, height);
            rc.SetBlendState(MyBlendStateManager.BlendAlphaPremult);
            rc.SetDepthStencilState(MyDepthStencilStateManager.IgnoreDepthStencil);
            rc.SetRtv(target);
            MyBillboardRenderer.Render(rc, null, MyBillboardRenderer.m_bucketBatches[PostPpBucket], false, true);
            rc.SetRtvNull();
            drewHudThisScene = true;
        }
        finally
        {
            drawingPostPp = false;
        }
        return true;
    }

    private static void BindUnjitteredFrameConstants()
    {
        var env = MyRender11.Environment;
        if (env != null)
            Jitter.Restore(env.Matrices);
        MyCommon.UpdateFrameConstants();
        DlssRuntime.ApplyOutputSpace();
    }

    public static bool TryRender(MyRenderContext rc, ISrvBindable depthRead, IRtvBindable target, int bucket)
    {
        if (!DlssRuntime.IsLive || rc == null || target == null)
            return false;
        var sceneDepth = MyGBuffer.Main?.ResolvedDepthStencil;
        if (sceneDepth == null || (target.Size.X == sceneDepth.Size.X && target.Size.Y == sceneDepth.Size.Y))
            return false;
        if (!HasBucket(bucket))
            return true;

        rc.SetViewport(0f, 0f, target.Size.X, target.Size.Y);
        rc.SetBlendState(MyBlendStateManager.BlendAlphaPremult);

        var outputDepth = DlssRuntime.TryAcquireOutputDepth(sceneDepth, target.Size);
        if (outputDepth != null)
        {
            DebugLog.WriteFrame("Billboard LDR upsampled depth dest=" + target.Size + " src=" + sceneDepth.Size);
            rc.SetDepthStencilState(MyDepthStencilStateManager.DefaultDepthState);
            rc.SetRtv(outputDepth.DsvRoDepth, target);
            MyBillboardRenderer.Render(rc, outputDepth.SrvDepth, MyBillboardRenderer.m_bucketBatches[bucket], false, true);
            rc.SetRtvNull();
            return true;
        }

        DebugLog.WriteFrame("Billboard LDR no-depth fallback dest=" + target.Size + " depth=" + sceneDepth.Size);
        rc.SetDepthStencilState(MyDepthStencilStateManager.IgnoreDepthStencil);
        rc.SetRtv(target);
        MyBillboardRenderer.Render(rc, depthRead, MyBillboardRenderer.m_bucketBatches[bucket], false, true);
        rc.SetRtvNull();
        return true;
    }

    private static int PendingCount()
    {
        lock (snapshotLock)
            return pendingAdds.Count;
    }

    private static int CaptureForDraw()
    {
        MatrixD sourceCamera = default;
        bool reanchor;
        lock (snapshotLock)
        {
            FreezeInto(published, snapshot);
            sourceCamera = publishedCameraWorld;
            reanchor = hasPublishedCamera;
        }

        if (reanchor)
            ReanchorToRenderCamera(sourceCamera);
        return snapshot.Count;
    }

    private static bool IsPostPp(MyBillboard billboard)
    {
        return billboard != null && billboard.BlendType == MyBillboard.BlendTypeEnum.PostPP;
    }

    private static void CapturePendingCameraIfNeeded()
    {
        if (hasPendingCamera)
            return;
        var camera = MyAPIGateway.Session != null ? MyAPIGateway.Session.Camera : null;
        if (camera == null)
            return;
        pendingCameraWorld = camera.WorldMatrix;
        hasPendingCamera = true;
    }

    private static void DedupeInto(List<MyBillboard> source, List<MyBillboard> dest)
    {
        dest.Clear();
        seen.Clear();
        for (int i = 0; i < source.Count; i++)
        {
            var billboard = source[i];
            if (billboard == null || !seen.Add(billboard))
                continue;
            dest.Add(billboard);
        }
    }

    private static void FreezeInto(List<MyBillboard> source, List<MyBillboard> dest)
    {
        while (dest.Count < source.Count)
            dest.Add(null);

        for (int i = 0; i < source.Count; i++)
            CopyBillboard(source[i], ref dest, i);

        if (dest.Count > source.Count)
            dest.RemoveRange(source.Count, dest.Count - source.Count);
    }

    private static void CopyBillboard(MyBillboard source, ref List<MyBillboard> dest, int index)
    {
        bool triangle = source is MyTriangleBillboard;
        MyBillboard copy = dest[index];
        if (copy == null || triangle != (copy is MyTriangleBillboard))
        {
            copy = triangle ? (MyBillboard)new MyTriangleBillboard() : new MyBillboard();
            dest[index] = copy;
        }

        copy.Material = source.Material;
        copy.BlendType = source.BlendType;
        copy.Position0 = source.Position0;
        copy.Position1 = source.Position1;
        copy.Position2 = source.Position2;
        copy.Position3 = source.Position3;
        copy.Color = source.Color;
        copy.ColorIntensity = source.ColorIntensity;
        copy.SoftParticleDistanceScale = source.SoftParticleDistanceScale;
        copy.UVOffset = source.UVOffset;
        copy.UVSize = source.UVSize;
        copy.LocalType = source.LocalType;
        copy.ParentID = source.ParentID;
        copy.DistanceSquared = source.DistanceSquared;
        copy.Reflectivity = source.Reflectivity;
        copy.AlphaCutout = source.AlphaCutout;
        copy.CustomViewProjection = source.CustomViewProjection;

        var sourceTriangle = source as MyTriangleBillboard;
        var copyTriangle = copy as MyTriangleBillboard;
        if (sourceTriangle != null && copyTriangle != null)
        {
            copyTriangle.UV0 = sourceTriangle.UV0;
            copyTriangle.UV1 = sourceTriangle.UV1;
            copyTriangle.UV2 = sourceTriangle.UV2;
            copyTriangle.Normal0 = sourceTriangle.Normal0;
        }
    }

    private static void ReanchorToRenderCamera(MatrixD sourceCameraWorld)
    {
        var env = MyRender11.Environment != null ? MyRender11.Environment.Matrices : null;
        if (env == null)
            return;

        MatrixD sourceInverse = MatrixD.Invert(sourceCameraWorld);
        MatrixD sourceToRender = sourceInverse * env.InvViewD;
        for (int i = 0; i < snapshot.Count; i++)
        {
            var billboard = snapshot[i];
            if (billboard.ParentID != uint.MaxValue ||
                billboard.CustomViewProjection != -1 ||
                billboard.LocalType != MyBillboard.LocalTypeEnum.Custom)
                continue;

            billboard.Position0 = Vector3D.Transform(billboard.Position0, sourceToRender);
            billboard.Position1 = Vector3D.Transform(billboard.Position1, sourceToRender);
            billboard.Position2 = Vector3D.Transform(billboard.Position2, sourceToRender);
            billboard.Position3 = Vector3D.Transform(billboard.Position3, sourceToRender);

            var triangle = billboard as MyTriangleBillboard;
            if (triangle != null)
            {
                Vector3D normal = Vector3D.TransformNormal((Vector3D)triangle.Normal0, sourceToRender);
                triangle.Normal0 = (Vector3)normal;
            }
        }
    }

    private static int PrepareFromSnapshot()
    {
        var counts = MyBillboardRenderer.m_bucketCounts;
        MyBillboardRenderer.m_batches.Clear();
        for (int i = 0; i < 6; i++)
            counts[i] = 0;

        for (int i = 0; i < snapshot.Count; i++)
            CountBillboard(snapshot[i], counts);

        int total = 0;
        for (int i = 0; i < 6; i++)
            total += counts[i];
        if (total == 0)
        {
            MyBillboardRenderer.m_billboardCountSafe = 0;
            return 0;
        }

        int safe = total > 32768 ? 32768 : total;
        int tempSize = MyBillboardRenderer.m_tempBuffer.Length;
        while (total > tempSize)
            tempSize *= 2;
        Array.Resize(ref MyBillboardRenderer.m_tempBuffer, tempSize);

        var arrays = MyBillboardRenderer.m_arrayDataBillboards;
        int dataSize = arrays.Length;
        while (safe > dataSize)
            dataSize *= 2;
        arrays.Resize(dataSize);
        MyBillboardRenderer.m_arrayDataBillboards = arrays;

        for (int i = 0; i < 6; i++)
            MyBillboardRenderer.m_bucketBatches[i] = default;

        MyBillboardRenderer.m_lastBatchOffset = 0;
        var indices = MyBillboardRenderer.m_bucketIndices;
        indices[0] = 0;
        for (int i = 1; i < 6; i++)
            indices[i] = indices[i - 1] + counts[i - 1];

        for (int i = 0; i < snapshot.Count; i++)
            PlaceBillboard(snapshot[i], indices);

        indices[0] = 0;
        for (int i = 1; i < 6; i++)
            indices[i] = indices[i - 1] + counts[i - 1];

        for (int i = 0; i < 6; i++)
        {
            if (i != 3 && i != 4 && counts[i] > 0)
                Array.Sort(MyBillboardRenderer.m_tempBuffer, indices[i], counts[i]);
        }

        MyBillboardRenderer.m_billboardCountSafe = safe;
        return safe;
    }

    private static void CountBillboard(MyBillboard billboard, int[] counts)
    {
        if (billboard == null)
            return;
        int bucket = MyBillboardRenderer.GetBillboardBucket(billboard);
        if ((uint)bucket < 6)
            counts[bucket]++;
    }

    private static void PlaceBillboard(MyBillboard billboard, int[] indices)
    {
        if (billboard == null)
            return;
        int bucket = MyBillboardRenderer.GetBillboardBucket(billboard);
        if ((uint)bucket < 6)
            MyBillboardRenderer.m_tempBuffer[indices[bucket]++] = billboard;
    }

    private static void FillHistogram()
    {
        for (int i = 0; i < blendHistogram.Length; i++)
            blendHistogram[i] = 0;
        for (int i = 0; i < snapshot.Count; i++)
        {
            int blend = (int)snapshot[i].BlendType;
            if ((uint)blend < (uint)blendHistogram.Length)
                blendHistogram[blend]++;
        }
    }

    private static bool HasBucket(int bucket)
    {
        return MyBillboardRenderer.m_bucketBatches != null &&
               bucket >= 0 &&
               bucket < MyBillboardRenderer.m_bucketBatches.Length &&
               MyBillboardRenderer.m_bucketBatches[bucket].Count > 0;
    }

    private static string DescribeSample()
    {
        if (snapshot.Count == 0)
            return "sample=none";
        var billboard = snapshot[0];
        return "sample blend=" + billboard.BlendType +
               " mat=" + billboard.Material +
               " p0=" + billboard.Position0;
    }

    private static string DescribeBuckets()
    {
        var batches = MyBillboardRenderer.m_bucketBatches;
        string buckets = "null";
        if (batches != null)
        {
            buckets = "";
            for (int i = 0; i < batches.Length; i++)
            {
                if (i > 0)
                    buckets += ",";
                buckets += batches[i].Count;
            }
        }
        return "eval=" + DlssRuntime.EvaluatedHdrThisFrame +
               " live=" + DlssRuntime.IsLive +
               " bucketBatches=" + buckets;
    }

    private static string lastHudLog;

    public static void LogHudOnce(string message)
    {
        DebugLog.WriteFrame(message);
        if (lastHudLog == message)
            return;
        lastHudLog = message;
        MyLog.Default.WriteLine("DLSS HUD: " + message);
    }
}

[HarmonyPatch(typeof(MyBillboardRenderer), nameof(MyBillboardRenderer.RenderLDR))]
internal static class BillboardLdrPatch
{
    [HarmonyPrefix]
    private static bool Prefix(MyRenderContext rc, ISrvBindable depthRead, IRtvBindable target)
    {
        return !BillboardOutputPass.TryRender(rc, depthRead, target, 3);
    }
}

[HarmonyPatch(typeof(MyBillboardRenderer), nameof(MyBillboardRenderer.RenderPostPP))]
internal static class BillboardPostPpPatch
{
    [HarmonyPrefix]
    private static bool Prefix(MyRenderContext rc, ISrvBindable depthRead, IRtvBindable target)
    {
        return !BillboardOutputPass.TryRenderPostPp(rc, target);
    }
}

[HarmonyPatch(typeof(MyRenderProxy), nameof(MyRenderProxy.AddBillboard))]
internal static class BillboardAddPatch
{
    [HarmonyPostfix]
    private static void Postfix(MyBillboard billboard)
    {
        BillboardOutputPass.NoteAdd(billboard);
    }
}

[HarmonyPatch(typeof(MyRenderProxy), nameof(MyRenderProxy.AddBillboards))]
internal static class BillboardAddRangePatch
{
    [HarmonyPostfix]
    private static void Postfix(IEnumerable<MyBillboard> billboards)
    {
        BillboardOutputPass.NoteAdds(billboards);
    }
}

[HarmonyPatch(
    typeof(MyTransparentGeometry),
    nameof(MyTransparentGeometry.ApplyActionOnPersistentBillboards),
    new[] { typeof(Action) })]
internal static class BillboardFrameCompletePatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        BillboardOutputPass.PublishCompletedFrame();
    }
}
