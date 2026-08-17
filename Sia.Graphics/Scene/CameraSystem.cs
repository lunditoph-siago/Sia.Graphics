using Sia;
using Sia.Math;

namespace Sia.Graphics.Scene;

public sealed class CameraSystem()
    : SystemBase(Matchers.Of<Camera, CameraActive, GlobalTransform, CameraMatrices>())
{
    public override void Execute(WorldContext context, IEntityQuery query)
    {
        var viewport = context.World.AcquireAddon<Viewport>().Value;
        var aspect = viewport.Height > 0 ? viewport.Width / viewport.Height : 1.0f;

        foreach (var entity in query) {
            var camera = entity.Get<Camera>();
            var worldMatrix = entity.Get<GlobalTransform>().WorldMatrix;

            var view = math.inverse(worldMatrix);
            var proj = float4x4.PerspectiveFov(camera.VerticalFovRadians, aspect, camera.Near, camera.Far);
            var viewProj = math.mul(proj, view);

            entity.Get<CameraMatrices>() = new CameraMatrices(
                View: view,
                Proj: proj,
                ViewProj: viewProj,
                InvViewProj: math.inverse(viewProj),
                WorldPosition: worldMatrix.c3.xyz,
                Frustum: Frustum.CreateFromViewProjection(viewProj));
        }
    }
}
