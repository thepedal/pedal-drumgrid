using System;
using System.Collections.Generic;

namespace PedalDrumGrid
{
    // Process-wide, content-addressed store of embedded lane audio (v1.8.0).
    //
    // A song saved by 1.8.0 stores each distinct embedded sample once: the
    // DrumGrid chosen as its holder writes the WAV bytes, every other DrumGrid
    // using the same sample writes only its SHA-256. On load, every kit
    // publishes the samples it holds here, and lanes that only have a hash take
    // the bytes from here. Machines load in any order, so a lane whose holder
    // hasn't loaded yet is left waiting and filled when the holder publishes.
    //
    // Entries are weak: the bytes stay alive only while some lane holds them,
    // so closing a song releases its samples. Waiting kits are weak too.
    public static class SharedSamples
    {
        static readonly object _lock = new object();
        static readonly Dictionary<string, WeakReference<byte[]>> _samples = new();
        static readonly List<WeakReference<DrumKit>> _waiting = new();

        public static string Key(byte[] sha256) => Convert.ToHexString(sha256);

        public static byte[] Hash(byte[] data) =>
            System.Security.Cryptography.SHA256.HashData(data);

        // Publish samples (hash, bytes). Wakes any kit waiting for one of them.
        public static void Publish(IEnumerable<(byte[] hash, byte[] wav)> items)
        {
            bool added = false;
            lock (_lock)
            {
                foreach (var (hash, wav) in items)
                {
                    if (hash == null || wav == null) continue;
                    string k = Key(hash);
                    if (_samples.TryGetValue(k, out var wr) && wr.TryGetTarget(out _)) continue;
                    _samples[k] = new WeakReference<byte[]>(wav);
                    added = true;
                }
            }
            if (added) WakeWaiting();
        }

        public static byte[] TryGet(byte[] sha256)
        {
            if (sha256 == null) return null;
            lock (_lock)
            {
                if (_samples.TryGetValue(Key(sha256), out var wr) && wr.TryGetTarget(out var wav))
                    return wav;
                return null;
            }
        }

        public static void Wait(DrumKit kit)
        {
            lock (_lock)
            {
                foreach (var w in _waiting)
                    if (w.TryGetTarget(out var k) && ReferenceEquals(k, kit)) return;
                _waiting.Add(new WeakReference<DrumKit>(kit));
            }
        }

        static void WakeWaiting()
        {
            List<DrumKit> kits = new();
            lock (_lock)
            {
                for (int i = _waiting.Count - 1; i >= 0; i--)
                {
                    if (_waiting[i].TryGetTarget(out var k)) kits.Add(k);
                    else _waiting.RemoveAt(i);
                }
            }
            // Outside the lock: resolving publishes in turn (re-entrant Publish).
            foreach (var k in kits)
            {
                if (k.ResolvePending())          // true = nothing left to wait for
                    lock (_lock) _waiting.RemoveAll(w => !w.TryGetTarget(out var x) || ReferenceEquals(x, k));
            }
        }
    }
}
