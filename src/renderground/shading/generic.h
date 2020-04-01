#pragma once

#include "shading/shading.h"

namespace ground {

struct GenericMaterialParameters {
    const Image* baseColor;
    const Image* emission;
};

class GenericMaterial : public Material {
public:
    GenericMaterial(const Scene* scene, const GenericMaterialParameters& params);

    Float3 EvaluateBsdf(const SurfacePoint& point, const Float3& inDir,
        const Float3& outDir, bool isOnLightSubpath) const final;

    BsdfSampleInfo WrapPrimarySampleToBsdf(const SurfacePoint& point,
        Float3* inDir, const Float3& outDir, bool isOnLightSubpath,
        const Float2& primarySample) const final;

    Float3 ComputeEmission(const SurfacePoint& point, const Float3& outDir) const final;

    BsdfSampleInfo ComputeJacobians(const SurfacePoint& point,
        const Float3& inDir, const Float3& outDir,
        bool isOnLightSubpath) const final;

    bool IsEmissive() const final {
        return parameters.emission != nullptr;
    }

private:
    GenericMaterialParameters parameters;
};

} // namespace ground