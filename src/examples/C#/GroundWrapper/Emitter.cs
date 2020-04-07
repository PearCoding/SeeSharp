using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ground
{
    public class Emitter {
        public int MeshId { get; }

        public int EmitterId { get; }

        public Emitter(int meshId, int emitterId) {
            MeshId = meshId;
            EmitterId = EmitterId;
        }

        public SurfaceSample WrapPrimaryToSurface(float u, float v) {
            return WrapPrimarySampleToEmitterSurface(EmitterId, u, v);
        }

        public EmitterSample WrapPrimaryToRay(Vector2 primaryPos, Vector2 primaryDir) {
            return WrapPrimarySampleToEmitterRay(EmitterId, primaryPos, primaryDir);
        }

        public float Jacobian(SurfacePoint point) {
            Debug.Assert(point.meshId == this.MeshId,
                "Attempted to compute the jacobian for the wrong light source.");

            return ComputePrimaryToEmitterSurfaceJacobian(ref point);
        }

        public ColorRGB ComputeEmission(SurfacePoint point, Vector3 outDir) {
            Debug.Assert(point.meshId == this.MeshId,
                "Attempted to compute emission for the wrong light source.");

            return ComputeEmission(ref point, outDir);
        }

        [DllImport("Ground", CallingConvention=CallingConvention.Cdecl)]
        static extern SurfaceSample WrapPrimarySampleToEmitterSurface(
            int emitterId, float u, float v);

        [DllImport("Ground", CallingConvention=CallingConvention.Cdecl)]
        static extern float ComputePrimaryToEmitterSurfaceJacobian(
            [In] ref SurfacePoint point);

        [DllImport("Ground", CallingConvention = CallingConvention.Cdecl)]
        static extern EmitterSample WrapPrimarySampleToEmitterRay(int emitterId,
            Vector2 primaryPos, Vector2 primaryDir);

        [DllImport("Ground", CallingConvention=CallingConvention.Cdecl)]
        static extern ColorRGB ComputeEmission([In] ref SurfacePoint point,
            Vector3 outDir);
    }
}