using System;
using System.Drawing;
using System.Collections.Generic;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;

namespace BatteryBar
{
    internal enum TypeWeight { Regular, Medium, Bold }

    internal static class Typography
    {
        // Separate collections keep non-RIBBI weights from being merged with the
        // Regular/Bold faces of the same family by GDI+.
        private sealed class Face
        {
            private readonly PrivateFontCollection collection;
            private readonly FontFamily family;
            private readonly FontStyle style;

            internal Face(PrivateFontCollection collection, FontFamily family, FontStyle style)
            {
                this.collection = collection;
                this.family = family;
                this.style = style;
            }

            internal Font Create(float size)
            {
                Font font = new Font(family, size, style, GraphicsUnit.Pixel);
                GC.KeepAlive(collection);
                return font;
            }

            internal void Dispose()
            {
                family.Dispose();
                collection.Dispose();
            }
        }

        private static readonly Face[] faces =
        {
            Load("Regular", FontStyle.Regular),
            Load("Medium", FontStyle.Regular),
            Load("Bold", FontStyle.Bold)
        };

        private static readonly Dictionary<int, Font> controls = new Dictionary<int, Font>();
        private static readonly byte[] coverage = LoadCoverage();

        internal static Font ControlFont(int size)
        {
            Font font;
            if (!controls.TryGetValue(size, out font))
            {
                font = Create("外设电量", size, TypeWeight.Medium);
                controls.Add(size, font);
            }
            return font;
        }

        internal static Font Create(string text, float size, TypeWeight weight)
        {
            Face face = faces[(int)weight];
            if (face != null && UsesHarmony(text)) return face.Create(size);
            return SystemFont(size, weight);
        }

        internal static Font SystemFont(float size, TypeWeight weight = TypeWeight.Regular)
        {
            return new Font("Microsoft YaHei UI", size,
                weight == TypeWeight.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        }

        internal static void Release()
        {
            foreach (Font font in controls.Values) font.Dispose();
            controls.Clear();
            foreach (Face face in faces)
                if (face != null) face.Dispose();
        }

        private static bool UsesHarmony(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                int point = text[i];
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    point = char.ConvertToUtf32(text[i], text[i + 1]);
                    i++;
                }
                if ((point >> 3) >= coverage.Length || (coverage[point >> 3] & (1 << (point & 7))) == 0)
                    return false;
            }
            return true;
        }

        private static byte[] LoadCoverage()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fonts", "HarmonyOS_Sans_SC.coverage");
            try { return File.ReadAllBytes(path); }
            catch (IOException) { return new byte[0]; }
            catch (UnauthorizedAccessException) { return new byte[0]; }
        }

        private static Face Load(string weight, FontStyle preferred)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fonts", "HarmonyOS_Sans_SC_" + weight + ".ttf");
            if (!File.Exists(path)) return null;
            var collection = new PrivateFontCollection();
            FontFamily[] families = null;
            bool retained = false;
            try
            {
                collection.AddFontFile(path);
                families = collection.Families;
                if (families.Length != 1) return null;
                FontFamily family = families[0];
                FontStyle alternate = preferred == FontStyle.Regular ? FontStyle.Bold : FontStyle.Regular;
                FontStyle style = family.IsStyleAvailable(preferred) ? preferred : alternate;
                if (!family.IsStyleAvailable(style)) return null;

                var face = new Face(collection, family, style);
                using (face.Create(12)) { }
                retained = true;
                return face;
            }
            catch (ArgumentException) { return null; }
            catch (ExternalException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            finally
            {
                if (!retained)
                {
                    if (families != null)
                        foreach (FontFamily family in families) family.Dispose();
                    collection.Dispose();
                }
            }
        }
    }
}
