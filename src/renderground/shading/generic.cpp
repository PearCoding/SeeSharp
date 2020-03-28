#include "shading/generic.h"

namespace ground
{

GenericMaterial::GenericMaterial(const GenericMaterialParameters& params) {

}

float GenericMaterial::EvaluateBsdf(const SurfacePoint& point,
    const Float3& inDir, const Float3& outDir, float wavelength,
    bool isOnLightSubpath) const
{
    return 0.0f;
}

float GenericMaterial::WrapPrimarySampleToBsdf(const SurfacePoint& point,
    Float3* inDir, const Float3& outDir, float wavelength,
    bool isOnLightSubpath) const
{
    return 0.0f;
}

float GenericMaterial::ComputeEmission(const SurfacePoint& point,
    const Float3& outDir, float wavelength) const
{
    return 0.0f;
}

} // namespace ground
