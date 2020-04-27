﻿using GroundWrapper.Shading.Bsdfs;
using System.Numerics;

namespace GroundWrapper.Geometry {
    public struct SurfacePoint {
        public Vector3 position;
        public Vector3 normal;
        public Vector2 barycentricCoords;
        public Mesh mesh;
        public uint primId;
        public float errorOffset;
        public float distance;

        public static implicit operator bool(SurfacePoint hit)
            => hit.mesh != null;

        public Vector3 ShadingNormal => mesh.ComputeShadingNormal((int)primId, barycentricCoords);

        public Vector2 TextureCoordinates => mesh.ComputeTextureCoordinates((int)primId, barycentricCoords);

        public Bsdf Bsdf => mesh.Material.GetBsdf(this);
    }
}
