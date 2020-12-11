﻿using SeeSharp.Core.Geometry;
using SeeSharp.Core.Shading.Bsdfs;
using SeeSharp.Core.Shading.MicrofacetDistributions;
using SeeSharp.Core.Image;
using System;
using System.Diagnostics;
using System.Numerics;

namespace SeeSharp.Core.Shading.Materials {
    /// <summary>
    /// Basic uber-shader surface material that should suffice for most integrator experiments.
    /// </summary>
    public class GenericMaterial : Material {
        public class Parameters {
            public Image<ColorRGB> baseColor = Image<ColorRGB>.Constant(ColorRGB.White);
            public Image<Scalar> roughness = Image<Scalar>.Constant(0.5f);
            public float metallic = 0.0f;
            public float specularTintStrength = 1.0f;
            public float anisotropic = 0.0f;
            public float specularTransmittance = 0.0f;
            public float indexOfRefraction = 1.45f;
            public bool thin = false;
            public float diffuseTransmittance = 0.0f;
        }

        public GenericMaterial(Parameters parameters) => this.parameters = parameters;

        public override float GetRoughness(SurfacePoint hit)
        => parameters.roughness.TextureLookup(hit.TextureCoordinates).Value;

        public override ColorRGB GetScatterStrength(SurfacePoint hit)
        => parameters.baseColor.TextureLookup(hit.TextureCoordinates);

        public override ColorRGB Evaluate(SurfacePoint hit, Vector3 outDir, Vector3 inDir, bool isOnLightSubpath) {
            // Transform directions to shading space and normalize
            var normal = hit.ShadingNormal;
            outDir = ShadingSpace.WorldToShading(normal, outDir);
            inDir = ShadingSpace.WorldToShading(normal, inDir);

            var local = ComputeLocalParams(hit);

            // Evaluate all components
            var result = ColorRGB.Black;
            if (local.diffuseWeight > 0) {
                result += new DisneyDiffuse(local.diffuseReflectance).Evaluate(outDir, inDir, isOnLightSubpath);
                result += new DisneyRetroReflection(local.retroReflectance, local.roughness)
                    .Evaluate(outDir, inDir, isOnLightSubpath);
            }
            if (parameters.specularTransmittance > 0) {
                result += new MicrofacetTransmission(local.specularTransmittance,
                    local.transmissionDistribution, 1, parameters.indexOfRefraction)
                    .Evaluate(outDir, inDir, isOnLightSubpath);
            }
            if (parameters.thin) {
                result += new DiffuseTransmission(local.baseColor * parameters.diffuseTransmittance)
                    .Evaluate(outDir, inDir, isOnLightSubpath);
            }
            result += new MicrofacetReflection(local.microfacetDistrib, local.fresnel, local.specularTint)
                .Evaluate(outDir, inDir, isOnLightSubpath);

            Debug.Assert(float.IsFinite(result.Average));

            return result;
        }

        public override BsdfSample Sample(SurfacePoint hit, Vector3 outDir, bool isOnLightSubpath, Vector2 primarySample) {
            // Transform directions to shading space and normalize
            var normal = hit.ShadingNormal;
            outDir = ShadingSpace.WorldToShading(normal, outDir);

            // Compute parameters
            var local = ComputeLocalParams(hit);
            var select = ComputeSelectWeights(local, outDir, outDir);

            // Select a component to sample from // TODO this can be done in a loop if the selection probs are in an array
            Vector3? sample = null;
            float offset = 0;
            if (primarySample.X < offset + select.DiffTrans) {
                var remapped = primarySample;
                remapped.X = Math.Min((primarySample.X - offset) / select.DiffTrans, 1);
                sample = new DiffuseTransmission(local.baseColor * parameters.diffuseTransmittance)
                    .Sample(outDir, isOnLightSubpath, remapped);
            }
            offset += select.DiffTrans;

            if (primarySample.X > offset && primarySample.X < offset + select.Retro) {
                var remapped = primarySample;
                remapped.X = Math.Min((primarySample.X - offset) / select.Retro, 1);
                sample = new DisneyRetroReflection(local.retroReflectance, local.roughness)
                    .Sample(outDir, isOnLightSubpath, remapped);
            }
            offset += select.Retro;

            if (primarySample.X > offset && primarySample.X < offset + select.Diff) {
                var remapped = primarySample;
                remapped.X = Math.Min((primarySample.X - offset) / select.Diff, 1);
                sample = new DisneyDiffuse(local.diffuseReflectance)
                    .Sample(outDir, isOnLightSubpath, remapped);
            }
            offset += select.Diff;

            if (primarySample.X > offset && primarySample.X < offset + select.Reflect) {
                var remapped = primarySample;
                remapped.X = Math.Min((primarySample.X - offset) / select.Reflect, 1);
                sample = new MicrofacetReflection(local.microfacetDistrib, local.fresnel, local.specularTint)
                    .Sample(outDir, isOnLightSubpath, remapped);
            }
            offset += select.Reflect;

            if (primarySample.X > offset && primarySample.X < offset + select.Trans) {
                var remapped = primarySample;
                remapped.X = Math.Min((primarySample.X - offset) / select.Trans, 1);
                sample = new MicrofacetTransmission(local.specularTransmittance, local.transmissionDistribution,
                    1, parameters.indexOfRefraction).Sample(outDir, isOnLightSubpath, remapped);
            }
            offset += select.Trans;

            // Terminate if no valid direction was sampled
            if (!sample.HasValue) return BsdfSample.Invalid;
            var sampledDir = ShadingSpace.ShadingToWorld(hit.ShadingNormal, sample.Value);

            // Evaluate all components
            var outWorld = ShadingSpace.ShadingToWorld(hit.ShadingNormal, outDir);
            var value = EvaluateWithCosine(hit, outWorld, sampledDir, isOnLightSubpath);

            // Compute all pdfs
            var (pdfFwd, pdfRev) = Pdf(hit, outWorld, sampledDir, isOnLightSubpath);
            if (pdfFwd == 0) return BsdfSample.Invalid;

            Debug.Assert(float.IsFinite(value.Average / pdfFwd));

            // Combine results with balance heuristic MIS
            return new BsdfSample {
                pdf = pdfFwd,
                pdfReverse = pdfRev,
                weight = value / pdfFwd,
                direction = sampledDir
            };
        }

        public override (float, float) Pdf(SurfacePoint hit, Vector3 outDir, Vector3 inDir, bool isOnLightSubpath) {
            // Transform directions to shading space and normalize
            var normal = hit.ShadingNormal;
            outDir = ShadingSpace.WorldToShading(normal, outDir);
            inDir = ShadingSpace.WorldToShading(normal, inDir);

            // Compute parameters
            var local = ComputeLocalParams(hit);
            var select = ComputeSelectWeights(local, outDir, inDir);

            // Compute the sum of all pdf values
            float pdfFwd = 0, pdfRev = 0;
            float fwd, rev;
            if (select.Diff > 0) {
                (fwd, rev) = new DisneyDiffuse(local.diffuseReflectance).Pdf(outDir, inDir, isOnLightSubpath);
                pdfFwd += fwd * select.Diff;
                pdfRev += rev * select.Diff;
            }
            if (select.Retro > 0) {
                (fwd, rev) = new DisneyRetroReflection(local.retroReflectance, local.roughness)
                    .Pdf(outDir, inDir, isOnLightSubpath);
                pdfFwd += fwd * select.Retro;
                pdfRev += rev * select.Retro;
            }
            if (select.Trans > 0) {
                (fwd, rev) = new MicrofacetTransmission(local.specularTransmittance,
                    local.transmissionDistribution, 1, parameters.indexOfRefraction)
                    .Pdf(outDir, inDir, isOnLightSubpath);
                pdfFwd += fwd * select.Trans;
                pdfRev += rev * select.TransRev;
            }
            if (select.DiffTrans > 0) {
                (fwd, rev) = new DiffuseTransmission(local.baseColor * parameters.diffuseTransmittance)
                    .Pdf(outDir, inDir, isOnLightSubpath);
                pdfFwd += fwd * select.DiffTrans;
                pdfRev += rev * select.DiffTrans;
            }
            if (select.Reflect > 0) {
                (fwd, rev) = new MicrofacetReflection(local.microfacetDistrib, local.fresnel, local.specularTint)
                    .Pdf(outDir, inDir, isOnLightSubpath);
                pdfFwd += fwd * select.Reflect;
                pdfRev += rev * select.ReflectRev;
            }

            return (pdfFwd, pdfRev);
        }

        struct LocalParams {
            public ColorRGB baseColor, colorTint, specularTint;
            public float roughness;
            public TrowbridgeReitzDistribution microfacetDistrib;
            public TrowbridgeReitzDistribution transmissionDistribution;
            public Fresnel fresnel;
            public float diffuseWeight;
            public ColorRGB diffuseReflectance;
            public ColorRGB retroReflectance;
            public ColorRGB specularTransmittance;
        }

        LocalParams ComputeLocalParams(SurfacePoint hit) {
            LocalParams result = new();

            (result.baseColor, result.colorTint, result.specularTint) = GetColorAndTints(hit);
            result.roughness = GetRoughness(hit);
            result.microfacetDistrib = CreateMicrofacetDistribution(result.roughness);
            result.transmissionDistribution = CreateTransmissionDistribution(result.roughness);
            result.fresnel = CreateFresnel(result.baseColor, result.specularTint);
            result.diffuseWeight = (1 - parameters.metallic) * (1 - parameters.specularTransmittance);
            result.diffuseReflectance = result.baseColor;
            if (parameters.thin)
                result.diffuseReflectance *= (1 - parameters.diffuseTransmittance);
            else
                result.diffuseReflectance *= result.diffuseWeight;
            result.retroReflectance = result.baseColor * result.diffuseWeight;
            result.specularTransmittance = parameters.specularTransmittance * ColorRGB.Sqrt(result.baseColor);

            return result;
        }

        (ColorRGB, ColorRGB, ColorRGB) GetColorAndTints(SurfacePoint hit) {
            // Isolate hue and saturation from the base color
            var baseColor = parameters.baseColor.TextureLookup(hit.TextureCoordinates);
            float luminance = baseColor.Luminance;
            var colorTint = luminance > 0 ? (baseColor / luminance) : ColorRGB.White;
            var specularTint = ColorRGB.Lerp(parameters.specularTintStrength, ColorRGB.White, colorTint);
            return (baseColor, colorTint, specularTint);
        }

        TrowbridgeReitzDistribution CreateMicrofacetDistribution(float roughness) {
            float aspect = MathF.Sqrt(1 - parameters.anisotropic * .9f);
            float ax = Math.Max(.001f, roughness * roughness / aspect);
            float ay = Math.Max(.001f, roughness * roughness * aspect);
            return new TrowbridgeReitzDistribution { AlphaX = ax, AlphaY = ay };
        }

        TrowbridgeReitzDistribution CreateTransmissionDistribution(float roughness) {
            if (parameters.thin) {
                // Scale roughness based on IOR (Burley 2015, Figure 15).
                float aspect = MathF.Sqrt(1 - parameters.anisotropic * .9f);
                float rscaled = (0.65f * parameters.indexOfRefraction - 0.35f) * roughness;
                float axT = Math.Max(.001f, rscaled * rscaled / aspect);
                float ayT = Math.Max(.001f, rscaled * rscaled * aspect);
                return new TrowbridgeReitzDistribution { AlphaX = axT, AlphaY = ayT };
            } else
                return CreateMicrofacetDistribution(roughness);
        }

        Fresnel CreateFresnel(ColorRGB baseColor, ColorRGB specularTint) {
            var specularReflectanceAtNormal = ColorRGB.Lerp(parameters.metallic,
                FresnelSchlick.SchlickR0FromEta(parameters.indexOfRefraction) * specularTint,
                baseColor);
            return new DisneyFresnel {
                IndexOfRefraction = parameters.indexOfRefraction,
                Metallic = parameters.metallic,
                ReflectanceAtNormal = specularReflectanceAtNormal
            };
        }

        struct SelectionWeights {
            public float DiffTrans;
            public float Retro;
            public float Diff;
            public float Reflect;
            public float Trans;
            public float ReflectRev;
            public float TransRev;
        }

        SelectionWeights ComputeSelectWeights(LocalParams p, Vector3 outDir, Vector3 inDir) {
            SelectionWeights weights = new();

            if (parameters.thin) {
                weights.DiffTrans = p.diffuseWeight * parameters.diffuseTransmittance;
                weights.Retro = p.diffuseWeight * (1 - parameters.diffuseTransmittance) * 0.5f;
                weights.Diff = p.diffuseWeight * (1 - parameters.diffuseTransmittance) * 0.5f;
            } else {
                weights.Retro = p.diffuseWeight * 0.5f;
                weights.Diff = p.diffuseWeight * 0.5f;
            }

            // Evaluate the fresnel term for transmittance importance sampling (ignores microfacet orientation)
            float f = p.fresnel.Evaluate(ShadingSpace.CosTheta(outDir)).Luminance;
            float fRev = p.fresnel.Evaluate(ShadingSpace.CosTheta(inDir)).Luminance;

            weights.Reflect = (1 - p.diffuseWeight) * f;
            weights.Trans = (1 - p.diffuseWeight) * (1 - f);
            weights.ReflectRev = (1 - p.diffuseWeight) * fRev;
            weights.TransRev = (1 - p.diffuseWeight) * (1 - fRev);
            return weights;
        }

        Parameters parameters;
    }
}
