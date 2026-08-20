using System;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SRdeck.DSP;

/// <summary>
/// FFT前処理などで使用するハニング窓（HanningWindow Window）関数を生成・適用するクラスです。
/// 信号の端点での不連続性を和らげ、周波数漏れ（リーケージ）を防ぎます。
/// </summary>
internal class HanningWindow
{
    public float[] hData;
    private int _s_size;
    public int Size => _s_size;

    public HanningWindow(int sampleSize)
    {
        _s_size = sampleSize;
        hData = new float[_s_size];
        for (int i = 0; i < _s_size; i++)
        {
            hData[i] = 0.5f - 0.5f * MathF.Cos((float)Math.PI * 2f * (float)i / (float)(_s_size - 1));
        }
    }

    /// <summary>
    /// 入力されたI/Qデータセグメント（short型）に対してハニング窓を適用し、指定されたバッファに書き込みます。
    /// </summary>
    public unsafe int ApplyWindowShort(short[] samplei, short[] sampleq, int offset, float[] desti, float[] destq)
    {
        int len = samplei.Length;
        int start = offset % len;
        if (start < 0) start += len;
        
        int size = _s_size;
        float[] h = hData;
        int available = len - start;

        fixed (short* pSampleI = samplei)
        fixed (short* pSampleQ = sampleq)
        fixed (float* pDestI = desti)
        fixed (float* pDestQ = destq)
        fixed (float* pH = h)
        {
            if (available >= size)
            {
                ApplySimd(pSampleI + start, pSampleQ + start, pH, pDestI, pDestQ, size);
            }
            else
            {
                ApplySimd(pSampleI + start, pSampleQ + start, pH, pDestI, pDestQ, available);
                ApplySimd(pSampleI, pSampleQ, pH + available, pDestI + available, pDestQ + available, size - available);
            }
        }
        return 0;
    }

    public int ApplyWindowShort(IqSampleRingBuffer samples, int offset, float[] desti, float[] destq)
    {
        samples.ApplyWindowToFloat(offset, hData, desti, destq, _s_size);
        return 0;
    }

    /// <summary>
    /// 入力データから窓関数を適用し、直接 Complex 配列へ書き込みます（FFT直前用）。
    /// </summary>
    public unsafe int ApplyWindow(short[] samplei, short[] sampleq, int offset, Complex[] dest)
    {
        int len = samplei.Length;
        int start = offset % len;
        if (start < 0) start += len;
        
        int size = _s_size;
        float[] h = hData;
        int available = len - start;

        fixed (short* pSampleI = samplei)
        fixed (short* pSampleQ = sampleq)
        fixed (float* pH = h)
        fixed (Complex* pDest = dest)
        {
            if (available >= size)
            {
                ApplySimdComplex(pSampleI + start, pSampleQ + start, pH, pDest, size);
            }
            else
            {
                ApplySimdComplex(pSampleI + start, pSampleQ + start, pH, pDest, available);
                ApplySimdComplex(pSampleI, pSampleQ, pH + available, pDest + available, size - available);
            }
        }
        return 0;
    }

    public int ApplyWindow(IqSampleRingBuffer samples, int offset, Complex[] dest)
    {
        samples.ApplyWindowToComplex(offset, hData, dest, _s_size);
        return 0;
    }

    /// <summary>
    /// float[] 入力データから窓関数を適用し、直接 Complex 配列へ書き込みます。
    /// </summary>
    public int ApplyWindow(float[] samplei, float[] sampleq, int offset, Complex[] dest)
    {
        int len = samplei.Length;
        int start = offset % len;
        if (start < 0) start += len;
        
        int size = _s_size;
        float[] h = hData;
        int available = len - start;

        if (available >= size)
        {
            for (int i = 0; i < size; i++)
            {
                int idx = start + i;
                dest[i].X = samplei[idx] * h[i];
                dest[i].Y = sampleq[idx] * h[i];
            }
        }
        else
        {
            for (int i = 0; i < available; i++)
            {
                int idx = start + i;
                dest[i].X = samplei[idx] * h[i];
                dest[i].Y = sampleq[idx] * h[i];
            }
            for (int i = 0; i < size - available; i++)
            {
                dest[available + i].X = samplei[i] * h[available + i];
                dest[available + i].Y = sampleq[i] * h[available + i];
            }
        }
        return 0;
    }

    private unsafe void ApplySimd(short* pSrcI, short* pSrcQ, float* pH, float* pDstI, float* pDstQ, int count)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated && count >= 8)
        {
            int limit = count - 8;
            for (; i <= limit; i += 8)
            {
                Vector128<short> sI = Vector128.Load(pSrcI + i);
                Vector128<short> sQ = Vector128.Load(pSrcQ + i);

                var (iIL, iIH) = Vector128.Widen(sI);
                var (iQL, iQH) = Vector128.Widen(sQ);

                Vector128<float> fIL = Vector128.ConvertToSingle(iIL);
                Vector128<float> fIH = Vector128.ConvertToSingle(iIH);
                Vector128<float> fQL = Vector128.ConvertToSingle(iQL);
                Vector128<float> fQH = Vector128.ConvertToSingle(iQH);

                Vector128<float> hL = Vector128.Load(pH + i);
                Vector128<float> hH = Vector128.Load(pH + i + 4);

                Vector128.Store(fIL * hL, pDstI + i);
                Vector128.Store(fIH * hH, pDstI + i + 4);
                Vector128.Store(fQL * hL, pDstQ + i);
                Vector128.Store(fQH * hH, pDstQ + i + 4);
            }
        }
        for (; i < count; i++)
        {
            pDstI[i] = (float)pSrcI[i] * pH[i];
            pDstQ[i] = (float)pSrcQ[i] * pH[i];
        }
    }

    private unsafe void ApplySimdComplex(short* pSrcI, short* pSrcQ, float* pH, Complex* pDst, int count)
    {
        int i = 0;
        float* pDestFloat = (float*)pDst;
        if (Vector128.IsHardwareAccelerated && count >= 8)
        {
            int limit = count - 8;
            for (; i <= limit; i += 8)
            {
                Vector128<short> sI = Vector128.Load(pSrcI + i);
                Vector128<short> sQ = Vector128.Load(pSrcQ + i);

                var (iIL, iIH) = Vector128.Widen(sI);
                var (iQL, iQH) = Vector128.Widen(sQ);

                Vector128<float> fIL = Vector128.ConvertToSingle(iIL);
                Vector128<float> fIH = Vector128.ConvertToSingle(iIH);
                Vector128<float> fQL = Vector128.ConvertToSingle(iQL);
                Vector128<float> fQH = Vector128.ConvertToSingle(iQH);

                Vector128<float> hL = Vector128.Load(pH + i);
                Vector128<float> hH = Vector128.Load(pH + i + 4);

                Vector128<float> resIL = fIL * hL;
                Vector128<float> resQL = fQL * hL;
                Vector128<float> resIH = fIH * hH;
                Vector128<float> resQH = fQH * hH;

                Vector128<float> packL1 = Sse.UnpackLow(resIL, resQL);
                Vector128<float> packL2 = Sse.UnpackHigh(resIL, resQL);
                Vector128<float> packH1 = Sse.UnpackLow(resIH, resQH);
                Vector128<float> packH2 = Sse.UnpackHigh(resIH, resQH);

                Vector128.Store(packL1, pDestFloat + (i * 2));
                Vector128.Store(packL2, pDestFloat + (i * 2) + 4);
                Vector128.Store(packH1, pDestFloat + (i * 2) + 8);
                Vector128.Store(packH2, pDestFloat + (i * 2) + 12);
            }
        }
        for (; i < count; i++)
        {
            pDst[i].X = (float)pSrcI[i] * pH[i];
            pDst[i].Y = (float)pSrcQ[i] * pH[i];
        }
    }
}
