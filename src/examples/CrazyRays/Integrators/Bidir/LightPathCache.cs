﻿using GroundWrapper;
using GroundWrapper.Sampling;
using GroundWrapper.Shading.Emitters;
using Integrators.Common;
using System;
using System.Numerics;
using System.Threading.Tasks;

namespace Integrators.Bidir {
    /// <summary>
    /// Samples a given number of light paths via random walks through a scene.
    /// The paths are stored in a <see cref="PathCache"/>
    /// </summary>
    public class LightPathCache {
        // Parameters
        public int NumPaths;
        public int MaxDepth;
        public uint BaseSeed = 0xC030114u;

        // Scene specific data
        public Scene scene;

        // Outputs
        public PathCache pathCache;
        public int[] endpoints;

        public virtual (Emitter, float, float) SelectLight(float primary) {
            float scaled = scene.Emitters.Count * primary;
            int idx = Math.Clamp((int)scaled, 0, scene.Emitters.Count - 1);
            var emitter = scene.Emitters[idx];
            return (emitter, 1.0f / scene.Emitters.Count, scaled - idx);
        }

        public virtual float SelectLightPmf(Emitter em) {
            return 1.0f / scene.Emitters.Count;
        }

        /// <summary>
        /// Resets the path cache and populates it with a new set of light paths.
        /// </summary>
        /// <param name="iter">Index of the current iteration, used to seed the random number generator.</param>
        public void TraceAllPaths(uint iter) {
            if (pathCache == null)
                pathCache = new PathCache(MaxDepth * NumPaths);
            else
                pathCache.Clear();

            endpoints = new int[NumPaths];

            Parallel.For(0, NumPaths, idx => {
                var seed = RNG.HashSeed(BaseSeed, (uint)idx, iter);
                var rng = new RNG(seed);
                endpoints[idx] = TraceLightPath(rng, (uint)idx);
            });
        }

        /// <summary>
        /// Called for each light path, used to populate the path cache.
        /// </summary>
        /// <returns>
        /// The index of the last vertex along the path.
        /// </returns>
        public virtual int TraceLightPath(RNG rng, uint pathIndex) {
            // Select an emitter
            float lightSelPrimary = rng.NextFloat();
            var (emitter, selectProb, _) = SelectLight(lightSelPrimary);

            // Sample a ray from the emitter
            var primaryPos = rng.NextFloat2D();
            var primaryDir = rng.NextFloat2D();
            var emitterSample = emitter.SampleRay(primaryPos, primaryDir); ;

            // Account for the light selection probability also in the MIS weights
            emitterSample.pdf *= selectProb;

            // TODO refactor: to reduce risk of mistakes, don't pass an emitter sample to "StartFromEmitter",
            //      pass the pdf, weight, and surface point directly instead.

            // Perform a random walk through the scene, storing all vertices along the path
            var walker = new CachedRandomWalk(scene, rng, MaxDepth, pathCache);
            walker.StartFromEmitter(emitterSample, emitterSample.weight / selectProb);

            return walker.lastId;
        }

        public delegate void ProcessVertex(PathVertex vertex, PathVertex ancestor, Vector3 dirToAncestor);

        /// <summary>
        /// Utility function that iterates over a light path, starting on the end point, excluding the point on the light itself.
        /// </summary>
        public void ForEachVertex(int endpoint, ProcessVertex func) {
            if (endpoint < 0) return;

            int vertexId = endpoint;
            while (pathCache[vertexId].ancestorId != -1) { // iterate over all vertices that have an ancestor
                var vertex = pathCache[vertexId];
                var ancestor = pathCache[vertex.ancestorId];
                var dirToAncestor = ancestor.point.position - vertex.point.position;

                func(vertex, ancestor, dirToAncestor);

                vertexId = vertex.ancestorId;
            }
        }
    }
}
