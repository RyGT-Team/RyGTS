// SPDX-License-Identifier: GPL-2.0-only
using System;
using System.Drawing;
using System.Diagnostics;
using System.IO;
namespace RyGT
{
    internal static class RenderResolution
    {
        public static Size Calculate(int outputWidth, int outputHeight, string preset)
        {
            if (outputWidth <= 0 || outputHeight <= 0) throw new ArgumentOutOfRangeException("outputWidth");
            double ratio;
            switch (preset) {
                case "ultra_performance": ratio = 1.0 / 3.0; break;
                case "performance": ratio = 0.5; break;
                case "balanced": ratio = 1.0 / 1.7; break;
                case "quality": ratio = 1.0 / 1.5; break;
                case "ultra_quality": ratio = 1.0 / 1.3; break;
                case "dlaa": ratio = 1.0; break;
                default: throw new ArgumentException("Unknown render-scale preset.", "preset");
            }
            // The scene is 16:9; output/camera aspect remains independently configured.
            double height = Math.Min(outputHeight, outputWidth * 9.0 / 16.0) * ratio;
            int units = Math.Max(20, Math.Min(240, (int)Math.Round(height / 9.0)));
            return new Size(units * 16, units * 9);
        }

        static void RequireSignature(byte[] pe, int rva, string hex)
        {
            int header = BitConverter.ToInt32(pe, 0x3c);
            int sections = BitConverter.ToUInt16(pe, header + 6);
            int table = header + 24 + BitConverter.ToUInt16(pe, header + 20);
            for (int i = 0; i < sections; ++i) {
                int at = table + i * 40;
                int va = BitConverter.ToInt32(pe, at + 12), size = BitConverter.ToInt32(pe, at + 16);
                if (rva < va || rva + hex.Length / 2 > (long)va + size) continue;
                int raw = BitConverter.ToInt32(pe, at + 20) + rva - va;
                for (int j = 0; j < hex.Length / 2; ++j)
                    if (pe[raw + j] != Convert.ToByte(hex.Substring(j * 2, 2), 16))
                        throw new InvalidOperationException("Experimental render scale does not support this game executable.");
                return;
            }
            throw new InvalidOperationException("Experimental render-scale patch site is missing.");
        }

        public static void ConfigureStart(ProcessStartInfo start, string root, Size? scene)
        {
            start.EnvironmentVariables.Remove("SHADGT_EXPERIMENTAL_SCENE_VIEWPORT");
            start.EnvironmentVariables.Remove("SHADGT_SCENE_WIDTH");
            start.EnvironmentVariables.Remove("SHADGT_SCENE_HEIGHT");
            // Composition downscaling is an independent quality experiment, not an upscaler.
            start.EnvironmentVariables.Remove("SHADGT_LOW_RES_COMPOSITION");
            if (!scene.HasValue) return;
            if (!String.IsNullOrEmpty(start.EnvironmentVariables["SHADGT_PATCH"]))
                throw new InvalidOperationException("Unset SHADGT_PATCH before using launcher render scaling.");
            Size size = scene.Value;
            byte[] pe = File.ReadAllBytes(Path.Combine(root, "GTSport.exe"));
            RequireSignature(pe, 0x1988ad3, "48b98007000038040000");
            RequireSignature(pe, 0x1988ab0, "8b0d72ea2301");
            RequireSignature(pe, 0x1988ab9, "8b0d6dea2301");
            string width = BitConverter.ToString(BitConverter.GetBytes(size.Width)).Replace("-", "").ToLowerInvariant();
            string height = BitConverter.ToString(BitConverter.GetBytes(size.Height)).Replace("-", "").ToLowerInvariant();
            start.EnvironmentVariables["SHADGT_PATCH"] = "1988ad3:48b98007000038040000:48b9" + width + height +
                ",1988ab0:8b0d72ea2301:b9" + width + "90,1988ab9:8b0d6dea2301:b9" + height + "90";
            start.EnvironmentVariables["SHADGT_EXPERIMENTAL_SCENE_VIEWPORT"] = "1";
            start.EnvironmentVariables["SHADGT_SCENE_WIDTH"] = size.Width.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.EnvironmentVariables["SHADGT_SCENE_HEIGHT"] = size.Height.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
