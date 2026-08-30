using System;
using VRageMath;
using VRageRender;

namespace ClientPlugin.Dlss;

internal static class Jitter
{
    public static float OffsetX { get; private set; }
    public static float OffsetY { get; private set; }
    public static Matrix JitteredInvViewProjection { get; private set; }
    public static Matrix UnjitteredViewProjection { get; private set; }
    public static Matrix PreviousViewProjection { get; private set; }
    public static bool HasPrevious { get; private set; }

    private static int frameIndex;
    private static bool applied;
    private static Vector3D previousCameraPos;
    private static Vector3 previousForward;
    private static float previousFovV;
    private static bool hasCameraSample;
    private static Matrix savedProjection;
    private static Matrix savedProjectionForSkybox;
    private static Matrix savedViewProjectionAt0;
    private static Matrix savedInvViewProjectionAt0;
    private static Matrix savedInvProjection;
    private static MatrixD savedViewProjectionD;
    private static MatrixD savedInvViewProjectionD;

    public static void Reset()
    {
        frameIndex = 0;
        HasPrevious = false;
        applied = false;
        hasCameraSample = false;
        OffsetX = 0f;
        OffsetY = 0f;
        JitteredInvViewProjection = default(Matrix);
        UnjitteredViewProjection = default(Matrix);
        PreviousViewProjection = default(Matrix);
    }

    public static void BeginFrame()
    {
        PreviousViewProjection = UnjitteredViewProjection;
        OffsetX = Halton(frameIndex, 2) - 0.5f;
        OffsetY = Halton(frameIndex, 3) - 0.5f;
        frameIndex++;
        HasPrevious = frameIndex > 1;
    }

    public static bool ConsumeCameraCut()
    {
        var env = MyRender11.Environment != null ? MyRender11.Environment.Matrices : null;
        if (env == null)
            return !HasPrevious;
        var pos = env.CameraPosition;
        var forward = env.ViewAt0.Forward;
        var fov = env.FovV;
        bool cut = !HasPrevious || !hasCameraSample;
        if (hasCameraSample)
        {
            double dist = Vector3D.Distance(pos, previousCameraPos);
            float align = Vector3.Dot(forward, previousForward);
            float fovDelta = Math.Abs(fov - previousFovV);
            if (dist > 40.0 || align < 0.82f || fovDelta > 0.04f)
                cut = true;
        }
        previousCameraPos = pos;
        previousForward = forward;
        previousFovV = fov;
        hasCameraSample = true;
        return cut;
    }

    public static bool TryGetRenderSize(out int width, out int height)
    {
        width = DlssRuntime.InternalWidth;
        height = DlssRuntime.InternalHeight;
        if (width > 0 && height > 0)
            return true;
        var size = DlssRuntime.DesiredInternalResolution();
        width = size.X;
        height = size.Y;
        return width > 0 && height > 0;
    }

    public static void Apply(MyEnvironmentMatrices env)
    {
        if (applied || env == null)
            return;
        int width;
        int height;
        if (!TryGetRenderSize(out width, out height))
            return;

        savedProjection = env.Projection;
        savedProjectionForSkybox = env.ProjectionForSkybox;
        savedViewProjectionAt0 = env.ViewProjectionAt0;
        savedInvViewProjectionAt0 = env.InvViewProjectionAt0;
        savedInvProjection = env.InvProjection;
        savedViewProjectionD = env.ViewProjectionD;
        savedInvViewProjectionD = env.InvViewProjectionD;
        UnjitteredViewProjection = env.ViewProjectionAt0;

        float ndcX;
        float ndcY;
        GetProjectionNdc(out ndcX, out ndcY);
        env.Projection.M31 += ndcX;
        env.Projection.M32 += ndcY;
        env.ProjectionForSkybox.M31 += ndcX;
        env.ProjectionForSkybox.M32 += ndcY;
        env.ViewProjectionAt0 = env.ViewAt0 * env.Projection;
        env.InvViewProjectionAt0 = Matrix.Invert(env.ViewProjectionAt0);
        env.InvProjection = Matrix.Invert(env.Projection);
        env.ViewProjectionD = env.ViewD * (MatrixD)env.Projection;
        env.InvViewProjectionD = MatrixD.Invert(env.ViewProjectionD);
        JitteredInvViewProjection = env.InvViewProjectionAt0;
        applied = true;
    }

    public static void Restore(MyEnvironmentMatrices env)
    {
        if (!applied || env == null)
            return;
        env.Projection = savedProjection;
        env.ProjectionForSkybox = savedProjectionForSkybox;
        env.ViewProjectionAt0 = savedViewProjectionAt0;
        env.InvViewProjectionAt0 = savedInvViewProjectionAt0;
        env.InvProjection = savedInvProjection;
        env.ViewProjectionD = savedViewProjectionD;
        env.InvViewProjectionD = savedInvViewProjectionD;
        applied = false;
    }

    public static void GetProjectionNdc(out float ndcX, out float ndcY)
    {
        ndcX = 0f;
        ndcY = 0f;
        int width;
        int height;
        if (!TryGetRenderSize(out width, out height))
            return;
        float jitterNdcX = OffsetX * 2f / width;
        float jitterNdcY = OffsetY * 2f / height;
        ndcX = -jitterNdcX;
        ndcY = jitterNdcY;
    }

    public static void CopyToArray(Matrix matrix, float[] dest)
    {
        dest[0] = matrix.M11; dest[1] = matrix.M12; dest[2] = matrix.M13; dest[3] = matrix.M14;
        dest[4] = matrix.M21; dest[5] = matrix.M22; dest[6] = matrix.M23; dest[7] = matrix.M24;
        dest[8] = matrix.M31; dest[9] = matrix.M32; dest[10] = matrix.M33; dest[11] = matrix.M34;
        dest[12] = matrix.M41; dest[13] = matrix.M42; dest[14] = matrix.M43; dest[15] = matrix.M44;
    }

    private static float Halton(int index, int baseValue)
    {
        float result = 0f;
        float inv = 1f / baseValue;
        float f = inv;
        int i = index + 1;
        while (i > 0)
        {
            result += (i % baseValue) * f;
            i /= baseValue;
            f *= inv;
        }
        return result;
    }
}
