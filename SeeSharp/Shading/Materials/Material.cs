﻿using System.Numerics;
using SeeSharp.Geometry;
using SeeSharp.Shading.Bsdfs;
using SimpleImageIO;

namespace SeeSharp.Shading.Materials {
    public abstract class Material {
        /// <summary>
        /// Computes the surface roughness, a value between 0 and 1.
        /// 0 is perfectly specular, 1 is perfectly diffuse.
        /// The exact value can differ between materials, as
        /// this is not a well-defined quantity from a physical point of view.
        /// </summary>
        public abstract float GetRoughness(SurfacePoint hit);

        /// <summary>
        /// Computes the sum of reflectance and transmittance.
        /// Can be an approximation, with the accuracy depending on the material.
        /// </summary>
        public abstract RgbColor GetScatterStrength(SurfacePoint hit);

        public abstract RgbColor Evaluate(SurfacePoint hit, Vector3 outDir, Vector3 inDir, bool isOnLightSubpath);

        public virtual RgbColor EvaluateWithCosine(SurfacePoint hit, Vector3 outDir, Vector3 inDir, bool isOnLightSubpath) {
            var bsdf = Evaluate(hit, outDir, inDir, isOnLightSubpath);
            inDir = ShadingSpace.WorldToShading(hit.ShadingNormal, inDir);
            return bsdf * ShadingSpace.AbsCosTheta(inDir);
        }

        public abstract BsdfSample Sample(SurfacePoint hit, Vector3 outDir, bool isOnLightSubpath, Vector2 primarySample);

        public abstract (float, float) Pdf(SurfacePoint hit, Vector3 outDir, Vector3 inDir, bool isOnLightSubpath);
    }
}
