﻿using GroundWrapper;
using Integrators;
using System;

namespace Renderer {

    class Program {
        static void Main(string[] args) {
            var scene = Scene.LoadFromFile("../../data/scenes/cbox.json");
            scene.FrameBuffer = new Image(512, 512);
            scene.Prepare();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            {
                var algorithm = new PathTracer();
                algorithm.TotalSpp = 10;
                algorithm.MaxDepth = 5;
                algorithm.MinDepth = 1;
                algorithm.Render(scene);
                scene.FrameBuffer.WriteToFile("CboxPT.exr");
            }
            //scene.FrameBuffer = new Image(scene.FrameBuffer.Width, scene.FrameBuffer.Height);
            //{
            //    var algorithm = new ClassicBidir();
            //    algorithm.NumIterations = 2;
            //    algorithm.MaxDepth = 5;
            //    algorithm.Render(scene);
            //    scene.FrameBuffer.WriteToFile("CboxClassicBidir.png");
            //}
            stopwatch.Stop();
            Console.WriteLine(stopwatch.ElapsedMilliseconds);

        }
    }

}
