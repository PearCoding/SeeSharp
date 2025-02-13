﻿using SeeSharp;
using SeeSharp.Image;
using SeeSharp.Cameras;
using SeeSharp.Geometry;
using SeeSharp.Shading;
using SeeSharp.Shading.Background;
using SeeSharp.Shading.Materials;
using System.Numerics;

namespace SeeSharp.Validation {
    class Validate_Environment : ValidationSceneFactory {
        public override int SamplesPerPixel => 10;

        public override int MaxDepth => 5;

        public override string Name => "Environment";

        public override Scene MakeScene() {
            var scene = Scene.LoadFromFile("Data/Scenes/simplebackground.json");
            //var scene = new Scene();

            //// Ground plane
            //float groundRadius = 0.1f;
            //scene.Meshes.Add(new Mesh(new Vector3[] {
            //    new Vector3(-groundRadius, -groundRadius, -2),
            //    new Vector3( groundRadius, -groundRadius, -2),
            //    new Vector3( groundRadius,  groundRadius, -2),
            //    new Vector3(-groundRadius,  groundRadius, -2),
            //}, new int[] {
            //    0, 1, 2, 0, 2, 3
            //}));
            //scene.Meshes[^1].Material = new DiffuseMaterial(new DiffuseMaterial.Parameters {
            //    baseColor = Image.Constant(RgbColor.White)
            //});

            //// Environment map background illumination
            //Image image = new Image(512, 256);
            //for (int row = 0; row < image.Height; ++row)
            //    for (int col = 0; col < image.Width; ++col)
            //        image.Splat(col, row, RgbColor.White * 1.0f);
            ////image.Splat(255, 128, RgbColor.White * 1000);
            //scene.Background = new EnvironmentMap(image);

            //// Camera and frame buffer
            //scene.Camera = new PerspectiveCamera(Matrix4x4.CreateLookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY), 40, null);
            scene.FrameBuffer = new FrameBuffer(512, 512, "");

            scene.Prepare();

            return scene;
        }
    }
}
