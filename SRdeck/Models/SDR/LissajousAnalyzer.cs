using System;
using SRdeck.Models;

namespace SRdeck.Models.SDR
{
    public static class LissajousAnalyzer
    {
        private const int DAT_LEN = 3200;

        public enum AnalysisType
        {
            Lissajous,
            Vector
        }

        public static float[] CalculateDensityMap(float[] srcL, float[] srcR, bool[] sqHistory, int size, AnalysisType type)
        {
            float[] map = new float[size * size];
            float scale = size * 0.45f;
            float center = size / 2f;

            for (int k = 0; k < 5; k++)
            {
                float weight = k switch { 4 => 1.0f, 3 => 0.5f, 2 => 0.2f, 1 => 0.1f, 0 => 0.1f, _ => 0f };
                float finalWeight = weight * (sqHistory[k] ? 1.0f : 0.3f);
                int startIdx = k * DAT_LEN;
                int endIdx = (k + 1) * DAT_LEN;

                for (int i = startIdx; i < endIdx && i < srcL.Length; i++)
                {
                    float L = srcL[i];
                    float R = (srcR != null && i < srcR.Length) ? srcR[i] : L;

                    float x, y;
                    if (type == AnalysisType.Lissajous)
                    {
                        x = R - L;
                        y = R + L;
                    }
                    else // Vector
                    {
                        float magSq = R * R + L * L;
                        if (magSq > 1e-9f)
                        {
                            float mag = MathF.Sqrt(magSq);
                            x = (R * R - L * L) / mag * 1.414f;
                            y = (2.0f * R * L) / mag * 1.414f;
                        }
                        else { x = 0; y = 0; }
                    }

                    int ix = (int)(center + x * scale);
                    int iy = (int)(center - y * scale);

                    if (ix >= 0 && ix < size && iy >= 0 && iy < size)
                    {
                        map[iy * size + ix] += finalWeight;
                    }
                }
            }

            float max = 0;
            for (int i = 0; i < map.Length; i++) if (map[i] > max) max = map[i];
            if (max > 0)
            {
                float divisor = Math.Max(1.0f, max * 0.5f); 
                for (int i = 0; i < map.Length; i++) map[i] = Math.Min(1.0f, map[i] / divisor);
            }

            return map;
        }

        public static byte[] CalculateDensityMapByte(float[] srcL, float[] srcR, bool[] sqHistory, int size, AnalysisType type)
        {
            float[] map = CalculateDensityMap(srcL, srcR, sqHistory, size, type);
            byte[] byteMap = new byte[map.Length];
            for (int i = 0; i < map.Length; i++)
            {
                byteMap[i] = (byte)(map[i] * 255f);
            }
            return byteMap;
        }
    }
}
