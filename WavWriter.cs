using System;
using System.IO;

namespace PedalDrumGrid
{
    // Encodes a WaveSnapshot (float L/R, ~±1.0) -> PCM/float WAV bytes for
    // embedding in a .pdrumgrid.xml kit. The snapshot carries its source's
    // native format (SourceBits / SourceFloat), and we write that back, so a
    // 24-bit or float source isn't silently downconverted to 16-bit. Scaling
    // mirrors WavReader (÷2^(bits-1)) so int formats round-trip losslessly
    // (32-bit int is limited by the float32 mantissa; float is verbatim).
    internal static class WavWriter
    {
        public static byte[] Write(WaveSnapshot snap)
        {
            if (snap == null || snap.Length <= 0) return null;

            bool isFloat = snap.SourceFloat;
            int  bits    = isFloat ? 32 : snap.SourceBits;
            if (!isFloat && bits != 8 && bits != 16 && bits != 24 && bits != 32)
                bits = 16;                               // sane fallback

            int    channels   = snap.Stereo ? 2 : 1;
            int    frames     = snap.Length;
            int    rate       = snap.SampleRate > 0 ? snap.SampleRate : 44100;
            int    blockAlign = channels * (bits / 8);
            int    dataBytes  = frames * blockAlign;
            ushort fmtTag     = (ushort)(isFloat ? 3 : 1);   // 3 = IEEE float, 1 = PCM

            using var ms = new MemoryStream(44 + dataBytes);
            using var bw = new BinaryWriter(ms);

            // RIFF header
            bw.Write(new[] { 'R', 'I', 'F', 'F' });
            bw.Write(36 + dataBytes);
            bw.Write(new[] { 'W', 'A', 'V', 'E' });

            // fmt chunk
            bw.Write(new[] { 'f', 'm', 't', ' ' });
            bw.Write(16);                          // fmt chunk size
            bw.Write(fmtTag);
            bw.Write((ushort)channels);
            bw.Write(rate);
            bw.Write(rate * blockAlign);           // byte rate
            bw.Write((ushort)blockAlign);
            bw.Write((ushort)bits);

            // data chunk
            bw.Write(new[] { 'd', 'a', 't', 'a' });
            bw.Write(dataBytes);

            for (int f = 0; f < frames; f++)
            {
                WriteSample(bw, snap.DataL[f], bits, isFloat);
                if (channels == 2)
                    WriteSample(bw, snap.DataR != null ? snap.DataR[f] : snap.DataL[f], bits, isFloat);
            }

            bw.Flush();
            return ms.ToArray();
        }

        static void WriteSample(BinaryWriter bw, float f, int bits, bool isFloat)
        {
            if (isFloat) { bw.Write(f); return; }            // 32-bit IEEE float, verbatim

            switch (bits)
            {
                case 8:                                       // unsigned PCM (reader: (d-128)/128)
                {
                    int v = (int)MathF.Round(f * 128f) + 128;
                    if (v < 0) v = 0; if (v > 255) v = 255;
                    bw.Write((byte)v);
                    break;
                }
                case 24:
                {
                    int v = (int)MathF.Round(f * 8388608f);
                    if (v >  8388607) v =  8388607;
                    if (v < -8388608) v = -8388608;
                    bw.Write((byte)(v & 0xFF));
                    bw.Write((byte)((v >> 8) & 0xFF));
                    bw.Write((byte)((v >> 16) & 0xFF));
                    break;
                }
                case 32:
                {
                    double d = Math.Round((double)f * 2147483648.0);
                    if (d >  2147483647.0) d =  2147483647.0;
                    if (d < -2147483648.0) d = -2147483648.0;
                    bw.Write((int)d);
                    break;
                }
                default:                                      // 16-bit (reader: v/32768)
                {
                    int v = (int)MathF.Round(f * 32768f);
                    if (v >  32767) v =  32767;
                    if (v < -32768) v = -32768;
                    bw.Write((short)v);
                    break;
                }
            }
        }
    }
}
