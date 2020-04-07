#pragma once

#include <vector>
#include <memory>
#include <embree3/rtcore.h>

#include "renderground/geometry/mesh.h"

namespace ground {

class Scene {
public:
    ~Scene() {
        if (isInit) {
            rtcReleaseScene(embreeScene);
            rtcReleaseDevice(embreeDevice);
        }
    }

    void Init();

    int AddMesh(Mesh&& mesh);
    const Mesh* GetMesh(int meshId) const { return meshes[meshId].get(); }
    int GetNumMeshes() const { return meshes.size(); }

    void Finalize();

    Hit Intersect(const Ray& ray);

private:
    std::vector<std::unique_ptr<Mesh>> meshes;
    bool isInit = false;
    bool isFinal = false;

    RTCDevice embreeDevice;
    RTCScene embreeScene;
};


} // namespace ground
