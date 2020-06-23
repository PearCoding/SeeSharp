using System.Numerics;
using SeeSharp.Core.Geometry;

namespace SeeSharp.Core.Shading.Background {
    /// <summary>
    /// Base class for all sorts of sky models, image based lighting, etc.
    /// </summary>
    public abstract class Background {
        /// <summary>
        /// Computes the emitted radiance in a given direction. All backgrounds are invariant with respect to the position.
        /// </summary>
        public abstract ColorRGB EmittedRadiance(Vector3 direction);

        public abstract BackgroundSample SampleDirection(Vector2 primary);
        public abstract float DirectionPdf(Vector3 Direction);
        public abstract (Ray, ColorRGB, float) SampleRay(Vector2 primaryPos, Vector2 primaryDir);

        /// <summary>
        /// Computes the pdf value for sampling a ray from the background towards the scene.
        /// </summary>
        /// <param name="point">A point along the ray. Could be the start, end, or some other point.</param>
        /// <param name="direction">Direction of the ray (i.e., from the background to the scene).</param>
        /// <returns></returns>
        public abstract float RayPdf(Vector3 point, Vector3 direction);

        public Vector3 SceneCenter;
        public float SceneRadius;
    }
}