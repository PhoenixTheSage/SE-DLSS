using System;
using System.Collections.Generic;
using System.Threading;
using ClientPlugin.Dlss;
using HarmonyLib;
using Sandbox.Game.World;
using SharpDX.Direct3D11;
using VRage.Game;
using VRage.Render11.RenderContext;
using VRage.Render11.Resources;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace ClientPlugin.Patches;

// PostPP Rich HUD is laid out in Master's PixelToWorld space from
// MySector.MainCamera. SmoothFrames interpolates the render camera, so
// gathering those world quads as-is leaves the overlay in last-pose world
// space (doorway ghost). Freeze copies in layout view space on the session
// thread, parent them to Environment.InvViewD at present. Do not rewrite
// view-projection (that made a skybox-locked blob).
internal static class BillboardOutputPass
{
    private const int PostPpBucket = 4;

    [ThreadStatic]
    private static bool _drawingPostPp;

    private static bool _drewHudThisScene;
    private static int _drewHudCount;
    private static readonly object SnapshotLock = new();
    private static readonly List<MyBillboard> PendingAdds = new(512);
    private static readonly List<MyBillboard> UniqueScratch = new(512);
    private static List<MyBillboard> _published = new(512);
    private static List<MyBillboard> _publishScratch = new(512);
    private static readonly List<MyBillboard> Snapshot = new(512);
    private static readonly List<MyBillboard> CaptureRefs = new(512);
    // Capture (render thread) and Dedupe (HUD / Publish postfix) used to share
    // one HashSet. Master's DrawUI walks persistents on the session thread;
    // CaptureLivePostPp walks them on the render thread. .NET 10 HashSet.Add
    // throws InvalidOperationException; RHUD ExceptionHandler then reloads.
    private static readonly HashSet<MyBillboard> CaptureSeen = [];
    private static readonly HashSet<MyBillboard> DedupeSeen = [];
    private static readonly int[] BlendHistogram = new int[8];
    private static int _lastSubmitTick;
    private static int _walkingPersistents;
    private static bool _publishing;
    private static bool _publishedAreViewLocal;

    // Retain the last complete HUD frame across empty submissions, then expire it to prevent frozen overlays.
    private const int StaleHudMs = 200;

    public static void BeginDraw()
    {
        _drewHudThisScene = false;
        _drewHudCount = 0;
    }

    public static void Reset()
    {
        _drawingPostPp = false;
        _drewHudThisScene = false;
        _drewHudCount = 0;
        _walkingPersistents = 0;
        _publishing = false;

        lock (SnapshotLock)
        {
            PendingAdds.Clear();
            UniqueScratch.Clear();
            _published.Clear();
            _publishScratch.Clear();
            Snapshot.Clear();
            CaptureRefs.Clear();
            CaptureSeen.Clear();
            DedupeSeen.Clear();
            _publishedAreViewLocal = false;
            Array.Clear(BlendHistogram, 0, BlendHistogram.Length);
            _lastSubmitTick = 0;
        }
    }

    public static void PublishCompletedFrame()
    {
        // Master FinishDraw: ApplyActionOnPersistentBillboards(Action) after
        // Parallel.For writes persistents. That call is session-thread and
        // often has empty PendingAdds (in-place HUD, not AddBillboard clones).
        if (!DlssRuntime.IsLive || _publishing)
            return;
        if (IsRenderThread())
            return;

        _publishing = true;
        try
        {
            UniqueScratch.Clear();
            DedupeSeen.Clear();
            _walkingPersistents++;
            try
            {
                MyRenderProxy.ApplyActionOnPersistentBillboards(ConsiderPublish);
            }
            finally
            {
                _walkingPersistents--;
            }

            int count;
            lock (SnapshotLock)
            {
                if (UniqueScratch.Count == 0)
                    DedupeInto(PendingAdds, UniqueScratch);
                PendingAdds.Clear();
                if (UniqueScratch.Count == 0)
                    return;

                FreezeInto(UniqueScratch, _publishScratch);
                var camera = MySector.MainCamera;
                var view = camera != null ? camera.ViewMatrix : default;
                if (camera != null && view.IsValid())
                {
                    PostPpHudSpace.ToViewLocal(_publishScratch, view);
                    _publishedAreViewLocal = true;
                }
                else
                {
                    _publishedAreViewLocal = false;
                }

                var published = _published;
                _published = _publishScratch;
                _publishScratch = published;

                NoteHudSubmitLocked();
                count = _published.Count;
            }

            LogHudOnce("Published view-local PostPP frame count=" + count);
        }
        catch (Exception e)
        {
            // Postfix runs under Master's DrawUI / ExceptionHandler.Run.
            // A throw here closes the terminal. Log and keep Master alive.
            DebugLog.Write("PublishCompletedFrame: " + e);
        }
        finally
        {
            _publishing = false;
        }
    }

    public static void NoteAdd(MyBillboard billboard)
    {
        if (!DlssRuntime.IsLive || !IsPostPp(billboard))
            return;
        lock (SnapshotLock)
        {
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
                   " pending=" + PendingCount() +
                   " yieldPresent=" + DlssRuntime.ShouldYieldPresentPath +
                   " " + DescribeBuckets());
        if (!DlssRuntime.IsLive)
            return false;

        // Keen binds internal GBuffer depth as DSV against an output-sized
        // dest, so D3D drops the draw. Skip it. Composite after CopyToRT
        // onto Backbuffer (DrawScene postfix), not onto this HDR UAV.
        return true;
    }

    public static void TryDrawOnSceneDest(ISrvBindable source, string reason)
    {
        if (!DlssRuntime.IsLive || source == null)
            return;
        if (_drewHudThisScene && PublishedCount() <= _drewHudCount)
            return;
        var dest = AsHudDest(source);
        var rc = MyRender11.RC;
        if (dest == null || rc == null)
            return;
        TryDrawOnto(rc, dest, HudViewport(dest), reason);
    }

    public static void TryDrawAfterSceneBlit()
    {
        if (!DlssRuntime.IsLive)
            return;
        if (_drewHudThisScene && PublishedCount() <= _drewHudCount)
            return;
        var dest = UnwrapHudTarget(MyRender11.Backbuffer);
        var rc = MyRender11.RC;
        if (dest == null || rc == null)
            return;
        DlssRuntime.RestoreViewportToOutput();
        var viewport = DlssRuntime.OutputPixelSize();
        if (viewport.X <= 0 || viewport.Y <= 0)
            return;
        TryDrawOnto(rc, dest, viewport, "present");
    }

    /// <summary>
    /// Composite bucket 4 onto the scene dest (HDR / chromatic / LDR) with
    /// no DSV. Do not rewrite view-projection (that made a world-space ghost).
    /// </summary>
    static bool TryDrawOnto(MyRenderContext rc, IRtvBindable dest, Vector2I viewport, string reason)
    {
        if (_drawingPostPp || rc == null || dest == null)
            return true;
        if (_drewHudThisScene && PublishedCount() <= _drewHudCount)
            return true;

        DlssRuntime.BindUnjitteredHudConstants();
        if (!EnsurePostPpBatches(rc))
        {
            LogHudOnce(reason + " no PostPP bucket dest=" + dest.Size + " " + DescribeBuckets());
            return true;
        }

        if (dest.Rtv == null)
        {
            LogHudOnce(reason + " dest has no RTV size=" + dest.Size);
            return true;
        }

        _drawingPostPp = true;
        try
        {
            rc.ComputeShader.SetUav(0, null);
            rc.SetBlendState(MyBlendStateManager.BlendAlphaPremult);
            rc.SetDepthStencilState(MyDepthStencilStateManager.IgnoreDepthStencil);
            BindHudRtv(rc, dest);
            // SetRtv does not set the viewport. Backbuffer.Size follows
            // ResolutionI (internal) after SetDRS; the DXGI buffer is output.
            rc.SetViewport(0f, 0f, viewport.X, viewport.Y);
            try
            {
                MyBillboardRenderer.Render(
                    rc, null, MyBillboardRenderer.m_bucketBatches[PostPpBucket], false, true);
                _drewHudThisScene = true;
                _drewHudCount = Math.Max(PostPpGatheredCount(), Snapshot.Count);
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

        LogHudOnce(reason + " dest=" + dest.GetType().Name + " " + dest.Size +
                   " output=" + viewport + " " + DescribeBuckets());
        return true;
    }

    static bool EnsurePostPpBatches(MyRenderContext rc)
    {
        if (rc == null || _drawingPostPp)
            return HasBucket(PostPpBucket);

        CaptureLivePostPp();
        var live = Snapshot.Count;
        if (live == 0)
            return false;

        var gathered = PostPpGatheredCount();
        var safe = PrepareFromSnapshot();
        if (safe <= 0)
            return false;

        // Camera-relative verts. Reusing last frame's VB with a new view
        // locks the overlay to skybox space (the black blob).
        MyBillboardRenderer.GatherInternal(rc);
        MyBillboardRenderer.TransferData(rc);
        LogHudOnce("rebuild PostPP live=" + live + " gathered=" + gathered +
                   " batches=" + (HasBucket(PostPpBucket) ? MyBillboardRenderer.m_bucketBatches[PostPpBucket].Count : 0) +
                   " cvp=" + (Snapshot.Count > 0 ? Snapshot[0].CustomViewProjection.ToString() : "none"));
        return HasBucket(PostPpBucket);
    }

    static int PostPpGatheredCount()
    {
        var counts = MyBillboardRenderer.m_bucketCounts;
        if (counts == null || (uint)PostPpBucket >= (uint)counts.Length)
            return 0;
        return counts[PostPpBucket];
    }

    static Vector2I HudViewport(IRtvBindable dest)
    {
        var output = DlssRuntime.OutputPixelSize();
        if (output.X <= 0 || output.Y <= 0)
            return dest != null ? dest.Size : default;
        if (dest != null && dest.Size.X == output.X && dest.Size.Y == output.Y)
            return dest.Size;
        return output;
    }

    static IRtvBindable AsHudDest(object texture)
    {
        switch (texture)
        {
            case IBorrowedCustomTexture borrowed:
                return UnwrapHudTarget(borrowed.Linear) ?? UnwrapHudTarget(borrowed.SRgb);
            case ICustomTexture custom:
                return UnwrapHudTarget(custom.Linear) ?? UnwrapHudTarget(custom.SRgb);
            case IRtvBindable rtv:
                return UnwrapHudTarget(rtv);
            default:
                return null;
        }
    }

    static bool CaptureLivePostPp()
    {
        var viewLocal = false;
        lock (SnapshotLock)
        {
            if (HudSnapshotIsStaleLocked())
                ClearPublishedLocked();

            if (_published.Count > 0 && _publishedAreViewLocal)
            {
                FreezeInto(_published, Snapshot);
                viewLocal = true;
            }
        }

        if (viewLocal)
        {
            PostPpHudSpace.ToWorldFromViewLocal(Snapshot);
            return Snapshot.Count > 0;
        }

        CaptureRefs.Clear();
        CaptureSeen.Clear();

        try
        {
            var read = MyRenderProxy.BillboardsRead;
            if (read != null)
            {
                foreach (var billboard in read)
                    ConsiderCapture(billboard);
            }

            var oncePool = MyBillboardRenderer.m_billboardsOncePool;
            if (oncePool != null)
            {
                var onceCount = oncePool.GetAllocatedCount();
                for (var i = 0; i < onceCount; i++)
                    ConsiderCapture(oncePool.GetAllocatedItem(i));
            }

            _walkingPersistents++;
            try
            {
                MyRenderProxy.ApplyActionOnPersistentBillboards(ConsiderCapture);
            }
            finally
            {
                _walkingPersistents--;
            }
        }
        catch (Exception e)
        {
            LogHudOnce("CaptureLivePostPp proxy: " + e.GetType().Name);
        }

        lock (SnapshotLock)
        {
            foreach (var billboard in PendingAdds)
                ConsiderCapture(billboard);

            FreezeInto(CaptureRefs, Snapshot);
        }

        return Snapshot.Count > 0;
    }

    static IRtvBindable UnwrapHudTarget(IRtvBindable target)
    {
        if (target is ICustomTexture custom)
        {
            if (custom.Linear != null)
                return custom.Linear;
            if (custom.SRgb != null)
                return custom.SRgb;
        }

        return target;
    }

    static void BindHudRtv(MyRenderContext rc, IRtvBindable target)
    {
        rc.ResetTargets();
        var rtv = target?.Rtv;
        if (rtv != null && rc.DeviceContext != null)
            rc.DeviceContext.OutputMerger.SetTargets((DepthStencilView)null, 1, new[] { rtv });
        if (target != null)
            rc.SetRtv(target);
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
        BindHudRtv(rc, UnwrapHudTarget(target));
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

    private static int PublishedCount()
    {
        lock (SnapshotLock)
            return _published.Count;
    }

    private static int CaptureForDraw()
    {
        var viewLocal = false;
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
            viewLocal = _publishedAreViewLocal;
        }

        if (viewLocal)
            PostPpHudSpace.ToWorldFromViewLocal(Snapshot);
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
        _publishedAreViewLocal = false;
    }

    private static bool IsRenderThread()
    {
        var renderThread = MyRender11.RenderThread;
        return renderThread != null && Thread.CurrentThread == renderThread;
    }

    private static void ConsiderPublish(MyBillboard billboard)
    {
        if (billboard == null || !IsPostPp(billboard) || !DedupeSeen.Add(billboard))
            return;
        UniqueScratch.Add(billboard);
    }

    private static void ConsiderCapture(MyBillboard billboard)
    {
        if (billboard == null || !IsPostPp(billboard) || !CaptureSeen.Add(billboard))
            return;
        CaptureRefs.Add(billboard);
    }

    private static bool IsPostPp(MyBillboard billboard)
    {
        return billboard is { BlendType: MyBillboard.BlendTypeEnum.PostPP };
    }

    private static void DedupeInto(List<MyBillboard> source, List<MyBillboard> dest)
    {
        dest.Clear();
        DedupeSeen.Clear();
        foreach (var billboard in source)
        {
            if (billboard == null || !DedupeSeen.Add(billboard))
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
               " cvp=" + billboard.CustomViewProjection +
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
        var counts = "null";
        var bucketCounts = MyBillboardRenderer.m_bucketCounts;
        if (bucketCounts != null)
        {
            counts = "";
            for (var i = 0; i < bucketCounts.Length; i++)
            {
                if (i > 0)
                    counts += ",";
                counts += bucketCounts[i];
            }
        }

        return "eval=" + DlssRuntime.EvaluatedThisFrame +
               " live=" + DlssRuntime.IsLive +
               " snapshot=" + Snapshot.Count +
               " published=" + PublishedCount() +
               " bucketCounts=" + counts +
               " bucketBatches=" + buckets;
    }

    public static void LogHudOnce(string message)
    {
        // rebuild / present alternate every frame; do not write SpaceEngineers.log.
        DebugLog.WriteFrame(message);
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
    typeof(Action))]
internal static class BillboardFrameCompletePatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        try
        {
            BillboardOutputPass.PublishCompletedFrame();
        }
        catch (Exception e)
        {
            DebugLog.Write("BillboardFrameCompletePatch: " + e);
        }
    }
}
