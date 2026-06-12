using System;
using System.IO;

namespace PedalDrumGrid
{
    // Compact PCM/float WAV reader -> WaveSnapshot. Handles 16/24/32-bit int
    // and 32-bit float, mono or stereo. Enough for loose drum one-shots; for
    // anything exotic, import into the ReBuzz wavetable and use kind="wavetable".
    internal static class WavReader
    {
        public static WaveSnapshot Read(string path, int rootNote)
        {
            using var fs = File.OpenRead(path);
            return Read(fs, rootNote);
        }

        public static WaveSnapshot Read(byte[] data, int rootNote)
        {
            using var ms = new MemoryStream(data);
            return Read(ms, rootNote);
        }

        public static WaveSnapshot Read(Stream fs, int rootNote)
        {
            using var br = new BinaryReader(fs);

            if (new string(br.ReadChars(4)) != "RIFF") return null;
            br.ReadInt32();                                  // riff size
            if (new string(br.ReadChars(4)) != "WAVE") return null;

            int channels = 1, sampleRate = 44100, bits = 16;
            ushort fmtTag = 1;
            byte[] data = null;

            while (fs.Position + 8 <= fs.Length)
            {
                string id = new string(br.ReadChars(4));
                int sz = br.ReadInt32();
                long next = fs.Position + sz + (sz & 1);     // chunks are word-aligned

                if (id == "fmt ")
                {
                    fmtTag    = br.ReadUInt16();
                    channels  = br.ReadUInt16();
                    sampleRate= br.ReadInt32();
                    br.ReadInt32();                           // byte rate
                    br.ReadUInt16();                          // block align
                    bits      = br.ReadUInt16();
                    if (fmtTag == 0xFFFE && sz >= 40)         // WAVE_FORMAT_EXTENSIBLE
                    {
                        br.ReadUInt16(); br.ReadUInt16(); br.ReadInt32();
                        fmtTag = br.ReadUInt16();             // real format in subformat GUID head
                    }
                }
                else if (id == "data")
                {
                    data = br.ReadBytes(sz);
                }
                fs.Position = next;
            }

            if (data == null || channels < 1) return null;

            int bytesPerSample = bits / 8;
            int frames = data.Length / (bytesPerSample * channels);
            if (frames <= 0) return null;

            bool stereo = channels >= 2;
            var snap = new WaveSnapshot
            {
                Length     = frames,
                Stereo     = stereo,
                SampleRate = sampleRate,
                RootMidi   = rootNote,
                DataL      = new float[frames + 1],
                DataR      = stereo ? new float[frames + 1] : null,
            };

            int p = 0;
            for (int f = 0; f < frames; f++)
            {
                for (int c = 0; c < channels; c++)
                {
                    float s = ReadSample(data, p, bits, fmtTag);
                    p += bytesPerSample;
                    if (c == 0) snap.DataL[f] = s;
                    else if (c == 1 && stereo) snap.DataR[f] = s;
                }
            }
            return snap;
        }

        static float ReadSample(byte[] d, int o, int bits, ushort fmtTag)
        {
            if (fmtTag == 3) // IEEE float
                return BitConverter.ToSingle(d, o);
            switch (bits)
            {
                case 16:
                    return (short)(d[o] | (d[o + 1] << 8)) / 32768f;
                case 24:
                    int v24 = d[o] | (d[o + 1] << 8) | (d[o + 2] << 16);
                    if ((v24 & 0x800000) != 0) v24 |= unchecked((int)0xFF000000);
                    return v24 / 8388608f;
                case 32:
                    int v32 = BitConverter.ToInt32(d, o);
                    return v32 / 2147483648f;
                case 8:
                    return (d[o] - 128) / 128f;
                default:
                    return 0f;
            }
        }
    }
}
