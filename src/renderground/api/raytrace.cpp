#include "api/raytrace.h"
#include "api/internal.h"
#include "api/cpputils.h"
#include "geometry/scene.h"
#include "api/cpputils.h"
#include "math/constants.h"

#include <tbb/parallel_for.h>

std::unique_ptr<ground::Scene> globalScene;

extern "C" {

GROUND_API void InitScene() {
    globalScene.reset(new ground::Scene());
    globalScene->Init();
}

GROUND_API void DeleteScene() {
    globalScene.release();
}

GROUND_API int AddTriangleMesh(const float* vertices, int numVerts,
    const int* indices, int numIdx, const float* texCoords, const float* shadingNormals)
{
    ApiCheck(numIdx % 3 == 0);

    return globalScene->AddMesh(ground::Mesh(
        reinterpret_cast<const Vector3*>(vertices), numVerts,
        indices, numIdx, reinterpret_cast<const Vector2*>(texCoords),
        reinterpret_cast<const Vector3*>(shadingNormals)));
}

GROUND_API void FinalizeScene() {
    // Scan the scene for all emissive objects and keep track of them.
    for (int meshId = 0; meshId < globalScene->GetNumMeshes(); ++meshId) {
        // Check if the mesh has a material
        auto iter = globalMeshToMaterial.find(meshId);
        if (iter == globalMeshToMaterial.end())
            continue;

        // Add to the registry if the material is emissive
        if (globalMaterials[iter->second]->IsEmissive())
            globalEmitterRegistry.push_back(meshId);
    }

    globalScene->Finalize();
}

GROUND_API Hit TraceSingle(Ray ray) {
    return globalScene->Intersect(ray);
}

GROUND_API void TraceMulti(const Ray* rays, int num, Hit* hits) {
    // TODO this should instead trigger a call to IntersectN
    //      in Embree, to reap additional performance!
    tbb::parallel_for(tbb::blocked_range<int>(0, num),
        [&](tbb::blocked_range<int> r) {
        for (int i = r.begin(); i < r.end(); ++i) {
            hits[i] = globalScene->Intersect(rays[i]);
        }
    });
}

GROUND_API SurfaceSample WrapPrimarySampleToSurface(int meshId, float u, float v) {
    ApiCheck(u >= 0 && u <= 1);
    ApiCheck(v >= 0 && v <= 1);
    ApiCheck(meshId < globalScene->GetNumMeshes());
    ApiCheck(meshId >= 0);

    // Get the mesh, wrap the sample to its surface, and set the correct meshId
    auto& m = globalScene->GetMesh(meshId);
    auto sample = m.PrimarySampleToSurface(Vector2{u, v});
    sample.point.meshId = meshId;

    return sample;
}

GROUND_API EmitterSample WrapPrimarySampleToEmitterRay(int meshId,
    Vector2 primaryPos, Vector2 primaryDir)
{
    SurfaceSample sample = WrapPrimarySampleToSurface(meshId, primaryPos.x, primaryPos.y);

    Ray ray;
    float dirJacobian;

    return EmitterSample{
        sample, ray, dirJacobian
    };
}

GROUND_API Vector2 ComputePrimaryToEmitterRayJacobian(SurfacePoint origin, Vector3 direction) {
    return Vector2 { 0, 0 };
}

GROUND_API float ComputePrimaryToSurfaceJacobian(const SurfacePoint* point) {
    auto& m = globalScene->GetMesh(point->meshId);
    return m.ComputePrimaryToSurfaceJacobian(*point);
}

GROUND_API bool IsOccluded(const Hit* from, Vector3 to) {
    // TODO this function could (and should) call a special variant of "TraceSingle"
    //      that only checks occlusion for performance.

    auto shadowDir = to - from->point.position;
    auto shadowHit = TraceSingle(Ray{from->point.position, shadowDir, from->errorOffset});
    if (shadowHit.point.meshId >= 0 && shadowHit.distance < 1.0f - from->errorOffset)
        return true;
    return false;
}

GROUND_API Ray SpawnRay(const Hit* from, Vector3 direction) {
    float sign = Dot(direction, from->point.normal) < 0.0f ? -1.0f : 1.0f;
    return Ray {
        from->point.position + sign * from->errorOffset * from->point.normal,
        direction,
        from->errorOffset
    };
}

GROUND_API GeometryTerms ComputeGeometryTerms(const SurfacePoint* from, const SurfacePoint* to) {
    auto dir = to->position - from->position;
    float squaredDistance = LengthSquared(dir);
    dir = dir / std::sqrt(squaredDistance);

    ground::CheckNormalized(from->normal);
    ground::CheckNormalized(to->normal);

    float cosSurface = std::abs(Dot(from->normal, dir));
    float cosLight = std::abs(Dot(to->normal, -dir));

    float geomTerm = cosSurface * cosLight / squaredDistance;

    // avoid NaNs if we happen to sample the exact same point "to" and "from"
    if (squaredDistance == 0.0f) geomTerm = 0.0f;

    return GeometryTerms {
        cosSurface,
        cosLight,
        squaredDistance,
        geomTerm
    };
}

GROUND_API Vector3 ComputeShadingNormal(SurfacePoint point) {
    return globalScene->GetMesh(point.meshId).ComputeShadingNormal(
        point.primId, point.barycentricCoords);
}

}