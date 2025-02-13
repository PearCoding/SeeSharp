﻿using SeeSharp.Geometry;
using SeeSharp.Sampling;
using SimpleImageIO;
using SeeSharp.Shading.Emitters;
using SeeSharp.Integrators.Common;
using System.IO;
using System.Numerics;
using TinyEmbree;
using System;

namespace SeeSharp.Integrators.Bidir {
    /// <summary>
    /// Variation of the bidirectional path tracer that uses the "Light vertex cache" proposed
    /// by Davidovic et al [2014] "Progressive Light Transport Simulation on the GPU: Survey and Improvements".
    /// </summary>
    public class VertexCacheBidir : BidirBase {
        public int NumConnections = 5;
        public int NumShadowRays = 1;

        public bool EnableLightTracer = true;
        public bool EnableHitting = true;
        public bool EnableConnections = true;

        public bool RenderTechniquePyramid = false;

        TechPyramid techPyramidRaw;
        TechPyramid techPyramidWeighted;

        protected VertexSelector vertexSelector;

        protected override float NextEventPdf(SurfacePoint from, SurfacePoint to) {
            return base.NextEventPdf(from, to) * NumShadowRays;
        }

        protected override (Emitter, SurfaceSample) SampleNextEvent(SurfacePoint from, RNG rng) {
            var (light, sample) = base.SampleNextEvent(from, rng);
            sample.Pdf *= NumShadowRays;
            return (light, sample);
        }

        protected override (int, int, float) SelectBidirPath(SurfacePoint cameraPoint, Vector3 outDir,
                                                             int pixelIndex, RNG rng) {
            // Select a single vertex from the entire cache at random
            var (path, vertex) = vertexSelector.Select(rng);
            return (path, vertex, BidirSelectDensity());
        }

        /// <summary>
        /// Computes the effective density of selecting a light path vertex for connection.
        /// That is, the product of the selection probability and the number of samples.
        /// </summary>
        /// <returns>Effective density</returns>
        public virtual float BidirSelectDensity() {
            if (vertexSelector.Count == 0) return 0;

            // We select light path vertices uniformly
            float selectProb = 1.0f / vertexSelector.Count;

            // There are "NumLightPaths" samples that could have generated the selected vertex,
            // we repeat the process "NumConnections" times
            float numSamples = NumConnections * NumLightPaths.Value;

            return selectProb * numSamples;
        }

        protected override void RegisterSample(RgbColor weight, float misWeight, Vector2 pixel,
                                               int cameraPathLength, int lightPathLength, int fullLength) {
            if (!RenderTechniquePyramid)
                return;

            // Technique pyramids are rendered across all iterations
            weight /= NumIterations;

            techPyramidRaw.Add(cameraPathLength, lightPathLength, fullLength, pixel, weight);
            techPyramidWeighted.Add(cameraPathLength, lightPathLength, fullLength, pixel, weight * misWeight);
        }

        public override void Render(Scene scene) {
            if (RenderTechniquePyramid) {
                techPyramidRaw = new TechPyramid(scene.FrameBuffer.Width, scene.FrameBuffer.Height,
                                                 minDepth: 1, maxDepth: MaxDepth, merges: false);
                techPyramidWeighted = new TechPyramid(scene.FrameBuffer.Width, scene.FrameBuffer.Height,
                                                      minDepth: 1, maxDepth: MaxDepth, merges: false);
            }

            base.Render(scene);

            if (RenderTechniquePyramid) {
                techPyramidRaw.WriteToFiles(Path.Join(scene.FrameBuffer.Basename, "techs-raw"));
                techPyramidWeighted.WriteToFiles(Path.Join(scene.FrameBuffer.Basename, "techs-weighted"));
            }
        }

        protected override void ProcessPathCache() {
            if (EnableConnections) vertexSelector = new VertexSelector(LightPaths.PathCache);
            if (EnableLightTracer) SplatLightVertices();
        }

        protected override RgbColor OnCameraHit(CameraPath path, RNG rng, Ray ray, SurfacePoint hit,
                                                float pdfFromAncestor, RgbColor throughput, int depth,
                                                float toAncestorJacobian) {
            RgbColor value = RgbColor.Black;

            // Was a light hit?
            Emitter light = Scene.QueryEmitter(hit);
            if (light != null && EnableHitting && depth >= MinDepth) {
                value += throughput * OnEmitterHit(light, hit, ray, path, toAncestorJacobian);
            }

            // Perform connections if the maximum depth has not yet been reached
            if (depth < MaxDepth) {
                for (int i = 0; i < NumConnections && EnableConnections; ++i) {
                    value += throughput * BidirConnections(hit, -ray.Direction, rng, path, toAncestorJacobian);
                }
            }

            if (depth < MaxDepth && depth + 1 >= MinDepth) {
                for (int i = 0; i < NumShadowRays; ++i) {
                    value += throughput * PerformNextEventEstimation(ray, hit, rng, path, toAncestorJacobian);
                }
            }

            return value;
        }

        public override float EmitterHitMis(CameraPath cameraPath, float pdfEmit, float pdfNextEvent) {
            int numPdfs = cameraPath.Vertices.Count;
            int lastCameraVertexIdx = numPdfs - 1;
            Span<float> camToLight = stackalloc float[numPdfs];
            Span<float> lightToCam = stackalloc float[numPdfs];

            if (numPdfs == 1) return 1.0f; // sole technique for rendering directly visible lights.

            var pathPdfs = new BidirPathPdfs(LightPaths.PathCache, lightToCam, camToLight);
            pathPdfs.GatherCameraPdfs(cameraPath, lastCameraVertexIdx);

            pathPdfs.PdfsLightToCamera[^2] = pdfEmit;

            float pdfThis = cameraPath.Vertices[^1].PdfFromAncestor;

            // Compute the actual weight
            float sumReciprocals = 1.0f;

            // Next event estimation
            sumReciprocals += pdfNextEvent / pdfThis;

            // All connections along the camera path
            sumReciprocals += CameraPathReciprocals(lastCameraVertexIdx - 1, pathPdfs) / pdfThis;

            return 1 / sumReciprocals;
        }

        public override float LightTracerMis(PathVertex lightVertex, float pdfCamToPrimary, float pdfReverse,
                                             float pdfNextEvent, Vector2 pixel, float distToCam) {
            int numPdfs = lightVertex.Depth + 1;
            int lastCameraVertexIdx = -1;
            Span<float> camToLight = stackalloc float[numPdfs];
            Span<float> lightToCam = stackalloc float[numPdfs];

            var pathPdfs = new BidirPathPdfs(LightPaths.PathCache, lightToCam, camToLight);

            pathPdfs.GatherLightPdfs(lightVertex, lastCameraVertexIdx);

            pathPdfs.PdfsCameraToLight[0] = pdfCamToPrimary;
            pathPdfs.PdfsCameraToLight[1] = pdfReverse + pdfNextEvent;

            // Compute the actual weight
            float sumReciprocals = LightPathReciprocals(lastCameraVertexIdx, numPdfs, pathPdfs);
            sumReciprocals /= NumLightPaths.Value;
            sumReciprocals += 1;
            return 1 / sumReciprocals;
        }

        public override float BidirConnectMis(CameraPath cameraPath, PathVertex lightVertex,
                                              float pdfCameraReverse, float pdfCameraToLight,
                                              float pdfLightReverse, float pdfLightToCamera,
                                              float pdfNextEvent) {
            int numPdfs = cameraPath.Vertices.Count + lightVertex.Depth + 1;
            int lastCameraVertexIdx = cameraPath.Vertices.Count - 1;
            Span<float> camToLight = stackalloc float[numPdfs];
            Span<float> lightToCam = stackalloc float[numPdfs];

            var pathPdfs = new BidirPathPdfs(LightPaths.PathCache, lightToCam, camToLight);
            pathPdfs.GatherCameraPdfs(cameraPath, lastCameraVertexIdx);
            pathPdfs.GatherLightPdfs(lightVertex, lastCameraVertexIdx);

            // Set the pdf values that are unique to this combination of paths
            if (lastCameraVertexIdx > 0) // only if this is not the primary hit point
                pathPdfs.PdfsLightToCamera[lastCameraVertexIdx - 1] = pdfCameraReverse;
            pathPdfs.PdfsCameraToLight[lastCameraVertexIdx] = cameraPath.Vertices[^1].PdfFromAncestor;
            pathPdfs.PdfsLightToCamera[lastCameraVertexIdx] = pdfLightToCamera;
            pathPdfs.PdfsCameraToLight[lastCameraVertexIdx + 1] = pdfCameraToLight;
            pathPdfs.PdfsCameraToLight[lastCameraVertexIdx + 2] = pdfLightReverse + pdfNextEvent;

            // Compute reciprocals for hypothetical connections along the camera sub-path
            float sumReciprocals = 1.0f;
            sumReciprocals += CameraPathReciprocals(lastCameraVertexIdx, pathPdfs) / BidirSelectDensity();
            sumReciprocals += LightPathReciprocals(lastCameraVertexIdx, numPdfs, pathPdfs) / BidirSelectDensity();

            return 1 / sumReciprocals;
        }

        public override float NextEventMis(CameraPath cameraPath, float pdfEmit, float pdfNextEvent,
                                           float pdfHit, float pdfReverse) {
            int numPdfs = cameraPath.Vertices.Count + 1;
            int lastCameraVertexIdx = numPdfs - 2;
            Span<float> camToLight = stackalloc float[numPdfs];
            Span<float> lightToCam = stackalloc float[numPdfs];

            var pathPdfs = new BidirPathPdfs(LightPaths.PathCache, lightToCam, camToLight);

            pathPdfs.GatherCameraPdfs(cameraPath, lastCameraVertexIdx);

            pathPdfs.PdfsCameraToLight[^2] = cameraPath.Vertices[^1].PdfFromAncestor;
            pathPdfs.PdfsLightToCamera[^2] = pdfEmit;
            if (numPdfs > 2) // not for direct illumination
                pathPdfs.PdfsLightToCamera[^3] = pdfReverse;

            // Compute the actual weight
            float sumReciprocals = 1.0f;

            // Hitting the light source
            if (EnableHitting) sumReciprocals += pdfHit / pdfNextEvent;

            // All bidirectional connections
            sumReciprocals += CameraPathReciprocals(lastCameraVertexIdx, pathPdfs) / pdfNextEvent;

            return 1 / sumReciprocals;
        }

        protected virtual float CameraPathReciprocals(int lastCameraVertexIdx, BidirPathPdfs pdfs) {
            float sumReciprocals = 0.0f;
            float nextReciprocal = 1.0f;
            for (int i = lastCameraVertexIdx; i > 0; --i) { // all bidir connections
                nextReciprocal *= pdfs.PdfsLightToCamera[i] / pdfs.PdfsCameraToLight[i];
                if (EnableConnections)
                    sumReciprocals += nextReciprocal * BidirSelectDensity();
            }
            if (EnableLightTracer)
                sumReciprocals +=
                    nextReciprocal * pdfs.PdfsLightToCamera[0] / pdfs.PdfsCameraToLight[0] * NumLightPaths.Value;
            return sumReciprocals;
        }

        protected virtual float LightPathReciprocals(int lastCameraVertexIdx, int numPdfs, BidirPathPdfs pdfs) {
            float sumReciprocals = 0.0f;
            float nextReciprocal = 1.0f;
            for (int i = lastCameraVertexIdx + 1; i < numPdfs; ++i) {
                nextReciprocal *= pdfs.PdfsCameraToLight[i] / pdfs.PdfsLightToCamera[i];
                if (i < numPdfs - 2 && EnableConnections) // Connections to the emitter (next event) are treated separately
                    sumReciprocals += nextReciprocal * BidirSelectDensity();
            }
            sumReciprocals += nextReciprocal; // Next event and hitting the emitter directly
            return sumReciprocals;
        }
    }
}
