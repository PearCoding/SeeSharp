﻿using SeeSharp.Core;
using SeeSharp.Experiments;
using SeeSharp.Integrators;
using System.Collections.Generic;
using System.IO;

namespace Experiments {
    public class ExperimentConductor {
        public ExperimentConductor(ExperimentFactory experimentFactory,
                                   string workingDirectory,
                                   int width, int height) {
            factory = experimentFactory;
            this.workingDirectory = workingDirectory;
            this.width = width;
            this.height = height;
        }

        /// <summary>
        /// Renders the scene with all methods. If the reference image does not exist yet,
        /// it is also rendered.
        /// Each method's images are placed in a separate folder, using the method's name
        /// as the folder's name.
        /// </summary>
        public void Run(bool forceReference = false) {
            var scene = factory.MakeScene();

            // Render the reference image (only if it does not exist or forced)
            scene.FrameBuffer = new FrameBuffer(width, height, Path.Join(workingDirectory, "reference.exr"));
            scene.Prepare();
            RenderReference(scene, forceReference);
            allImages.Add("reference.exr");

            var methods = factory.MakeMethods();
            foreach (var method in methods) {
                string path = Path.Join(workingDirectory, method.name);
                Render(path, "render.exr", method.integrator, scene);

                allImages.Add(Path.Join(method.name, "render.exr"));
                allImages.AddRange(method.files);
            }

            GenerateOpenScript();
        }

        private void GenerateOpenScript() {
            string command = "tev " + string.Join(" ", allImages);
            System.IO.File.WriteAllText(Path.Join(workingDirectory, "open-tev.ps1"), command);
        }

        private void RenderReference(Scene scene, bool force) {
            string filepath = Path.Join(workingDirectory, "reference.exr");
            bool exists = File.Exists(filepath);
            if (!exists || force) {
                var integrator = factory.MakeReferenceIntegrator();
                scene.FrameBuffer = new FrameBuffer(width, height, filepath);
                integrator.Render(scene);
                scene.FrameBuffer.WriteToFile();
            }
        }

        private double Render(string dir, string filename, Integrator integrator, Scene scene) {
            scene.FrameBuffer = new FrameBuffer(width, height, Path.Join(dir, filename), factory.FrameBufferFlags);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            integrator.Render(scene);
            stopwatch.Stop();

            scene.FrameBuffer.WriteToFile();
            return stopwatch.ElapsedMilliseconds / 1000.0;
        }

        ExperimentFactory factory;
        string workingDirectory;
        int width, height;
        List<string> allImages = new List<string>();
    }
}
