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
    private static bool _drawingPostPp;

    private static bool _drewHudThisScene;
    private static readonly object SnapshotLock = new();
    private static readonly List<MyBillboard> PendingAdds = new(512);
    private static readonly List<MyBillboard> UniqueScratch = new(512);
    private static List<MyBillboard> _published = new(512);
    private static List<MyBillboard> _publishScratch = new(512);
    private static readonly List<MyBillboard> Snapshot = new(512);
    private static readonly HashSet<MyBillboard> Seen = [];
    private static readonly int[] BlendHistogram = new int[8];
    private static MatrixD _pendingCameraWorld;
    private static MatrixD _publishedCameraWorld;
    private static bool _hasPendingCamera;
    private static bool _hasPublishedCamera;
    private static int _lastSubmitTick;

    // Retain the last complete HUD frame across empty submissions, then expire it to prevent frozen overlays.
    private const int StaleHudMs = 200;

    public static void BeginDraw()
    {
        _drewHudThisScene = false;
    }

    public static void Reset()
    {
        _drawingPostPp = false;
        _drewHudThisScene = false;
        _lastHudLog = null;

        lock (SnapshotLock)
        {
            PendingAdds.Clear();
            UniqueScratch.Clear();
            _published.Clear();
            _publishScratch.Clear();
            Snapshot.Clear();
            Seen.Clear();
            Array.Clear(BlendHistogram, 0, BlendHistogram.Length);
            _pendingCameraWorld = default(MatrixD);
            _publishedCameraWorld = default(MatrixD);
            _hasPendingCamera = false;
            _hasPublishedCamera = false;
            _lastSubmitTick = 0;
        }
    }

    public static void PublishCompletedFrame()
    {
        if (!DlssRuntime.IsLive)
            return;

        int count;
        lock (SnapshotLock)
        {
            if (PendingAdds.Count == 0)
                return;

            DedupeInto(PendingAdds, UniqueScratch);
            PendingAdds.Clear();
            FreezeInto(UniqueScratch, _publishScratch);

            (_published, _publishScratch) = (_publishScratch, _published);

            _publishedCameraWorld = _pendingCameraWorld;
            _hasPublishedCamera = _hasPendingCamera;
            _hasPendingCamera = false;
            NoteHudSubmitLocked();
            count = _published.Count;
        }

        LogHudOnce("Published complete PostPP frame count=" + count);
    }

    public static void NoteAdd(MyBillboard billboard)
    {
        if (!DlssRuntime.IsLive || !IsPostPp(billboard))
            return;
        lock (SnapshotLock)
        {
            CapturePendingCameraIfNeeded();
            PendingAdds.Add(billboard);
            NoteHudSubmitLocked();
        }
    }

    public static void NoteAdds(IEnumerable<MyBillboard> billboards)
    {
        if (!DlssRuntime.IsLive || billboards == null)
            return;
        lock (SnapshotLock)
        {
            var added = false;
            foreach (var billboard in billboards)
            {
                if (!IsPostPp(billboard))
                    continue;
                CapturePendingCameraIfNeeded();
                PendingAdds.Add(billboard);
                added = true;
            }
            if (added)
                NoteHudSubmitLocked();
        }
    }

    public static bool TryRenderPostPp(MyRenderContext rc, IRtvBindable target)
    {
        LogHudOnce("RenderPostPP enter live=" + DlssRuntime.IsLive +
                   " target=" + (target != null ? target.Size.ToString() : "null") +
                   " pending=" + PendingCount());
        if (!DlssRuntime.IsLive || _drawingPostPp || rc == null || target == null)
            return false;

        BindUnjitteredFrameConstants();
        return TryDrawCaptured(rc, target, "RenderPostPP");
    }

    public static void TryDrawAfterSceneBlit()
    {
        if (!DlssRuntime.IsLive || _drewHudThisScene)
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
        if (_drewHudThisScene || _drawingPostPp || rc == null || target == null)
            return true;

        var captured = CaptureForDraw();
        if (captured == 0)
            return true;

        var prepared = PrepareFromSnapshot();
        if (prepared > 0)
        {
            MyBillboardRenderer.GatherInternal(rc);
            MyBillboardRenderer.TransferData(rc);
        }

        FillHistogram();
        var output = DlssRuntime.OutputPixelSize();
        LogHudOnce(reason + " captured=" + captured +
                   " indexed=" + Snapshot.Count +
                   " postpp=" + BlendHistogram[PostPpBucket] +
                   " dest=" + target.Size +
                   " output=" + output +
                   " " + DescribeSample() +
                   " " + DescribeBuckets());

        if (!HasBucket(PostPpBucket))
            return true;

        if (!DlssRuntime.TryGetHudTargetSize(target, out var viewport))
        {
            LogHudOnce(reason + " skip internal dest=" + target.Size +
                       " output=" + DlssRuntime.OutputPixelSize());
            return true;
        }

        _drawingPostPp = true;
        try
        {
            rc.SetViewport(0f, 0f, viewport.X, viewport.Y);
            rc.SetBlendState(MyBlendStateManager.BlendAlphaPremult);
            rc.SetDepthStencilState(MyDepthStencilStateManager.IgnoreDepthStencil);
            rc.SetRtv(target);
            try
            {
                MyBillboardRenderer.Render(rc, null, MyBillboardRenderer.m_bucketBatches[PostPpBucket], false, true);
                _drewHudThisScene = true;
            }
            finally
            {
                rc.SetRtvNull();
            }
        }
        finally
        {
            _drawingPostPp = false;
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
            try
            {
                MyBillboardRenderer.Render(
                    rc,
                    outputDepth.SrvDepth,
                    MyBillboardRenderer.m_bucketBatches[bucket],
                    false,
                    true);
            }
            finally
            {
                rc.SetRtvNull();
            }
            return true;
        }

        DebugLog.WriteFrame("Billboard LDR no-depth fallback dest=" + target.Size + " depth=" + sceneDepth.Size);
        rc.SetDepthStencilState(MyDepthStencilStateManager.IgnoreDepthStencil);
        rc.SetRtv(target);
        try
        {
            MyBillboardRenderer.Render(rc, depthRead, MyBillboardRenderer.m_bucketBatches[bucket], false, true);
        }
        finally
        {
            rc.SetRtvNull();
        }
        return true;
    }

    private static int PendingCount()
    {
        lock (SnapshotLock)
            return PendingAdds.Count;
    }

    private static int CaptureForDraw()
    {
        MatrixD sourceCamera;
        bool reanchor;
        lock (SnapshotLock)
        {
            if (HudSnapshotIsStaleLocked())
            {
                LogHudOnce("Expired stale PostPP snapshot count=" + _published.Count);
                ClearPublishedLocked();
                Snapshot.Clear();
                return 0;
            }

            FreezeInto(_published, Snapshot);
            sourceCamera = _publishedCameraWorld;
            reanchor = _hasPublishedCamera;
        }

        if (reanchor)
            ReanchorToRenderCamera(sourceCamera);
        return Snapshot.Count;
    }

    private static void NoteHudSubmitLocked()
    {
        _lastSubmitTick = Environment.TickCount;
    }

    private static bool HudSnapshotIsStaleLocked()
    {
        if (_published.Count == 0 || PendingAdds.Count > 0)
            return false;
        unchecked
        {
            return Environment.TickCount - _lastSubmitTick > StaleHudMs;
        }
    }

    private static void ClearPublishedLocked()
    {
        _published.Clear();
        _hasPublishedCamera = false;
        _hasPendingCamera = false;
    }

    private static bool IsPostPp(MyBillboard billboard)
    {
        return billboard is { BlendType: MyBillboard.BlendTypeEnum.PostPP };
    }

    private static void CapturePendingCameraIfNeeded()
    {
        if (_hasPendingCamera)
            return;
        var camera = MyAPIGateway.Session?.Camera;
        if (camera == null)
            return;
        _pendingCameraWorld = camera.WorldMatrix;
        _hasPendingCamera = true;
    }

    private static void DedupeInto(List<MyBillboard> source, List<MyBillboard> dest)
    {
        dest.Clear();
        Seen.Clear();
        foreach (var billboard in source)
        {
            if (billboard == null || !Seen.Add(billboard))
                continue;
            dest.Add(billboard);
        }
    }

    private static void FreezeInto(List<MyBillboard> source, List<MyBillboard> dest)
    {
        while (dest.Count < source.Count)
            dest.Add(null);

        for (var i = 0; i < source.Count; i++)
            CopyBillboard(source[i], ref dest, i);

        if (dest.Count > source.Count)
            dest.RemoveRange(source.Count, dest.Count - source.Count);
    }

    private static void CopyBillboard(MyBillboard source, ref List<MyBillboard> dest, int index)
    {
        var triangle = source is MyTriangleBillboard;
        var copy = dest[index];
        if (copy == null || triangle != (copy is MyTriangleBillboard))
        {
            copy = triangle ? new MyTriangleBillboard() : new MyBillboard();
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

        if (source is MyTriangleBillboard sourceTriangle && copy is MyTriangleBillboard copyTriangle)
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

        var sourceInverse = MatrixD.Invert(sourceCameraWorld);
        var sourceToRender = sourceInverse * env.InvViewD;
        foreach (var billboard in Snapshot)
        {
            if (billboard.ParentID != uint.MaxValue ||
                billboard.CustomViewProjection != -1 ||
                billboard.LocalType != MyBillboard.LocalTypeEnum.Custom)
                continue;

            billboard.Position0 = Vector3D.Transform(billboard.Position0, sourceToRender);
            billboard.Position1 = Vector3D.Transform(billboard.Position1, sourceToRender);
            billboard.Position2 = Vector3D.Transform(billboard.Position2, sourceToRender);
            billboard.Position3 = Vector3D.Transform(billboard.Position3, sourceToRender);

            if (billboard is MyTriangleBillboard triangle)
            {
                var normal = Vector3D.TransformNormal(triangle.Normal0, sourceToRender);
                triangle.Normal0 = normal;
            }
        }
    }

    private static int PrepareFromSnapshot()
    {
        var counts = MyBillboardRenderer.m_bucketCounts;
        MyBillboardRenderer.m_batches.Clear();
        for (var i = 0; i < 6; i++)
            counts[i] = 0;

        foreach (var billboard in Snapshot)
            CountBillboard(billboard, counts);

        var total = 0;
        for (var i = 0; i < 6; i++)
            total += counts[i];
        if (total == 0)
        {
            MyBillboardRenderer.m_billboardCountSafe = 0;
            return 0;
        }

        var safe = total > 32768 ? 32768 : total;
        var tempSize = MyBillboardRenderer.m_tempBuffer.Length;
        while (total > tempSize)
            tempSize *= 2;
        Array.Resize(ref MyBillboardRenderer.m_tempBuffer, tempSize);

        var arrays = MyBillboardRenderer.m_arrayDataBillboards;
        var dataSize = arrays.Length;
        while (safe > dataSize)
            dataSize *= 2;
        arrays.Resize(dataSize);
        MyBillboardRenderer.m_arrayDataBillboards = arrays;

        for (var i = 0; i < 6; i++)
            MyBillboardRenderer.m_bucketBatches[i] = default;

        MyBillboardRenderer.m_lastBatchOffset = 0;
        var indices = MyBillboardRenderer.m_bucketIndices;
        indices[0] = 0;
        for (var i = 1; i < 6; i++)
            indices[i] = indices[i - 1] + counts[i - 1];

        foreach (var billboard in Snapshot)
            PlaceBillboard(billboard, indices);

        indices[0] = 0;
        for (var i = 1; i < 6; i++)
            indices[i] = indices[i - 1] + counts[i - 1];

        for (var i = 0; i < 6; i++)
            if (i != 3 && i != 4 && counts[i] > 0)
                Array.Sort(MyBillboardRenderer.m_tempBuffer, indices[i], counts[i]);

        MyBillboardRenderer.m_billboardCountSafe = safe;
        return safe;
    }

    private static void CountBillboard(MyBillboard billboard, int[] counts)
    {
        if (billboard == null)
            return;
        var bucket = MyBillboardRenderer.GetBillboardBucket(billboard);
        if ((uint)bucket < 6)
            counts[bucket]++;
    }

    private static void PlaceBillboard(MyBillboard billboard, int[] indices)
    {
        if (billboard == null)
            return;
        var bucket = MyBillboardRenderer.GetBillboardBucket(billboard);
        if ((uint)bucket < 6)
            MyBillboardRenderer.m_tempBuffer[indices[bucket]++] = billboard;
    }

    private static void FillHistogram()
    {
        Array.Clear(BlendHistogram, 0, BlendHistogram.Length);
        foreach (var billboard in Snapshot)
        {
            var blend = (int)billboard.BlendType;
            if ((uint)blend < (uint)BlendHistogram.Length)
                BlendHistogram[blend]++;
        }
    }

    private static bool HasBucket(int bucket)
    {
        return MyBillboardRenderer.m_bucketBatches is { } batches &&
               bucket >= 0 &&
               bucket < batches.Length &&
               batches[bucket].Count > 0;
    }

    private static string DescribeSample()
    {
        if (Snapshot.Count == 0)
            return "sample=none";
        var billboard = Snapshot[0];
        return "sample blend=" + billboard.BlendType +
               " mat=" + billboard.Material +
               " p0=" + billboard.Position0;
    }

    private static string DescribeBuckets()
    {
        var batches = MyBillboardRenderer.m_bucketBatches;
        var buckets = "null";
        if (batches != null)
        {
            buckets = "";
            for (var i = 0; i < batches.Length; i++)
            {
                if (i > 0)
                    buckets += ",";
                buckets += batches[i].Count;
            }
        }
        return "eval=" + DlssRuntime.EvaluatedThisFrame +
               " live=" + DlssRuntime.IsLive +
               " bucketBatches=" + buckets;
    }

    private static string _lastHudLog;

    public static void LogHudOnce(string message)
    {
        DebugLog.WriteFrame(message);
        if (_lastHudLog == message)
            return;
        _lastHudLog = message;
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
    private static bool Prefix(MyRenderContext rc, IRtvBindable target)
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
    typeof(Action))]
internal static class BillboardFrameCompletePatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        BillboardOutputPass.PublishCompletedFrame();
    }
}
