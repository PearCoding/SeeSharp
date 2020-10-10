using System.Collections.Generic;
using System.IO;

namespace SeeSharp.Experiments {
    public class Benchmark {
        public Benchmark(Dictionary<string, ExperimentFactory> experiments,
                         int imageWidth, int imageHeight) {
            this.Experiments = experiments;
            this.imageWidth = imageWidth;
            this.imageHeight = imageHeight;
        }

        public void Run(List<string> sceneFilter = null, bool forceReference = false) {
            foreach (var experiment in Experiments) {
                if (sceneFilter != null && !sceneFilter.Contains(experiment.Key))
                    continue;

                var conductor = new ExperimentConductor(experiment.Value, Path.Join("results", experiment.Key),
                                                        imageWidth, imageHeight);
                conductor.Run(forceReference);
            }
        }

        public Dictionary<string, ExperimentFactory> Experiments;
        int imageWidth, imageHeight;
    }
}