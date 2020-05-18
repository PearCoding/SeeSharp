﻿using SeeSharp.Core;
using SeeSharp.Core.Cameras;
using SeeSharp.Core.Geometry;
using SeeSharp.Core.Sampling;
using SeeSharp.Core.Shading;
using SeeSharp.Core.Shading.Emitters;
using SeeSharp.Integrators.Common;
using System.IO;
using System.Numerics;

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

        public bool RenderTechniquePyramid = true;
        public string TechniquePyramidPath = "vertex-cache";

        TechPyramid techPyramidRaw;
        TechPyramid techPyramidWeighted;

        VertexSelector vertexSelector;

        public override float NextEventPdf(SurfacePoint from, SurfacePoint to) {
            return base.NextEventPdf(from, to) * NumShadowRays;
        }

        public override (int, bool) SelectBidirPath(int pixelIndex, RNG rng) {
            // Select a single vertex from the entire cache at random
            return (vertexSelector.Select(rng), false);
        }

        /// <summary>
        /// Computes the effective density of selecting a light path vertex for connection.
        /// That is, the product of the selection probability and the number of samples.
        /// </summary>
        /// <returns>Effective density</returns>
        public float BidirSelectDensity() {
            // We select light path vertices uniformly
            float selectProb = 1.0f / vertexSelector.Count;

            // There are "NumLightPaths" samples that could have generated the selected vertex, 
            // we repeat the process "NumConnections" times
            float numSamples = NumConnections * NumLightPaths;

            return selectProb * numSamples;
        }

        public override void RegisterSample(ColorRGB weight, float misWeight, Vector2 pixel,
                                            int cameraPathLength, int lightPathLength, int fullLength) {
            if (!RenderTechniquePyramid)
                return;

            // Divide by the splitting factors and selection probabilities
            if (lightPathLength == 0 && fullLength != cameraPathLength) {
                // next event estimation
                weight /= NumShadowRays;
            } else if (cameraPathLength > 0 && lightPathLength > 0) {
                // bidirectional connection
                weight /= BidirSelectDensity();
            }

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
                techPyramidRaw.WriteToFiles(Path.Join(TechniquePyramidPath, "raw-"));
                techPyramidWeighted.WriteToFiles(Path.Join(TechniquePyramidPath, "weighted-"));
            }
        }

        public override void ProcessPathCache() {
            if (EnableLightTracer) 
                SplatLightVertices();
            vertexSelector = new VertexSelector(lightPaths.pathCache);
        }

        public override ColorRGB OnCameraHit(CameraPath path, RNG rng, int pixelIndex, Ray ray, SurfacePoint hit,
                                             float pdfFromAncestor, float pdfToAncestor, ColorRGB throughput,
                                             int depth, float toAncestorJacobian) {
            ColorRGB value = ColorRGB.Black;

            // Was a light hit?
            Emitter light = scene.QueryEmitter(hit);
            if (light != null && EnableHitting) {
                value += throughput * OnEmitterHit(light, hit, ray, path, toAncestorJacobian);
            }
            
            // Perform connections if the maximum depth has not yet been reached
            if (depth < MaxDepth) {
                for (int i = 0; i < NumConnections; ++i) {
                    var weight = throughput * BidirConnections(pixelIndex, hit, -ray.direction, rng, path, toAncestorJacobian);
                    value += weight / BidirSelectDensity();
                }

                for (int i = 0; i < NumShadowRays; ++i) {
                    var weight = throughput * PerformNextEventEstimation(ray, hit, rng, path, toAncestorJacobian);
                    value += weight / NumShadowRays;
                }
            }

            return value;
        }

        public override float EmitterHitMis(CameraPath cameraPath, float pdfEmit, float pdfNextEvent) {
            int numPdfs = cameraPath.vertices.Count;
            int lastCameraVertexIdx = numPdfs - 1;

            if (numPdfs == 1) return 1.0f; // sole technique for rendering directly visible lights.

            var pathPdfs = new BidirPathPdfs(lightPaths.pathCache, numPdfs);
            pathPdfs.GatherCameraPdfs(cameraPath, lastCameraVertexIdx);

            pathPdfs.pdfsLightToCamera[^2] = pdfEmit;

            float pdfThis = cameraPath.vertices[^1].pdfFromAncestor;

            // Compute the actual weight
            float sumReciprocals = 1.0f;

            // Next event estimation
            sumReciprocals += pdfNextEvent / pdfThis;

            // All connections along the camera path
            sumReciprocals += CameraPathReciprocals(lastCameraVertexIdx - 1, pathPdfs) / pdfThis;

            return 1 / sumReciprocals;
        }

        public override float LightTracerMis(PathVertex lightVertex, float pdfCamToPrimary, float pdfReverse) {
            int numPdfs = lightVertex.depth + 1;
            int lastCameraVertexIdx = -1;

            var pathPdfs = new BidirPathPdfs(lightPaths.pathCache, numPdfs);

            pathPdfs.GatherLightPdfs(lightVertex, lastCameraVertexIdx, numPdfs);

            pathPdfs.pdfsCameraToLight[0] = pdfCamToPrimary;
            pathPdfs.pdfsCameraToLight[1] = pdfReverse;

            // Compute the actual weight
            float sumReciprocals = LightPathReciprocals(lastCameraVertexIdx, numPdfs, pathPdfs);
            sumReciprocals /= NumLightPaths;
            sumReciprocals += 1;
            return 1 / sumReciprocals;
        }

        public override float BidirConnectMis(CameraPath cameraPath, PathVertex lightVertex, float pdfCameraReverse,
                                              float pdfCameraToLight, float pdfLightReverse, float pdfLightToCamera) {
            int numPdfs = cameraPath.vertices.Count + lightVertex.depth + 1;
            int lastCameraVertexIdx = cameraPath.vertices.Count - 1;

            var pathPdfs = new BidirPathPdfs(lightPaths.pathCache, numPdfs);
            pathPdfs.GatherCameraPdfs(cameraPath, lastCameraVertexIdx);
            pathPdfs.GatherLightPdfs(lightVertex, lastCameraVertexIdx, numPdfs);

            // Set the pdf values that are unique to this combination of paths
            if (lastCameraVertexIdx > 0) // only if this is not the primary hit point
                pathPdfs.pdfsLightToCamera[lastCameraVertexIdx - 1] = pdfCameraReverse;
            pathPdfs.pdfsCameraToLight[lastCameraVertexIdx] = cameraPath.vertices[^1].pdfFromAncestor;
            pathPdfs.pdfsLightToCamera[lastCameraVertexIdx] = pdfLightToCamera;
            pathPdfs.pdfsCameraToLight[lastCameraVertexIdx + 1] = pdfCameraToLight;
            pathPdfs.pdfsCameraToLight[lastCameraVertexIdx + 2] = pdfLightReverse;

            // Compute reciprocals for hypothetical connections along the camera sub-path
            float sumReciprocals = 1.0f;
            sumReciprocals += CameraPathReciprocals(lastCameraVertexIdx, pathPdfs) / BidirSelectDensity();
            sumReciprocals += LightPathReciprocals(lastCameraVertexIdx, numPdfs, pathPdfs) / BidirSelectDensity();

            return 1 / sumReciprocals;
        }

        public override float NextEventMis(CameraPath cameraPath, float pdfEmit, float pdfNextEvent, float pdfHit, float pdfReverse) {
            int numPdfs = cameraPath.vertices.Count + 1;
            int lastCameraVertexIdx = numPdfs - 2;

            var pathPdfs = new BidirPathPdfs(lightPaths.pathCache, numPdfs);

            pathPdfs.GatherCameraPdfs(cameraPath, lastCameraVertexIdx);

            pathPdfs.pdfsCameraToLight[^2] = cameraPath.vertices[^1].pdfFromAncestor;
            pathPdfs.pdfsLightToCamera[^2] = pdfEmit;
            if (numPdfs > 2) // not for direct illumination
                pathPdfs.pdfsLightToCamera[^3] = pdfReverse;

            // Compute the actual weight
            float sumReciprocals = 1.0f;

            // Hitting the light source
            if (EnableHitting) sumReciprocals += pdfHit / pdfNextEvent;

            // All bidirectional connections
            sumReciprocals += CameraPathReciprocals(lastCameraVertexIdx, pathPdfs) / pdfNextEvent;

            return 1 / sumReciprocals;
        }

        private float CameraPathReciprocals(int lastCameraVertexIdx, BidirPathPdfs pdfs) {
            float sumReciprocals = 0.0f;
            float nextReciprocal = 1.0f;
            for (int i = lastCameraVertexIdx; i > 0; --i) { // all bidir connections
                nextReciprocal *= pdfs.pdfsLightToCamera[i] / pdfs.pdfsCameraToLight[i];
                sumReciprocals += nextReciprocal * BidirSelectDensity();
            }
            if (EnableLightTracer)
                sumReciprocals += nextReciprocal * pdfs.pdfsLightToCamera[0] / pdfs.pdfsCameraToLight[0] * NumLightPaths;
            return sumReciprocals;
        }

        private float LightPathReciprocals(int lastCameraVertexIdx, int numPdfs, BidirPathPdfs pdfs) {
            float sumReciprocals = 0.0f;
            float nextReciprocal = 1.0f;
            for (int i = lastCameraVertexIdx + 1; i < numPdfs; ++i) {
                nextReciprocal *= pdfs.pdfsCameraToLight[i] / pdfs.pdfsLightToCamera[i];
                if (i < numPdfs - 2) // Connections to the emitter (next event) are treated separately
                    sumReciprocals += nextReciprocal * BidirSelectDensity();
            }
            sumReciprocals += nextReciprocal; // Next event and hitting the emitter directly
            return sumReciprocals;
        }
    }
}
