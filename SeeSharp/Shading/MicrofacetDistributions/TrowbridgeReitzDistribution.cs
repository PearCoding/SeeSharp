﻿using System;
using System.Diagnostics;
using System.Numerics;

namespace SeeSharp.Shading.MicrofacetDistributions {
    /// <summary>
    /// GGX microfacet distribution. Heavily based on PBRT v3.
    /// </summary>
    public struct TrowbridgeReitzDistribution {
        /// <summary>
        /// Squared roughness in one direction
        /// </summary>
        public float AlphaX;

        /// <summary>
        /// Squared roughness in the other direction
        /// </summary>
        public float AlphaY;

        /// <summary>
        /// Computes the distribution of microfacets with the given normal.
        /// </summary>
        /// <param name="normal">The normal vector of the microfacets, in shading space.</param>
        /// <returns>The fraction of microfacets that are oriented with the given normal.</returns>
        public float NormalDistribution(Vector3 normal) {
            float tan2Theta = ShadingSpace.TanThetaSqr(normal);
            if (float.IsInfinity(tan2Theta)) return 0;

            float cos4Theta = ShadingSpace.CosThetaSqr(normal) * ShadingSpace.CosThetaSqr(normal);

            float e = tan2Theta * (
                  ShadingSpace.CosPhiSqr(normal) / (AlphaX * AlphaX)
                + ShadingSpace.SinPhiSqr(normal) / (AlphaY * AlphaY)
            );

            return 1 / (MathF.PI * AlphaX * AlphaY * cos4Theta * (1 + e) * (1 + e));
        }

        /// <summary>
        /// Computes the masking-shadowing function:
        /// The ratio of visible microfacet area to the total area of all correctly oriented microfacets.
        /// </summary>
        /// <param name="normal">
        /// The normal vector of the microfacets, in shading space.
        /// </param>
        /// <returns>The masking shadowing function value ("G" in most papers).</returns>
        public float MaskingShadowing(Vector3 normal) {
            return 1 / (1 + MaskingRatio(normal));
        }

        public float MaskingShadowing(Vector3 outDir, Vector3 inDir) {
            return 1 / (1 + MaskingRatio(outDir) + MaskingRatio(inDir));
        }

        /// <summary>
        /// The Pdf that is used for importance sampling microfacet normals from this distribution.
        /// This usually importance samples the portion of normals that are in the hemisphere of the outgoing direction.
        /// </summary>
        /// <param name="outDir">The outgoing direction in shading space.</param>
        /// <param name="inDir">The incoming direction in shading space.</param>
        /// <returns>The pdf value.</returns>
        public float Pdf(Vector3 outDir, Vector3 normal) =>
            NormalDistribution(normal) * MaskingShadowing(outDir) * MathF.Abs(Vector3.Dot(outDir, normal))
                / ShadingSpace.AbsCosTheta(outDir);

        /// <summary>
        /// Warps the given primary sample to follow the pdf computed by <see cref="Pdf(Vector3, Vector3)"/>.
        /// </summary>
        /// <returns>The direction that corresponds to the given primary sample.</returns>
        public Vector3 Sample(Vector3 outDir, Vector2 primary) {
            bool flip = ShadingSpace.CosTheta(outDir) < 0;
            var wh = TrowbridgeReitzSample(flip ? -outDir : outDir, AlphaX, AlphaY, primary.X, primary.Y);
            if (flip) wh = -wh;
            return wh;
        }

        /// <summary>
        /// Computes the ratio of self-masked area to visible area. Used by <see cref="MaskingShadowing(Vector3)"/>.
        /// </summary>
        /// <param name="normal">Normal of the microfacets, in shading space.</param>
        /// <returns>Ratio of self-masked area to visible area.</returns>
        public float MaskingRatio(Vector3 normal) {
            float absTanTheta = MathF.Abs(ShadingSpace.TanTheta(normal));
            if (float.IsInfinity(absTanTheta)) return 0;
            float alpha = MathF.Sqrt(ShadingSpace.CosPhiSqr(normal) * AlphaX * AlphaX + ShadingSpace.SinPhiSqr(normal) * AlphaY * AlphaY);
            float alpha2Tan2Theta = alpha * absTanTheta * (alpha * absTanTheta);
            return (-1 + MathF.Sqrt(1 + alpha2Tan2Theta)) / 2;
        }

        public static void TrowbridgeReitzSample11(float cosTheta, float U1, float U2,
                                                   out float slope_x, out float slope_y) {
            // special case (normal incidence)
            if (cosTheta > .9999) {
                if (U1 == 1) {
                    // Catch NaN/Inf causing corner case and produce a valid number instead
                    // (from a math point of view, this can never happen anyway)
                    slope_x = 0;
                    slope_y = 0;
                    return;
                }

                float r = MathF.Sqrt(U1 / (1 - U1));
                float phi = (float)(6.28318530718 * U2);
                slope_x = r * MathF.Cos(phi);
                slope_y = r * MathF.Sin(phi);

                Debug.Assert(float.IsFinite(slope_x));
                Debug.Assert(float.IsFinite(slope_y));
                return;
            }

            float sinTheta = MathF.Sqrt(Math.Max(0, 1 - cosTheta * cosTheta));
            float tanTheta = sinTheta / cosTheta;
            float a = 1 / tanTheta;
            float G1 = 2 / (1 + MathF.Sqrt(1 + 1 / (a * a)));

            // sample slope_x
            float A = 2 * U1 / G1 - 1;
            float tmp = 1 / (A * A - 1);
            if (tmp > 1e10) tmp = 1e10f;
            float B = tanTheta;
            float D = MathF.Sqrt(Math.Max(B * B * tmp * tmp - (A * A - B * B) * tmp, 0));
            float slope_x_1 = B * tmp - D;
            float slope_x_2 = B * tmp + D;
            slope_x = A < 0 || slope_x_2 > 1 / tanTheta ? slope_x_1 : slope_x_2;

            // sample slope_y
            float S;
            if (U2 > 0.5f) {
                S = 1;
                U2 = 2 * (U2 - .5f);
            } else {
                S = -1;
                U2 = 2 * (.5f - U2);
            }
            float z = U2 * (U2 * (U2 * 0.27385f - 0.73369f) + 0.46341f)
                / (U2 * (U2 * (U2 * 0.093073f + 0.309420f) - 1.000000f) + 0.597999f);
            slope_y = S * z * MathF.Sqrt(1 + slope_x * slope_x);

            Debug.Assert(float.IsFinite(slope_x));
            Debug.Assert(float.IsFinite(slope_y));
        }

        static Vector3 TrowbridgeReitzSample(Vector3 wi, float alpha_x,
                                             float alpha_y, float U1, float U2) {
            // 1. stretch wi
            Vector3 wiStretched = Vector3.Normalize(new Vector3(alpha_x * wi.X, alpha_y * wi.Y, wi.Z));

            // 2. simulate P22_{wi}(x_slope, y_slope, 1, 1)
            float slope_x, slope_y;
            TrowbridgeReitzSample11(ShadingSpace.CosTheta(wiStretched), U1, U2, out slope_x, out slope_y);

            // 3. rotate
            float tmp = ShadingSpace.CosPhi(wiStretched) * slope_x - ShadingSpace.SinPhi(wiStretched) * slope_y;
            slope_y = ShadingSpace.SinPhi(wiStretched) * slope_x + ShadingSpace.CosPhi(wiStretched) * slope_y;
            slope_x = tmp;

            // 4. unstretch
            slope_x = alpha_x * slope_x;
            slope_y = alpha_y * slope_y;

            // 5. compute normal
            return Vector3.Normalize(new Vector3(-slope_x, -slope_y, 1));
        }
    }
}
