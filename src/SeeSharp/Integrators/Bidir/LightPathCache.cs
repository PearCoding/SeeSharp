﻿using SeeSharp.Core;
using SeeSharp.Core.Geometry;
using SeeSharp.Core.Sampling;
using SeeSharp.Core.Shading;
using SeeSharp.Core.Shading.Emitters;
using SeeSharp.Integrators.Common;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;

namespace SeeSharp.Integrators.Bidir {
    /// <summary>
    /// Samples a given number of light paths via random walks through a scene.
    /// The paths are stored in a <see cref="Common.PathCache"/>
    /// </summary>
    public class LightPathCache {
        // Parameters
        public int NumPaths;
        public int MaxDepth;
        public uint BaseSeed = 0xC030114u;

        // Scene specific data
        public Scene Scene;

        // Outputs
        public PathCache PathCache;

        public virtual (Emitter, float, float) SelectLight(float primary) {
            float scaled = Scene.Emitters.Count * primary;
            int idx = Math.Clamp((int)scaled, 0, Scene.Emitters.Count - 1);
            var emitter = Scene.Emitters[idx];
            return (emitter, 1.0f / Scene.Emitters.Count, scaled - idx);
        }

        public virtual float SelectLightPmf(Emitter em) {
            if (em == null) { // background
                return BackgroundProbability;
            } else {
                return 1.0f / Scene.Emitters.Count * (1 - BackgroundProbability);
            }
        }

        public virtual float BackgroundProbability
            => Scene.Background != null ? 1 / (1.0f + Scene.Emitters.Count) : 0;

        /// <summary>
        /// Resets the path cache and populates it with a new set of light paths.
        /// </summary>
        /// <param name="iter">Index of the current iteration, used to seed the random number generator.</param>
        public void TraceAllPaths(uint iter, NextEventPdfCallback nextEventPdfCallback) {
            if (PathCache == null)
                PathCache = new PathCache(NumPaths, MaxDepth);
            else
                PathCache.Clear();

            Parallel.For(0, NumPaths, idx => {
                var seed = RNG.HashSeed(BaseSeed, (uint)idx, iter);
                var rng = new RNG(seed);
                TraceLightPath(rng, nextEventPdfCallback, idx);
            });
        }

        public delegate float NextEventPdfCallback(PathVertex origin, PathVertex primary, Vector3 nextDirection);

        class NotifyingCachedWalk : CachedRandomWalk {
            public NextEventPdfCallback callback;
            public NotifyingCachedWalk(Scene scene, RNG rng, int maxDepth, PathCache cache, int pathIdx)
                : base(scene, rng, maxDepth, cache, pathIdx) {
            }

            protected override ColorRGB OnHit(Ray ray, SurfacePoint hit, float pdfFromAncestor,
                                              ColorRGB throughput, int depth, float toAncestorJacobian) {
                // Call the base first, so the vertex gets created
                var weight = base.OnHit(ray, hit, pdfFromAncestor, throughput, depth, toAncestorJacobian);

                // The next event pdf is computed once the path has three vertices
                if (depth == 2 && callback != null) {
                    ref var vertex = ref cache[pathIdx, lastId];
                    var primary = cache[pathIdx, vertex.AncestorId];
                    var origin = cache[pathIdx, primary.AncestorId];
                    vertex.PdfNextEventAncestor = callback(origin, primary, ray.Direction);
                }

                return weight;
            }
        }

        /// <summary>
        /// Called for each light path, used to populate the path cache.
        /// </summary>
        /// <returns>
        /// The index of the last vertex along the path.
        /// </returns>
        public virtual int TraceLightPath(RNG rng, NextEventPdfCallback nextEvtCallback, int idx) {
            // Select an emitter or the background
            float lightSelPrimary = rng.NextFloat();
            if (lightSelPrimary > BackgroundProbability) { // Sample from an emitter in the scene
                // Remap the primary sample
                lightSelPrimary = (lightSelPrimary - BackgroundProbability) / (1 - BackgroundProbability);
                return TraceEmitterPath(rng, lightSelPrimary, nextEvtCallback, idx);
            } else { // Sample from the background
                return TraceBackgroundPath(rng, nextEvtCallback, idx);
            }
        }

        public delegate void ProcessVertex(PathVertex vertex, PathVertex ancestor, Vector3 dirToAncestor);

        /// <summary>
        /// Utility function that iterates over a light path, starting on the end point, excluding the point on the light itself.
        /// </summary>
        public void ForEachVertex(int index, ProcessVertex func) {
            int n = PathCache.Length(index);
            for (int i = 1; i < n; ++i) {
                var ancestor = PathCache[index, i-1];
                var vertex = PathCache[index, i];
                var dirToAncestor = ancestor.Point.Position - vertex.Point.Position;
                func(vertex, ancestor, dirToAncestor);
            }
        }

        int TraceEmitterPath(RNG rng, float lightSelPrimary, NextEventPdfCallback nextEventPdfCallback, int idx) {
            var (emitter, selectProb, _) = SelectLight(lightSelPrimary);
            selectProb *= 1 - BackgroundProbability;

            // Sample a ray from the emitter
            var primaryPos = rng.NextFloat2D();
            var primaryDir = rng.NextFloat2D();
            var emitterSample = emitter.SampleRay(primaryPos, primaryDir); ;

            // Account for the light selection probability in the MIS weights
            emitterSample.Pdf *= selectProb;

            // Perform a random walk through the scene, storing all vertices along the path
            var walker = new NotifyingCachedWalk(Scene, rng, MaxDepth, PathCache, idx);
            walker.callback = nextEventPdfCallback;
            walker.StartFromEmitter(emitterSample, emitterSample.Weight / selectProb);
            return walker.lastId;
        }

        int TraceBackgroundPath(RNG rng, NextEventPdfCallback nextEventPdfCallback, int idx) {
            // Sample a ray from the background towards the scene
            var primaryPos = rng.NextFloat2D();
            var primaryDir = rng.NextFloat2D();
            var (ray, weight, pdf) = Scene.Background.SampleRay(primaryPos, primaryDir);

            // Account for the light selection probability
            pdf *= BackgroundProbability;
            weight /= BackgroundProbability;

            if (pdf == 0) // Avoid NaNs
                return -1;

            Debug.Assert(float.IsFinite(weight.Average));

            // Perform a random walk through the scene, storing all vertices along the path
            var walker = new NotifyingCachedWalk(Scene, rng, MaxDepth, PathCache, idx);
            walker.callback = nextEventPdfCallback;
            walker.StartFromBackground(ray, weight, pdf);
            return walker.lastId;
        }
    }
}
