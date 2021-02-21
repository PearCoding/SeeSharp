﻿using SeeSharp.Geometry;
using SeeSharp.Sampling;
using SimpleImageIO;
using SeeSharp.Shading.Emitters;
using SeeSharp.Integrators.Common;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using TinyEmbree;
using System.Diagnostics;

namespace SeeSharp.Integrators.Bidir {
    public abstract class BidirBase : Integrator {
        public int NumIterations = 2;
        public int NumLightPaths = 0;
        public int MaxDepth = 10;
        public int MinDepth = 1;

        public uint BaseSeedCamera = 0xC030114u;
        public uint BaseSeedLight = 0x13C0FEFEu;

        public Util.PathLogger PathLogger;

        public Scene scene;
        public LightPathCache lightPaths;

        public struct PathPdfPair {
            public float PdfFromAncestor;
            public float PdfToAncestor;
        }

        public struct CameraPath {
            /// <summary>
            /// The pixel position where the path was started.
            /// </summary>
            public Vector2 Pixel;

            /// <summary>
            /// The product of the local estimators along the path (BSDF * cos / pdf)
            /// </summary>
            public RgbColor Throughput;

            /// <summary>
            /// The pdf values for sampling this path.
            /// </summary>
            public List<PathPdfPair> Vertices;

            public List<float> Distances;
        }

        /// <summary>
        /// Called once per iteration after the light paths have been traced.
        /// Use this to create acceleration structures etc.
        /// </summary>
        public abstract void ProcessPathCache();

        public virtual (Emitter, float, float) SelectLight(float primary) {
            float scaled = scene.Emitters.Count * primary;
            int idx = Math.Clamp((int)scaled, 0, scene.Emitters.Count - 1);
            var emitter = scene.Emitters[idx];
            return (emitter, 1.0f / scene.Emitters.Count, scaled - idx);
        }

        public virtual float SelectLightPmf(Emitter em) {
            return 1.0f / scene.Emitters.Count;
        }

        public virtual (Emitter, float, float) SelectLight(SurfacePoint from, float primary)
        => SelectLight(primary);

        public virtual float SelectLightPmf(SurfacePoint from, Emitter em)
        => SelectLightPmf(em);

        /// <summary>
        /// Called once for each pixel per iteration. Expected to perform some sort of path tracing,
        /// possibly connecting vertices with those from the light path cache.
        /// </summary>
        /// <returns>The estimated pixel value.</returns>
        public virtual RgbColor EstimatePixelValue(SurfacePoint cameraPoint, Vector2 filmPosition, Ray primaryRay,
                                                   float pdfFromCamera, RgbColor initialWeight, RNG rng) {
            // The pixel index determines which light path we connect to
            int row = Math.Min((int)filmPosition.Y, scene.FrameBuffer.Height - 1);
            int col = Math.Min((int)filmPosition.X, scene.FrameBuffer.Width - 1);
            int pixelIndex = row * scene.FrameBuffer.Width + col;
            var walk = new CameraRandomWalk(rng, filmPosition, pixelIndex, this);
            return walk.StartFromCamera(filmPosition, cameraPoint, pdfFromCamera, primaryRay, initialWeight);
        }

        public virtual void PostIteration(uint iteration) { }
        public virtual void PreIteration(uint iteration) { }

        protected virtual LightPathCache MakeLightPathCache()
        => new LightPathCache { MaxDepth = MaxDepth, NumPaths = NumLightPaths, Scene = scene };

        public override void Render(Scene scene) {
            this.scene = scene;

            if (NumLightPaths <= 0) {
                NumLightPaths = scene.FrameBuffer.Width * scene.FrameBuffer.Height;
            }

            lightPaths = MakeLightPathCache();

            SeeSharp.Common.ProgressBar progressBar = new(NumIterations);
            for (uint iter = 0; iter < NumIterations; ++iter) {
                var stop = Stopwatch.StartNew();
                scene.FrameBuffer.StartIteration();
                PreIteration(iter);

                try {
                    lightPaths.TraceAllPaths(iter,
                        (origin, primary, nextDirection) => NextEventPdf(primary.Point, origin.Point));
                    ProcessPathCache();
                    TraceAllCameraPaths(iter);
                } catch {
                    Console.WriteLine($"Exception in iteration {iter} out of {NumIterations}!");
                    throw;
                }

                scene.FrameBuffer.EndIteration();
                PostIteration(iter);
                progressBar.ReportDone(1, stop.Elapsed.TotalSeconds);
            }
        }

        private void TraceAllCameraPaths(uint iter) {
            Parallel.For(0, scene.FrameBuffer.Height,
                row => {
                    for (uint col = 0; col < scene.FrameBuffer.Width; ++col) {
                        uint pixelIndex = (uint)(row * scene.FrameBuffer.Width + col);
                        var seed = RNG.HashSeed(BaseSeedCamera, pixelIndex, (uint)iter);
                        var rng = new RNG(seed);
                        RenderPixel((uint)row, col, rng);
                    }
                }
            );
        }

        private void RenderPixel(uint row, uint col, RNG rng) {
            // Sample a ray from the camera
            var offset = rng.NextFloat2D();
            var filmSample = new Vector2(col, row) + offset;
            var cameraRay = scene.Camera.GenerateRay(filmSample, rng);
            var value = EstimatePixelValue(cameraRay.Point, filmSample, cameraRay.Ray,
                                           cameraRay.PdfRay, cameraRay.Weight, rng);

            // TODO we do nearest neighbor splatting manually here, to avoid numerical
            //      issues if the primary samples are almost 1 (400 + 0.99999999f = 401)
            scene.FrameBuffer.Splat((float)col, (float)row, value);
        }

        /// <summary>
        /// Called for each sample that has a non-zero contribution to the image.
        /// This can be used to write out pyramids of sampling technique images / MIS weights.
        /// The default implementation does nothing.
        /// </summary>
        /// <param name="cameraPathLength">Number of edges in the camera sub-path (0 if light tracer).</param>
        /// <param name="lightPathLength">Number of edges in the light sub-path (0 when hitting the light).</param>
        /// <param name="fullLength">Number of edges forming the full path. Used to disambiguate techniques.</param>
        public virtual void RegisterSample(RgbColor weight, float misWeight, Vector2 pixel,
                                           int cameraPathLength, int lightPathLength, int fullLength) {}
        public virtual void LightTracerUpdate(RgbColor weight, float misWeight, Vector2 pixel,
                                              PathVertex lightVertex, float pdfCamToPrimary, float pdfReverse,
                                              float pdfNextEvent, float distToCam) {}
        public virtual void NextEventUpdate(RgbColor weight, float misWeight, CameraPath cameraPath,
                                            float pdfEmit, float pdfNextEvent, float pdfHit, float pdfReverse,
                                            Emitter emitter, Vector3 lightToSurface, SurfacePoint lightPoint) {}
        public virtual void EmitterHitUpdate(RgbColor weight, float misWeight, CameraPath cameraPath,
                                             float pdfEmit, float pdfNextEvent, Emitter emitter,
                                             Vector3 lightToSurface, SurfacePoint lightPoint) {}
        public virtual void BidirConnectUpdate(RgbColor weight, float misWeight, CameraPath cameraPath,
                                               PathVertex lightVertex, float pdfCameraReverse,
                                               float pdfCameraToLight, float pdfLightReverse,
                                               float pdfLightToCamera, float pdfNextEvent) {}


        public abstract float LightTracerMis(PathVertex lightVertex, float pdfCamToPrimary, float pdfReverse,
                                             float pdfNextEvent, Vector2 pixel, float distToCam);

        public void SplatLightVertices() {
            Parallel.For(0, lightPaths.NumPaths, idx => {
                ConnectLightPathToCamera(idx);
            });
        }

        public virtual void ConnectLightPathToCamera(int pathIdx) {
            lightPaths.ForEachVertex(pathIdx, (vertex, ancestor, dirToAncestor) => {
                if (vertex.Depth + 1 < MinDepth) return;

                // Compute image plane location
                var raster = scene.Camera.WorldToFilm(vertex.Point.Position);
                if (!raster.HasValue)
                    return;
                var pixel = new Vector2(raster.Value.X, raster.Value.Y);

                // Perform a change of variables from scene surface to pixel area.
                // TODO this could be computed by the camera itself...
                // First: map the scene surface to the solid angle about the camera
                var dirToCam = scene.Camera.Position - vertex.Point.Position;
                float distToCam = dirToCam.Length();
                float cosToCam = Math.Abs(Vector3.Dot(vertex.Point.Normal, dirToCam)) / distToCam;
                float surfaceToSolidAngle = cosToCam / (distToCam * distToCam);

                if (distToCam == 0 || cosToCam == 0)
                    return;

                // Second: map the solid angle to the pixel area
                float solidAngleToPixel = scene.Camera.SolidAngleToPixelJacobian(vertex.Point.Position);

                // Third: combine to get the full jacobian
                float surfaceToPixelJacobian = surfaceToSolidAngle * solidAngleToPixel;

                // Trace shadow ray
                if (scene.Raytracer.IsOccluded(vertex.Point, scene.Camera.Position))
                    return;

                var bsdfValue = vertex.Point.Material.Evaluate(vertex.Point, dirToAncestor, dirToCam, true);
                if (bsdfValue == RgbColor.Black)
                    return;

                // Compute the surface area pdf of sampling the previous vertex instead
                float pdfReverse =
                    vertex.Point.Material.Pdf(vertex.Point, dirToCam, dirToAncestor, false).Item1;
                if (ancestor.Point.Mesh != null)
                    pdfReverse *= SampleWarp.SurfaceAreaToSolidAngle(vertex.Point, ancestor.Point);

                // Account for next event estimation
                float pdfNextEvent = 0.0f;
                if (vertex.Depth == 1) {
                    pdfNextEvent = NextEventPdf(vertex.Point, ancestor.Point);
                }

                float misWeight =
                    LightTracerMis(vertex, surfaceToPixelJacobian, pdfReverse, pdfNextEvent, pixel, distToCam);

                // Compute image contribution and splat
                RgbColor weight = vertex.Weight * bsdfValue * surfaceToPixelJacobian / NumLightPaths;

                scene.FrameBuffer.Splat(pixel.X, pixel.Y, misWeight * weight);
                RegisterSample(weight, misWeight, pixel, 0, vertex.Depth, vertex.Depth + 1);
                LightTracerUpdate(weight, misWeight, pixel, vertex, surfaceToPixelJacobian, pdfReverse,
                    pdfNextEvent, distToCam);

                if (PathLogger != null && misWeight != 0 && weight != RgbColor.Black) {
                    var logId = PathLogger.StartNew(pixel);
                    for (int i = 0; i < vertex.Depth + 1; ++i) {
                        PathLogger.Continue(logId, lightPaths.PathCache[pathIdx, i].Point.Position, 1);
                    }
                    PathLogger.SetContrib(logId, misWeight * weight);
                }
            });
        }

        public abstract float BidirConnectMis(CameraPath cameraPath, PathVertex lightVertex,
                                              float pdfCameraReverse, float pdfCameraToLight,
                                              float pdfLightReverse, float pdfLightToCamera,
                                              float pdfNextEvent);

        public virtual (int, int, float) SelectBidirPath(SurfacePoint cameraPoint, Vector3 outDir,
                                                         int pixelIndex, RNG rng)
        => (pixelIndex, -1, 1.0f);

        public virtual RgbColor BidirConnections(int pixelIndex, SurfacePoint cameraPoint, Vector3 outDir,
                                                 RNG rng, CameraPath path, float reversePdfJacobian) {
            RgbColor result = RgbColor.Black;

            // Select a path to connect to (based on pixel index)
            (int lightPathIdx, int lightVertIdx, float lightVertexProb) =
                SelectBidirPath(cameraPoint, outDir, pixelIndex, rng);

            void Connect(PathVertex vertex, PathVertex ancestor, Vector3 dirToAncestor) {
                // Only allow connections that do not exceed the maximum total path length
                int depth = vertex.Depth + path.Vertices.Count + 1;
                if (depth > MaxDepth || depth < MinDepth) return;

                // Trace shadow ray
                if (scene.Raytracer.IsOccluded(vertex.Point, cameraPoint))
                    return;

                // Compute connection direction
                var dirFromCamToLight = vertex.Point.Position - cameraPoint.Position;

                var bsdfWeightLight = vertex.Point.Material.EvaluateWithCosine(vertex.Point, dirToAncestor,
                    -dirFromCamToLight, true);
                var bsdfWeightCam = cameraPoint.Material.EvaluateWithCosine(cameraPoint, outDir,
                    dirFromCamToLight, false);

                if (bsdfWeightCam == RgbColor.Black || bsdfWeightLight == RgbColor.Black)
                    return;

                // Compute the missing pdfs
                var (pdfCameraToLight, pdfCameraReverse) =
                    cameraPoint.Material.Pdf(cameraPoint, outDir, dirFromCamToLight, false);
                pdfCameraReverse *= reversePdfJacobian;
                pdfCameraToLight *= SampleWarp.SurfaceAreaToSolidAngle(cameraPoint, vertex.Point);

                var (pdfLightToCamera, pdfLightReverse) =
                    vertex.Point.Material.Pdf(vertex.Point, dirToAncestor, -dirFromCamToLight, true);
                if (ancestor.Point.Mesh != null) // not when from background
                    pdfLightReverse *= SampleWarp.SurfaceAreaToSolidAngle(vertex.Point, ancestor.Point);
                pdfLightToCamera *= SampleWarp.SurfaceAreaToSolidAngle(vertex.Point, cameraPoint);

                float pdfNextEvent = 0.0f;
                if (vertex.Depth == 1) {
                    pdfNextEvent = NextEventPdf(vertex.Point, ancestor.Point);
                }

                float misWeight = BidirConnectMis(path, vertex, pdfCameraReverse, pdfCameraToLight,
                    pdfLightReverse, pdfLightToCamera, pdfNextEvent);
                float distanceSqr = (cameraPoint.Position - vertex.Point.Position).LengthSquared();

                // Avoid NaNs in rare cases
                if (distanceSqr == 0)
                    return;

                RgbColor weight =
                    vertex.Weight * bsdfWeightLight * bsdfWeightCam / distanceSqr / lightVertexProb;
                result += misWeight * weight;

                RegisterSample(weight * path.Throughput, misWeight, path.Pixel,
                               path.Vertices.Count, vertex.Depth, depth);
                BidirConnectUpdate(weight * path.Throughput, misWeight, path, vertex, pdfCameraReverse,
                    pdfCameraToLight, pdfLightReverse, pdfLightToCamera, pdfNextEvent);
            }

            if (lightVertIdx > 0 && lightVertIdx < lightPaths.PathCache.Length(lightPathIdx)) {
                // specific vertex selected
                var vertex = lightPaths.PathCache[lightPathIdx, lightVertIdx];
                var ancestor = lightPaths.PathCache[lightPathIdx, lightVertIdx - 1];
                var dirToAncestor = ancestor.Point.Position - vertex.Point.Position;
                Connect(vertex, ancestor, dirToAncestor);
            } else if (lightPathIdx >= 0) {
                // Connect with all vertices along the path
                lightPaths.ForEachVertex(lightPathIdx, Connect);
            }

            return result;
        }

        public abstract float NextEventMis(CameraPath cameraPath, float pdfEmit, float pdfNextEvent,
                                           float pdfHit, float pdfReverse);

        public virtual float NextEventPdf(SurfacePoint from, SurfacePoint to) {
            float backgroundProbability = ComputeNextEventBackgroundProbability(/*hit*/);
            if (to.Mesh == null) { // Background
                var direction = to.Position - from.Position;
                return scene.Background.DirectionPdf(direction) * backgroundProbability;
            } else { // Emissive object
                var emitter = scene.QueryEmitter(to);
                return emitter.PdfArea(to) * SelectLightPmf(from, emitter) * (1 - backgroundProbability);
            }
        }

        public virtual (Emitter, SurfaceSample) SampleNextEvent(SurfacePoint from, RNG rng) {
            var (light, lightProb, _) = SelectLight(from, rng.NextFloat());
            var lightSample = light.SampleArea(rng.NextFloat2D());
            lightSample.Pdf *= lightProb;
            return (light, lightSample);
        }

        public virtual float ComputeNextEventBackgroundProbability(/*SurfacePoint from*/)
        => scene.Background == null ? 0 : 1 / (1.0f + scene.Emitters.Count);

        public virtual RgbColor PerformNextEventEstimation(Ray ray, SurfacePoint hit, RNG rng, CameraPath path,
                                                           float reversePdfJacobian) {
            float backgroundProbability = ComputeNextEventBackgroundProbability(/*hit*/);
            if (rng.NextFloat() < backgroundProbability) { // Connect to the background
                if (scene.Background == null)
                    return RgbColor.Black; // There is no background

                var sample = scene.Background.SampleDirection(rng.NextFloat2D());
                sample.Pdf *= backgroundProbability;
                sample.Weight /= backgroundProbability;

                if (sample.Pdf == 0) // Prevent NaN
                    return RgbColor.Black;

                if (scene.Raytracer.LeavesScene(hit, sample.Direction)) {
                    var bsdfTimesCosine = hit.Material.EvaluateWithCosine(hit, -ray.Direction,
                        sample.Direction, false);

                    // Compute the reverse BSDF sampling pdf
                    var (bsdfForwardPdf, bsdfReversePdf) = hit.Material.Pdf(hit, -ray.Direction,
                        sample.Direction, false);
                    bsdfReversePdf *= reversePdfJacobian;

                    // Compute emission pdf
                    float pdfEmit = lightPaths.ComputeBackgroundPdf(hit.Position, -sample.Direction);

                    // Compute the mis weight
                    float misWeight = NextEventMis(path, pdfEmit, sample.Pdf, bsdfForwardPdf, bsdfReversePdf);

                    // Compute and log the final sample weight
                    var weight = sample.Weight * bsdfTimesCosine;
                    RegisterSample(weight * path.Throughput, misWeight, path.Pixel,
                                   path.Vertices.Count, 0, path.Vertices.Count + 1);
                    NextEventUpdate(weight * path.Throughput, misWeight, path, pdfEmit, sample.Pdf,
                        bsdfForwardPdf, bsdfReversePdf, null, -sample.Direction,
                        new() { Position = hit.Position });
                    return misWeight * weight;
                }
            } else { // Connect to an emissive surface
                if (scene.Emitters.Count == 0)
                    return RgbColor.Black;

                // Sample a point on the light source
                var (light, lightSample) = SampleNextEvent(hit, rng);
                lightSample.Pdf *= (1 - backgroundProbability);

                if (lightSample.Pdf == 0) // Prevent NaN
                    return RgbColor.Black;

                if (!scene.Raytracer.IsOccluded(hit, lightSample.Point)) {
                    Vector3 lightToSurface = hit.Position - lightSample.Point.Position;
                    var emission = light.EmittedRadiance(lightSample.Point, lightToSurface);

                    var bsdfTimesCosine =
                        hit.Material.EvaluateWithCosine(hit, -ray.Direction, -lightToSurface, false);
                    if (bsdfTimesCosine == RgbColor.Black)
                        return RgbColor.Black;

                    // Compute the jacobian for surface area -> solid angle
                    // (Inverse of the jacobian for solid angle pdf -> surface area pdf)
                    float jacobian = SampleWarp.SurfaceAreaToSolidAngle(hit, lightSample.Point);
                    if (jacobian == 0) return RgbColor.Black;

                    // Compute the missing pdf terms
                    var (bsdfForwardPdf, bsdfReversePdf) =
                        hit.Material.Pdf(hit, -ray.Direction, -lightToSurface, false);
                    bsdfForwardPdf *= SampleWarp.SurfaceAreaToSolidAngle(hit, lightSample.Point);
                    bsdfReversePdf *= reversePdfJacobian;

                    float pdfEmit = lightPaths.ComputeEmitterPdf(light, lightSample.Point, lightToSurface,
                        SampleWarp.SurfaceAreaToSolidAngle(lightSample.Point, hit));

                    float misWeight =
                        NextEventMis(path, pdfEmit, lightSample.Pdf, bsdfForwardPdf, bsdfReversePdf);

                    var weight = emission * bsdfTimesCosine * (jacobian / lightSample.Pdf);
                    RegisterSample(weight * path.Throughput, misWeight, path.Pixel,
                                   path.Vertices.Count, 0, path.Vertices.Count + 1);
                    NextEventUpdate(weight * path.Throughput, misWeight, path, pdfEmit, lightSample.Pdf,
                        bsdfForwardPdf, bsdfReversePdf, light, lightToSurface, lightSample.Point);
                    return misWeight * weight;
                }
            }

            return RgbColor.Black;
        }

        public abstract float EmitterHitMis(CameraPath cameraPath, float pdfEmit, float pdfNextEvent);

        public virtual RgbColor OnEmitterHit(Emitter emitter, SurfacePoint hit, Ray ray,
                                             CameraPath path, float reversePdfJacobian) {
            var emission = emitter.EmittedRadiance(hit, -ray.Direction);

            // Compute pdf values
            float pdfEmit = lightPaths.ComputeEmitterPdf(emitter, hit, -ray.Direction, reversePdfJacobian);
            float pdfNextEvent = NextEventPdf(new SurfacePoint(), hit); // TODO get the actual previous point!

            float misWeight = EmitterHitMis(path, pdfEmit, pdfNextEvent);
            RegisterSample(emission * path.Throughput, misWeight, path.Pixel,
                           path.Vertices.Count, 0, path.Vertices.Count);
            EmitterHitUpdate(emission * path.Throughput, misWeight, path, pdfEmit, pdfNextEvent, emitter,
                -ray.Direction, hit);
            return misWeight * emission;
        }

        public virtual RgbColor OnBackgroundHit(Ray ray, CameraPath path) {
            if (scene.Background == null)
                return RgbColor.Black;

            // Compute the pdf of sampling the previous point by emission from the background
            float pdfEmit = lightPaths.ComputeBackgroundPdf(ray.Origin, -ray.Direction);

            // Compute the pdf of sampling the same connection via next event estimation
            float pdfNextEvent = scene.Background.DirectionPdf(ray.Direction);
            float backgroundProbability = ComputeNextEventBackgroundProbability(/*hit*/);
            pdfNextEvent *= backgroundProbability;

            float misWeight = EmitterHitMis(path, pdfEmit, pdfNextEvent);
            var emission = scene.Background.EmittedRadiance(ray.Direction);
            RegisterSample(emission * path.Throughput, misWeight, path.Pixel,
                           path.Vertices.Count, 0, path.Vertices.Count);
            EmitterHitUpdate(emission * path.Throughput, misWeight, path, pdfEmit, pdfNextEvent, null,
                -ray.Direction, new() { Position = ray.Origin });
            return misWeight * emission * path.Throughput;
        }

        public abstract RgbColor OnCameraHit(CameraPath path, RNG rng, int pixelIndex, Ray ray,
                                             SurfacePoint hit, float pdfFromAncestor, RgbColor throughput,
                                             int depth, float toAncestorJacobian);

        class CameraRandomWalk : RandomWalk {
            int pixelIndex;
            BidirBase integrator;
            CameraPath path;

            public CameraRandomWalk(RNG rng, Vector2 filmPosition, int pixelIndex, BidirBase integrator)
                : base(integrator.scene, rng, integrator.MaxDepth + 1) {
                this.pixelIndex = pixelIndex;
                this.integrator = integrator;
                path.Vertices = new List<PathPdfPair>(integrator.MaxDepth);
                path.Distances = new List<float>(integrator.MaxDepth);
                path.Pixel = filmPosition;
            }

            protected override RgbColor OnInvalidHit(Ray ray, float pdfFromAncestor, RgbColor throughput,
                                                     int depth) {
                path.Vertices.Add(new PathPdfPair {
                    PdfFromAncestor = pdfFromAncestor,
                    PdfToAncestor = 0
                });
                path.Throughput = throughput;
                path.Distances.Add(float.PositiveInfinity);
                return integrator.OnBackgroundHit(ray, path);
            }

            protected override RgbColor OnHit(Ray ray, SurfacePoint hit, float pdfFromAncestor,
                                              RgbColor throughput, int depth, float toAncestorJacobian) {
                path.Vertices.Add(new PathPdfPair {
                    PdfFromAncestor = pdfFromAncestor,
                    PdfToAncestor = 0
                });
                path.Throughput = throughput;
                path.Distances.Add(hit.Distance);
                return integrator.OnCameraHit(path, rng, pixelIndex, ray, hit, pdfFromAncestor, throughput,
                    depth, toAncestorJacobian);
            }

            protected override void OnContinue(float pdfToAncestor, int depth) {
                // Update the reverse pdf of the previous vertex.
                // TODO this currently assumes that no splitting is happening!
                var lastVert = path.Vertices[^1];
                path.Vertices[^1] = new PathPdfPair {
                    PdfFromAncestor = lastVert.PdfFromAncestor,
                    PdfToAncestor = pdfToAncestor
                };
            }
        }
    }
}
