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

    private static int _frameIndex;
    private static bool _applied;
    private static Vector3D _previousCameraPos;
    private static Vector3 _previousForward;
    private static float _previousFovV;
    private static bool _hasCameraSample;
    private static Matrix _savedProjection;
    private static Matrix _savedProjectionForSkybox;
    private static Matrix _savedViewProjectionAt0;
    private static Matrix _savedInvViewProjectionAt0;
    private static Matrix _savedInvProjection;
    private static MatrixD _savedViewProjectionD;
    private static MatrixD _savedInvViewProjectionD;

    public static void Reset()
    {
        if (_applied)
        {
            var env = MyRender11.Environment;
            if (env != null)
                Restore(env.Matrices);
        }

        _frameIndex = 0;
        HasPrevious = false;
        _applied = false;
        _hasCameraSample = false;
        OffsetX = 0f;
        OffsetY = 0f;
        JitteredInvViewProjection = default(Matrix);
        UnjitteredViewProjection = default(Matrix);
        PreviousViewProjection = default(Matrix);
    }

    public static void BeginFrame()
    {
        PreviousViewProjection = UnjitteredViewProjection;
        OffsetX = Halton(_frameIndex, 2) - 0.5f;
        OffsetY = Halton(_frameIndex, 3) - 0.5f;
        _frameIndex++;
        HasPrevious = _frameIndex > 1;
    }

    public static bool ConsumeCameraCut()
    {
        var env = MyRender11.Environment != null ? MyRender11.Environment.Matrices : null;
        if (env == null)
            return !HasPrevious;
        var pos = env.CameraPosition;
        var forward = env.ViewAt0.Forward;
        var fov = env.FovV;
        var cut = !HasPrevious || !_hasCameraSample;
        if (_hasCameraSample)
        {
            var dist = Vector3D.Distance(pos, _previousCameraPos);
            var align = Vector3.Dot(forward, _previousForward);
            var fovDelta = Math.Abs(fov - _previousFovV);
            if (dist > 40.0 || align < 0.82f || fovDelta > 0.04f)
                cut = true;
        }
        _previousCameraPos = pos;
        _previousForward = forward;
        _previousFovV = fov;
        _hasCameraSample = true;
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
        if (_applied || env == null)
            return;
        if (!TryGetRenderSize(out _, out _))
            return;

        _savedProjection = env.Projection;
        _savedProjectionForSkybox = env.ProjectionForSkybox;
        _savedViewProjectionAt0 = env.ViewProjectionAt0;
        _savedInvViewProjectionAt0 = env.InvViewProjectionAt0;
        _savedInvProjection = env.InvProjection;
        _savedViewProjectionD = env.ViewProjectionD;
        _savedInvViewProjectionD = env.InvViewProjectionD;
        UnjitteredViewProjection = env.ViewProjectionAt0;

        GetProjectionNdc(out var ndcX, out var ndcY);
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
        _applied = true;
    }

    public static void Restore(MyEnvironmentMatrices env)
    {
        if (!_applied || env == null)
            return;
        env.Projection = _savedProjection;
        env.ProjectionForSkybox = _savedProjectionForSkybox;
        env.ViewProjectionAt0 = _savedViewProjectionAt0;
        env.InvViewProjectionAt0 = _savedInvViewProjectionAt0;
        env.InvProjection = _savedInvProjection;
        env.ViewProjectionD = _savedViewProjectionD;
        env.InvViewProjectionD = _savedInvViewProjectionD;
        _applied = false;
    }

    public static void GetProjectionNdc(out float ndcX, out float ndcY)
    {
        ndcX = 0f;
        ndcY = 0f;
        if (!TryGetRenderSize(out var width, out var height))
            return;
        var jitterNdcX = OffsetX * 2f / width;
        var jitterNdcY = OffsetY * 2f / height;
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
        var result = 0f;
        var inv = 1f / baseValue;
        var f = inv;
        var i = index + 1;
        while (i > 0)
        {
            result += (i % baseValue) * f;
            i /= baseValue;
            f *= inv;
        }
        return result;
    }
}
