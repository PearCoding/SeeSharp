#load "api.fsx"
open Ground

// Build a basic scene

InitScene()

let vertices = [|
    0.0f; 0.0f; 0.0f;
    1.0f; 0.0f; 0.0f;
    1.0f; 1.0f; 0.0f;
    0.0f; 1.0f; 0.0f;
|]

let indices = [|
    0; 1; 2;
    0; 2; 3
|]

AddTriangleMesh(vertices, 4, indices, 6)

FinalizeScene()

// Render the quad with an orthographic camera

let imageWidth = 512
let imageHeight = 512
let imageId = CreateImage(imageWidth, imageHeight, 1)

let topLeft  = [| -1.0f;  -1.0f; 5.0f |]
let diag = [|  3.0f; 3.0f; 0.0f |]

let stopWatch = System.Diagnostics.Stopwatch.StartNew()

for y in 0 .. imageHeight - 1 do
    for x in 0 .. imageWidth - 1 do
        let org = [|
            topLeft.[0] + float32(x) / float32(imageWidth) * diag.[0];
            topLeft.[1] + float32(y) / float32(imageHeight) * diag.[1];
            5.0f
        |]
        let dir = [| 0.0f; 0.0f; -1.0f |]

        let hit = TraceSingle(org, dir)

        let value = [| float32(hit.meshId) |]
        AddSplat(imageId, float32(x), float32(y), value)

stopWatch.Stop()
printfn "%f ms" stopWatch.Elapsed.TotalMilliseconds

WriteImage(imageId, "../../dist/renderFS.exr")

