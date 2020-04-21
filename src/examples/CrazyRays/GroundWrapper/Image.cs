using GroundWrapper.Shading;
using System.Runtime.InteropServices;
using System.Threading;

namespace GroundWrapper {
    public class Image {
        public int Width {
            get; private set;
        }

        public int Height {
            get; private set;
        }

        public Image(int width, int height) {
            if (width < 1 || height < 1)
                throw new System.ArgumentOutOfRangeException("width / height",
                    "Cannot create an image smaller than 1x1 pixels.");

            Width = width;
            Height = height;

            data = new ColorRGB[width * height];
        }

        float AtomicAddFloat(ref float target, float addend) {
            float initialValue, computedValue;
            do {
                initialValue = target;
                computedValue = initialValue + addend;
            } while (initialValue !=
                Interlocked.CompareExchange(ref target, computedValue, initialValue));
            return computedValue;
        }

        public void Splat(float x, float y, ColorRGB value) {
            int idx = IndexOf(x, y);
            lock (this) {
                this[x, y] += value;
            }
            //AtomicAddFloat(ref data[idx].r, value.r);
            //AtomicAddFloat(ref data[idx].g, value.g);
            //AtomicAddFloat(ref data[idx].b, value.b);
        }

        int IndexOf(float x, float y) {
            int row = (int)y;
            int col = (int)x;

            row = System.Math.Clamp(row, 0, Height - 1);
            col = System.Math.Clamp(col, 0, Width - 1);

            return col + row * Width;
        }

        /// <summary>
        /// Allows access to a pixel in the image. (0,0) is the top left corner.
        /// Not thread-safe unless read-only. Use <see cref="Splat(float, float)"/> for thread-safe writing.
        /// </summary>
        /// <param name="x">Horizontal coordinate [0,width], left to right.</param>
        /// <param name="y">Vertical coordinate [0, height], top to bottom.</param>
        /// <returns>The pixel color.</returns>
        public ColorRGB this[float x, float y] {
            get => data[IndexOf(x, y)];
            set => data[IndexOf(x, y)] = value;
        }

        public void WriteToFile(string filename) {
            var ext = System.IO.Path.GetExtension(filename);
            if (ext.ToLower() == ".exr")
                TinyExr.WriteImageToExr(data, Width, Height, 3, filename);
            else {
                WriteImageToLDR(this, filename);
            }
        }

        public static Image LoadFromFile(string filename) {
            // Read the image from the file, it is cached in nativ memory
            int width, height;
            int id = TinyExr.CacheExrImage(out width, out height, filename);
            if (id < 0)
                throw new System.IO.IOException($"could not load .exr file '{filename}'");

            // Copy to managed memory array and return
            var img = new Image(width, height);
            TinyExr.CopyCachedImage(id, img.data);
            return img;

            // TODO performance could probably be improved by using the memory allocated by the managed code, instead of copying.
            //      However, the tinyexr library uses a different structure, so there is a benefit to copying as well.
        }

        public static Image Constant(ColorRGB color) {
            var img = new Image(1, 1);
            img[0, 0] = color;
            return img;
        }

        readonly ColorRGB[] data;

        private static class TinyExr {
            [DllImport("Ground", CallingConvention = CallingConvention.Cdecl)]
            public static extern void WriteImageToExr(ColorRGB[] data, int width, int height, int numChannels,
                                                      string filename);

            [DllImport("Ground", CallingConvention = CallingConvention.Cdecl)]
            public static extern int CacheExrImage(out int width, out int height, string filename);

            [DllImport("Ground", CallingConvention = CallingConvention.Cdecl)]
            public static extern void CopyCachedImage(int id, [Out] ColorRGB[] buffer);
        }

        static void WriteImageToLDR(Image img, string filename) {
            int width = img.Width;
            int height = img.Height;
            using (System.Drawing.Bitmap b = new System.Drawing.Bitmap(width, height)) {
                for (int x = 0; x < width; ++x) for (int y = 0; y < height; ++y) {
                        var px = img[x,y];
                        int ToInt(float chan) {
                            chan = System.MathF.Pow(chan, 1/2.2f);
                            return System.Math.Clamp((int)(chan * 255), 0, 255);
                        }
                        b.SetPixel(x, y, System.Drawing.Color.FromArgb(ToInt(px.r), ToInt(px.g), ToInt(px.b)));
                    }
                b.Save(filename);
            }
        }
    }
}