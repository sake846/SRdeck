using System;
using SRdeck.Renderers.Compat.Direct2D1;
using SRdeck.Models;
using Rect = SRdeck.Renderers.Compat.Mathematics.Rect;
using Color = SRdeck.Renderers.Compat.Mathematics.Color4;

namespace SRdeck.Renderers;

internal partial class WaterfallRenderer
{
    private void ResizeHistory(int oldWidth, int oldHeight, int newWidth, int newHeight)
    {
        if (_waterfallData == null || oldWidth <= 0 || oldHeight <= 0 || newWidth <= 0 || newHeight <= 0)
        {
            _waterfallData = new uint[newWidth * newHeight];
            _writeY = 0;
            return;
        }

        uint[] unrolled = new uint[oldWidth * oldHeight];
        for (int y = 0; y < oldHeight; y++)
        {
            int srcY = (_writeY + y) % oldHeight;
            Array.Copy(_waterfallData, srcY * oldWidth, unrolled, y * oldWidth, oldWidth);
        }

        uint[] newArray = new uint[newWidth * newHeight];
        float scaleX = (float)oldWidth / newWidth;
        float scaleY = (float)oldHeight / newHeight;

        for (int y = 0; y < newHeight; y++)
        {
            float srcYf = (y + 0.5f) * scaleY - 0.5f;
            int oldY = (int)MathF.Round(srcYf);
            if (oldY < 0) oldY = 0;
            if (oldY >= oldHeight) oldY = oldHeight - 1;

            int newRowOffset = y * newWidth;
            int oldRowOffset = oldY * oldWidth;

            for (int x = 0; x < newWidth; x++)
            {
                float srcXf = (x + 0.5f) * scaleX - 0.5f;
                int oldX = (int)MathF.Round(srcXf);
                if (oldX < 0) oldX = 0;
                if (oldX >= oldWidth) oldX = oldWidth - 1;
                newArray[newRowOffset + x] = unrolled[oldRowOffset + oldX];
            }
        }
        _waterfallData = newArray;
        _writeY = 0;
    }

    private unsafe void UpdateWaterfallRow(RadioControl p, RadioState r, int width, int fullFftSize, float displayBw, float fullBw, int fftCenterFreqHz)
    {
        uint[] lut = ColorLUT.GetLutBgr32(p.WaterfallColorMode);
        float safeDisplayBw = Math.Max(1f, displayBw);
        float halfDisplay = safeDisplayBw * 0.5f;
        float halfFull = fullBw * 0.5f;
        float centerOffsetHz = fftCenterFreqHz > 0 ? p.CenterFreqHz - fftCenterFreqHz : 0f;

        float invWidth = 1.0f / Math.Max(1, width);
        float hzPerPixel = safeDisplayBw * invWidth;
        float startHzBase = -halfDisplay + centerOffsetHz;
        float normFactor = fullFftSize / fullBw;

        float sysDb = float.IsFinite(p.SystemDb) ? p.SystemDb : 0f;
        float minFloor = float.IsFinite(r.Min2FftPwr) ? r.Min2FftPwr : -120f;

        fixed (uint* pData = _waterfallData)
        fixed (byte* pBytes = _rowByteBuffer)
        {
            uint* pRow = (uint*)pBytes;
            int rowOffset = _writeY * width;

            for (int l = 0; l < width; l++)
            {
                float hzStart = (l * hzPerPixel) + startHzBase;
                float hzEnd = hzStart + hzPerPixel;

                if (hzEnd <= -halfFull || hzStart >= halfFull)
                {
                    pData[rowOffset + l] = 0;
                    pRow[l] = 0xFF000000;
                    continue;
                }

                float clippedHzStart = MathF.Max(hzStart, -halfFull);
                float clippedHzEnd = MathF.Min(hzEnd, halfFull);
                
                int startIdx = Math.Clamp((int)MathF.Floor((clippedHzStart + halfFull) * normFactor), 0, fullFftSize - 1);
                int endIdx = Math.Clamp((int)MathF.Ceiling((clippedHzEnd + halfFull) * normFactor), startIdx + 1, fullFftSize);

                float pMax = float.MinValue;
                for (int m = startIdx; m < endIdx; m++)
                {
                    if (_maxFftDat![m] > pMax) pMax = _maxFftDat[m];
                }

                float physicalLevel = pMax - sysDb + _rfCalOffset;
                int colorIdx = Math.Clamp((int)((physicalLevel - minFloor) * 4.0f + _biasDb), 0, 255);
                uint argb = lut[colorIdx];
                
                pData[rowOffset + l] = argb;
                pRow[l] = argb;
            }
        }
        var currentDataRect = new System.Drawing.Rectangle(0, _writeY, width, 1);
        _d2dWaterfallBitmap!.CopyFromMemory(currentDataRect, _rowByteBuffer!, (uint)(width * 4));
    }

    private unsafe void ClearWaterfallRow(int width)
    {
        Array.Clear(_rowByteBuffer!, 0, width * 4);

        fixed (uint* pData = _waterfallData)
        {
            int rowOffset = _writeY * width;
            for (int x = 0; x < width; x++)
            {
                pData[rowOffset + x] = 0;
            }
        }

        var currentDataRect = new System.Drawing.Rectangle(0, _writeY, width, 1);
        _d2dWaterfallBitmap!.CopyFromMemory(currentDataRect, _rowByteBuffer!, (uint)(width * 4));
    }

    private void SyncWaterfallScene(int width, int height)
    {
        _d2dRenderTarget!.BeginDraw();
        _d2dRenderTarget.Clear(new Color(0f, 0f, 0f, 1f));

        var srcRectLower = new Rect(0, _writeY, width, height - _writeY);
        var destRectLower = new Rect(0, 0, width, height - _writeY);
        if (_d2dWaterfallBitmap != null)
        {
            _d2dRenderTarget.DrawBitmap(_d2dWaterfallBitmap, destRectLower, 1.0f, BitmapInterpolationMode.NearestNeighbor, srcRectLower);
        }

        if (_writeY > 0 && _d2dWaterfallBitmap != null)
        {
            var srcRectUpper = new Rect(0, 0, width, _writeY);
            var destRectUpper = new Rect(0, height - _writeY, width, _writeY);
            _d2dRenderTarget.DrawBitmap(_d2dWaterfallBitmap, destRectUpper, 1.0f, BitmapInterpolationMode.NearestNeighbor, srcRectUpper);
        }

        _d2dRenderTarget.EndDraw();
    }
}
