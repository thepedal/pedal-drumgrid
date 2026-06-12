using System;
using System.IO;

namespace PedalDrumGrid
{
    // Encodes a WaveSnapshot (float L/R, ~±1.0) -> 16-bit PCM WAV bytes for
    // embedding in a .pdrumgrid.xml kit. 16-bit is the standard sample format
    // and keeps embedded kits compact; the snapshot floats came from
    // GetDataAsFloat / WavReader so this is the usual sample round-trip.
    internal static class WavWriter
    {
        public static byte[] Write(WaveSnapshot snap)
        {
            if (snap == null || snap.Length <= 0) return null;
            int channels = snap.Stereo ? 2 : 1;
            int frames   = snap.Length;
            int rate     = snap.SampleRate > 0 ? snap.SampleRate : 44100;
            const int bits = 16;
            int blockAlign = channels * (bits / 8);
            int dataBytes  = frames * blockAlign;

            using var ms = new MemoryStream(44 + dataBytes);
            using var bw = new BinaryWriter(ms);

            // RIFF header
            bw.Write(new[] { 'R', 'I', 'F', 'F' });
            bw.Write(36 + dataBytes);
            bw.Write(new[] { 'W', 'A', 'V', 'E' });

            // fmt chunk (PCM)
            bw.Write(new[] { 'f', 'm', 't', ' ' });
            bw.Write(16);                          // fmt chunk size
            bw.Write((ushort)1);                   // PCM
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
                WriteSample16(bw, snap.DataL[f]);
                if (channels == 2)
                    WriteSample16(bw, snap.DataR != null ? snap.DataR[f] : snap.DataL[f]);
            }

            bw.Flush();
            return ms.ToArray();
        }

        static void WriteSample16(BinaryWriter bw, float f)
        {
            int v = (int)MathF.Round(f * 32767f);
            if (v >  32767) v =  32767;
            if (v < -32768) v = -32768;
            bw.Write((short)v);
        }
    }
}
