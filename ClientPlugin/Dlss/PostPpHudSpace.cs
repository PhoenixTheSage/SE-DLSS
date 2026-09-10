using System.Collections.Generic;
using VRageMath;
using VRageRender;

namespace ClientPlugin.Dlss;

/// <summary>
/// Lobbies-style screen lock: freeze PostPP HUD in <b>view space</b> with the
/// layout camera, then parent those quads to the interpolated render camera
/// every present. A world-space delta from MainCamera (sampled on the render
/// thread) against live persistents left the overlay in last-pose world space
/// — the doorway ghost — and flickered when the two cameras disagreed by a
/// millimetre.
/// </summary>
internal static class PostPpHudSpace
{
    public static void ToViewLocal(List<MyBillboard> copies, MatrixD layoutView)
    {
        if (copies == null || copies.Count == 0 || !layoutView.IsValid())
            return;
        for (var i = 0; i < copies.Count; i++)
            TransformScreenSpace(copies[i], ref layoutView);
    }

    public static void ToWorldFromViewLocal(List<MyBillboard> copies)
    {
        if (copies == null || copies.Count == 0)
            return;
        var env = MyRender11.Environment?.Matrices;
        if (env == null)
            return;
        var renderWorld = env.InvViewD;
        if (!renderWorld.IsValid())
            return;
        for (var i = 0; i < copies.Count; i++)
            TransformScreenSpace(copies[i], ref renderWorld);
    }

    static void TransformScreenSpace(MyBillboard billboard, ref MatrixD matrix)
    {
        if (billboard == null || billboard.CustomViewProjection != -1)
            return;
        if (billboard.ParentID != uint.MaxValue)
            return;
        if (billboard.LocalType is MyBillboard.LocalTypeEnum.Line or MyBillboard.LocalTypeEnum.Point)
            return;

        Vector3D.Transform(ref billboard.Position0, ref matrix, out billboard.Position0);
        Vector3D.Transform(ref billboard.Position1, ref matrix, out billboard.Position1);
        Vector3D.Transform(ref billboard.Position2, ref matrix, out billboard.Position2);
        Vector3D.Transform(ref billboard.Position3, ref matrix, out billboard.Position3);
    }
}
