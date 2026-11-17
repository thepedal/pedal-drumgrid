using System;
using System.IO;
using System.Xml.Linq;
using BuzzGUI.Interfaces;

namespace PedalDrumGrid
{
    // Snapshot of decoded sample data for one lane (Tracker/M1 WaveSnapshot,
    // simplified — no head/tail pad since linear interp doesn't look behind).
    public sealed class WaveSnapshot
    {
        public float[] DataL;
        public float[] DataR;      // null if mono
        public bool    Stereo;
        public int     Length;
        public int     RootMidi;   // linear MIDI 0..127
        public int     SampleRate;
        public int     SourceBits  = 16;     // native bit depth (8/16/24/32); preserved on embed
        public bool    SourceFloat = false;  // true = IEEE float (32-bit)
    }

    // Lane source description.
    public enum LaneSourceKind { None, Wavetable, File, Embedded }

    public sealed class LaneSource
    {
        public LaneSourceKind Kind;
        public string Name;          // display name (sample/lane label)
        public int    WaveIndex;     // for Wavetable
        public string FilePath;      // for File (already resolved to absolute)
        public byte[] EmbeddedWav;   // for Embedded (WAV bytes); null while waiting for a shared sample
        public byte[] PendingHash;   // v1.8.0: SHA-256 of a shared sample not yet resolved (EmbeddedWav null)
        byte[] _hashOf, _hash;       // SHA-256 cache, valid while EmbeddedWav is the same array

        // SHA-256 of this lane's embedded audio (or of the shared sample it waits for).
        public byte[] Sha256()
        {
            if (EmbeddedWav == null) return PendingHash;
            if (!ReferenceEquals(_hashOf, EmbeddedWav))
            {
                _hash = SharedSamples.Hash(EmbeddedWav);
                _hashOf = EmbeddedWav;
            }
            return _hash;
        }
        public int    RootNote = 60;
        public int    ChokeGroup;
        public int    Velocity = 127; // per-lane default (0..127)
        public int    Pitch    = 0;   // per-lane default (semitones)
        public int    Out      = 0;   // per-lane output bus (1-based); 0 = unspecified
        public WaveSnapshot Snapshot; // built lazily
    }

    // Owns the per-lane sources, the .pdrumgrid.xml load/save, and the bridge
    // to the ReBuzz wavetable. Snapshots are cached. Wavetable lanes are rebuilt
    // when IWavetable.WaveChanged fires for their slot (v1.8.1): the new snapshot
    // is built on the notifying thread and swapped in, never nulled, so the
    // audio thread neither rebuilds nor clicks (M1 §2). Playing voices keep the
    // snapshot they started with.
    public sealed class DrumKit
    {
        readonly Func<IBuzz> _getBuzz;
        readonly LaneSource[] _lanes = new LaneSource[PedalDrumGridMachine.LANES];

        public string ActiveKitPath { get; private set; }
        public string Name { get; private set; } = "Init";

        public DrumKit(Func<IBuzz> getBuzz)
        {
            _getBuzz = getBuzz;
            for (int i = 0; i < _lanes.Length; i++) _lanes[i] = new LaneSource();
        }

        public int ChokeGroup(int lane) =>
            (uint)lane < _lanes.Length ? _lanes[lane].ChokeGroup : 0;

        public int LaneCount => _lanes.Length;

        public void SetActiveKitPath(string path) => ActiveKitPath = path;

        // ---- Wavetable change hook (v1.8.1) --------------------------------
        // Why: when a .bmxml song loads, ReBuzz creates the wavetable slots and
        // their layers (with SampleCount set) BEFORE it initialises machines, but
        // allocates and fills the sample data AFTER (BMXMLFile.LoadSongData:
        // Load Wavetable, then Load Machines / InitMachines, then Load Waves).
        // A wavetable lane built from MachineState at init therefore read a
        // full-length buffer of silence (GetDataAsFloat returns early while the
        // layer has no data) and cached it, so the lane never sounded until it was
        // reassigned. Load Waves calls wave.Invalidate() per layer, which raises
        // IWavetable.WaveChanged(index); rebuilding the lane there fixes the load
        // and also picks up a wave replaced or edited later.
        // The handler holds the kit weakly and unsubscribes itself once the kit
        // is gone (machines have no teardown hook).
        sealed class WaveHook
        {
            readonly WeakReference<DrumKit> _kit;
            readonly IWavetable _wt;
            public WaveHook(DrumKit kit, IWavetable wt) { _kit = new WeakReference<DrumKit>(kit); _wt = wt; }
            public void OnWaveChanged(int index)
            {
                if (_kit.TryGetTarget(out var kit)) kit.OnWavetableWaveChanged(index);
                else try { _wt.WaveChanged -= OnWaveChanged; } catch { }
            }
        }
        IWavetable _hookedWt;
        WaveHook _hook;

        // Idempotent; call from load/UI paths (never Work). Re-hooks if the song's
        // wavetable object has changed.
        public void EnsureWavetableHook()
        {
            try
            {
                var wt = _getBuzz?.Invoke()?.Song?.Wavetable;
                if (wt == null || ReferenceEquals(wt, _hookedWt)) return;
                if (_hookedWt != null && _hook != null)
                    try { _hookedWt.WaveChanged -= _hook.OnWaveChanged; } catch { }
                _hook = new WaveHook(this, wt);
                wt.WaveChanged += _hook.OnWaveChanged;
                _hookedWt = wt;
            }
            catch { }
        }

        // index = 0-based wavetable slot (lanes store it 1-based).
        void OnWavetableWaveChanged(int index)
        {
            for (int i = 0; i < _lanes.Length; i++)
            {
                var ls = _lanes[i];
                if (ls.Kind != LaneSourceKind.Wavetable || ls.WaveIndex - 1 != index) continue;
                WaveSnapshot snap = null;
                try { snap = BuildFromWavetable(ls.WaveIndex); } catch { }
                ls.Snapshot = snap;   // null (wave removed) falls back to the lazy build
            }
        }

        // ---- GUI accessors -------------------------------------------------
        public LaneSourceKind GetKind(int lane) =>
            (uint)lane < _lanes.Length ? _lanes[lane].Kind : LaneSourceKind.None;

        // 1-based wavetable slot for a Wavetable lane, else 0.
        public int GetWaveIndex(int lane) =>
            (uint)lane < _lanes.Length && _lanes[lane].Kind == LaneSourceKind.Wavetable
                ? _lanes[lane].WaveIndex : 0;

        // Human-readable per-lane source for a status label.
        public string LaneDisplay(int lane)
        {
            if ((uint)lane >= _lanes.Length) return "(empty)";
            var ls = _lanes[lane];
            string detail;
            switch (ls.Kind)
            {
                case LaneSourceKind.Wavetable: detail = "WT " + ls.WaveIndex; break;
                case LaneSourceKind.File:      detail = "file: " + System.IO.Path.GetFileName(ls.FilePath ?? ""); break;
                case LaneSourceKind.Embedded:
                    detail = ls.EmbeddedWav != null
                        ? "embedded " + ls.EmbeddedWav.Length / 1024 + " KB"
                        : "\u26A0 shared sample missing (its holder DrumGrid is not in the song)";
                    break;
                default: return "(empty)";
            }
            return string.IsNullOrEmpty(ls.Name) ? detail : ls.Name + " — " + detail;
        }

        // Concise sample-rate / bit-depth tag for the GUI, e.g. "44.1k/24",
        // "48k/16", "44.1k/32f" (f = float). Empty for an unassigned lane. Reads
        // the (cached) snapshot, so it reflects the source's native format.
        public string LaneFormat(int lane)
        {
            if ((uint)lane >= _lanes.Length || _lanes[lane].Kind == LaneSourceKind.None) return "";
            var snap = GetSnapshot(lane);
            if (snap == null) return "";
            string bits = snap.SourceFloat ? "32f" : snap.SourceBits.ToString();
            return RateTag(snap.SampleRate) + "/" + bits;
        }

        static string RateTag(int hz)
        {
            if (hz <= 0) return "?";
            double k = hz / 1000.0;
            return (k == System.Math.Floor(k) ? k.ToString("0") : k.ToString("0.0")) + "k";
        }

        // ---- Per-lane velocity/pitch defaults (synced with the machine) -----
        public int GetLaneVelocity(int lane) =>
            (uint)lane < _lanes.Length ? _lanes[lane].Velocity : 127;
        public int GetLanePitch(int lane) =>
            (uint)lane < _lanes.Length ? _lanes[lane].Pitch : 0;
        public void SetLaneDefaults(int lane, int velocity, int pitch)
        {
            if ((uint)lane >= _lanes.Length) return;
            _lanes[lane].Velocity = velocity;
            _lanes[lane].Pitch = pitch;
        }

        // Per-lane output bus stored in the kit (1-based; 0 = the kit doesn't
        // specify one, e.g. a pre-v1.5 file). The machine pushes its routing in
        // before save and pulls it back after load.
        public int GetLaneOut(int lane) =>
            (uint)lane < _lanes.Length ? _lanes[lane].Out : 0;
        public void SetLaneOut(int lane, int outBus)
        {
            if ((uint)lane < _lanes.Length) _lanes[lane].Out = outBus;
        }

        // ---- Lane-state persistence (MachineState v3 writes names) ---------
        // On-disk lane kind for a shared embedded sample (state v8+). In memory
        // such a lane is Kind = Embedded with its bytes from SharedSamples.
        public const byte WIRE_EMBEDDED_REF = 4;

        // storeFull(hash) decides, per embedded lane, whether this machine writes
        // the audio (true) or only its hash (false). null = always write audio.
        public void WriteLanes(System.IO.BinaryWriter bw, Func<byte[], bool> storeFull = null)
        {
            bw.Write((byte)_lanes.Length);
            foreach (var ls in _lanes)
            {
                bool asRef = false;
                if (ls.Kind == LaneSourceKind.Embedded)
                {
                    var h = ls.Sha256();
                    if (ls.EmbeddedWav == null) asRef = h != null;           // unresolved: keep the reference
                    else if (storeFull != null && h != null) asRef = !storeFull(h);
                }
                bw.Write(asRef ? WIRE_EMBEDDED_REF : (byte)ls.Kind);
                WriteStr(bw, ls.Name);                // v3+: per-lane name
                bw.Write(ls.ChokeGroup);
                if (asRef)
                {
                    bw.Write(ls.RootNote);
                    bw.Write(ls.Sha256());                // 32 bytes
                    continue;
                }
                switch (ls.Kind)
                {
                    case LaneSourceKind.Wavetable:
                        bw.Write(ls.WaveIndex);
                        break;
                    case LaneSourceKind.File:
                        bw.Write(ls.RootNote);
                        WriteStr(bw, ls.FilePath);
                        break;
                    case LaneSourceKind.Embedded:
                        // Carry the audio so an embedded-kit song is self-contained.
                        bw.Write(ls.RootNote);
                        int wlen = ls.EmbeddedWav?.Length ?? 0;
                        bw.Write(wlen);                       // int32 length
                        if (wlen > 0) bw.Write(ls.EmbeddedWav);
                        break;
                }
            }
        }

        static void WriteStr(System.IO.BinaryWriter bw, string s)
        {
            var b = System.Text.Encoding.UTF8.GetBytes(s ?? "");
            bw.Write((ushort)b.Length);
            bw.Write(b);
        }
        static string ReadStr(System.IO.BinaryReader br)
        {
            int len = br.ReadUInt16();
            return System.Text.Encoding.UTF8.GetString(br.ReadBytes(len));
        }

        // withNames = true for MachineState v3+, false for v2 (no name field).
        public void ReadLanes(System.IO.BinaryReader br, bool withNames)
        {
            int n = br.ReadByte();
            for (int i = 0; i < n; i++)
            {
                byte wire = br.ReadByte();
                var kind = (LaneSourceKind)wire;
                string name = withNames ? ReadStr(br) : null;
                int choke = br.ReadInt32();
                int waveIndex = 0, rootNote = 60;
                string path = null;
                byte[] wav = null, pending = null;
                if (wire == WIRE_EMBEDDED_REF)
                {
                    kind = LaneSourceKind.Embedded;
                    rootNote = br.ReadInt32();
                    pending = br.ReadBytes(32);
                }
                else if (kind == LaneSourceKind.Wavetable)
                {
                    waveIndex = br.ReadInt32();
                }
                else if (kind == LaneSourceKind.File)
                {
                    rootNote = br.ReadInt32();
                    path = ReadStr(br);
                }
                else if (kind == LaneSourceKind.Embedded)
                {
                    rootNote = br.ReadInt32();
                    int wlen = br.ReadInt32();
                    if (wlen > 0) wav = br.ReadBytes(wlen);
                }
                if (i >= _lanes.Length) continue;
                var ls = _lanes[i];
                ls.Kind = kind; ls.Name = string.IsNullOrEmpty(name) ? null : name;
                ls.ChokeGroup = choke;
                ls.WaveIndex = waveIndex; ls.RootNote = rootNote;
                ls.FilePath = path; ls.EmbeddedWav = wav;
                ls.PendingHash = pending;
                ls.Snapshot = null;     // rebuild lazily
            }
            _rev++;
        }

        public void AssignWavetableLane(int lane, int waveIndex)
        {
            if ((uint)lane >= _lanes.Length) return;
            var ls = _lanes[lane];
            _rev++;                 // lane source changed: kit file may no longer match
            EnsureWavetableHook();
            if (waveIndex <= 0) { ls.Kind = LaneSourceKind.None; ls.Name = null; ls.Snapshot = null; return; }
            ls.Kind = LaneSourceKind.Wavetable;
            ls.WaveIndex = waveIndex;
            ls.Name = ResolveWaveName(waveIndex);   // grab the wavetable wave's name
            ls.Snapshot = null;     // rebuild on next access
        }

        string ResolveWaveName(int waveIndex)
        {
            try
            {
                var waves = _getBuzz?.Invoke()?.Song?.Wavetable?.Waves;
                int slot = waveIndex - 1;
                if (waves != null && slot >= 0 && slot < waves.Count)
                {
                    var w = waves[slot];
                    if (w != null && !string.IsNullOrEmpty(w.Name)) return w.Name;
                }
            }
            catch { }
            return null;
        }

        public string GetLaneName(int lane) =>
            (uint)lane < _lanes.Length ? (_lanes[lane].Name ?? "") : "";

        public void SetLaneName(int lane, string name)
        {
            if ((uint)lane < _lanes.Length) _lanes[lane].Name = name;
        }

        // Returns a ready snapshot for the lane, building it lazily. Called
        // from Work() — but the FIRST build (which allocates + copies sample
        // data) is not something you want on the audio thread for large waves
        // (M1 §2). For drum one-shots it's small; for safety, pre-build kit
        // snapshots in the GUI/load path and treat this as cache-hit-only.
        public WaveSnapshot GetSnapshot(int lane)
        {
            if ((uint)lane >= _lanes.Length) return null;
            var ls = _lanes[lane];
            if (ls.Snapshot != null) return ls.Snapshot;
            ls.Snapshot = Build(ls);
            return ls.Snapshot;
        }

        // Build snapshots for every lane up front (call on the UI thread after
        // load) so the first hit doesn't decode/allocate on the audio thread.
        // Also publishes this kit's embedded samples to SharedSamples and takes
        // any shared samples its lanes are waiting for (v1.8.0).
        public void PrebuildAll()
        {
            EnsureWavetableHook();
            ResolvePending();
            for (int i = 0; i < _lanes.Length; i++)
            {
                var ls = _lanes[i];
                if (ls.Kind != LaneSourceKind.None && ls.Snapshot == null)
                    ls.Snapshot = Build(ls);
            }
            var items = new System.Collections.Generic.List<(byte[], byte[])>();
            foreach (var ls in _lanes)
                if (ls.Kind == LaneSourceKind.Embedded && ls.EmbeddedWav != null)
                    items.Add((ls.Sha256(), ls.EmbeddedWav));
            if (items.Count > 0) SharedSamples.Publish(items);
        }

        // Fill lanes waiting for a shared sample from SharedSamples. Returns true
        // when no lane is left waiting; otherwise registers to be woken later.
        // Called on load paths (UI/load thread), never from Work().
        public bool ResolvePending()
        {
            bool waiting = false;
            foreach (var ls in _lanes)
            {
                if (ls.Kind != LaneSourceKind.Embedded || ls.EmbeddedWav != null || ls.PendingHash == null)
                    continue;
                var wav = SharedSamples.TryGet(ls.PendingHash);
                if (wav == null) { waiting = true; continue; }
                ls.EmbeddedWav = wav;
                ls.PendingHash = null;
                ls.Snapshot = Build(ls);           // decode here, off the audio thread
                _rev++;                            // lane changed: kit-file match must be re-checked
            }
            if (waiting) SharedSamples.Wait(this);
            return !waiting;
        }

        // Hashes of the embedded samples this kit actually holds (bytes present).
        public System.Collections.Generic.HashSet<string> HeldSampleKeys()
        {
            var set = new System.Collections.Generic.HashSet<string>();
            foreach (var ls in _lanes)
                if (ls.Kind == LaneSourceKind.Embedded && ls.EmbeddedWav != null)
                    set.Add(SharedSamples.Key(ls.Sha256()));
            return set;
        }

        public byte[] LaneSha256(int lane) => (uint)lane < _lanes.Length ? _lanes[lane].Sha256() : null;
        public bool LaneHasAudio(int lane) => (uint)lane < _lanes.Length && _lanes[lane].EmbeddedWav != null;

        // Lanes still waiting for a shared sample.
        public int MissingSharedCount()
        {
            int n = 0;
            foreach (var ls in _lanes)
                if (ls.Kind == LaneSourceKind.Embedded && ls.EmbeddedWav == null && ls.PendingHash != null) n++;
            return n;
        }

        WaveSnapshot Build(LaneSource ls)
        {
            try
            {
                switch (ls.Kind)
                {
                    case LaneSourceKind.Wavetable: return BuildFromWavetable(ls.WaveIndex);
                    case LaneSourceKind.File:      return BuildFromFile(ls.FilePath, ls.RootNote);
                    case LaneSourceKind.Embedded:
                        return ls.EmbeddedWav != null ? WavReader.Read(ls.EmbeddedWav, ls.RootNote) : null;
                    default: return null;
                }
            }
            catch { return null; }
        }

        // ---- wavetable read (Tracker §7.1 GetDataAsFloat, §7.2 RootNote) ----
        // NOTE: the wavetable ENUMERATION path (Song.Wavetable.Waves[index],
        // IWave.Layers[0]) is the one spot the project notes don't pin down by
        // member name — verify these against BuzzGUI.Interfaces when wiring up.
        WaveSnapshot BuildFromWavetable(int waveIndex)
        {
            var buzz = _getBuzz?.Invoke();
            var wt = buzz?.Song?.Wavetable;
            if (wt == null) return null;

            // waveIndex is 1-based in the pattern/picker; wavetable slots 0-based.
            int slot = waveIndex - 1;
            if (slot < 0) return null;
            var waves = wt.Waves;
            if (waves == null || slot >= waves.Count) return null;
            var wave = waves[slot];
            if (wave == null || wave.Layers == null || wave.Layers.Count == 0) return null;

            var layer = wave.Layers[0];
            int len = layer.SampleCount;
            if (len <= 0) return null;

            bool stereo = (wave.Flags & WaveFlags.Stereo) != 0;   // Flags is on IWave, not IWaveLayer
            var (wbits, wfloat) = WavetableFormat(layer, wave);
            var snap = new WaveSnapshot
            {
                Length     = len,
                Stereo     = stereo,
                SampleRate = layer.SampleRate,
                RootMidi   = BuzzByteToMidi(layer.RootNote),
                DataL      = new float[len + 1],
                DataR      = stereo ? new float[len + 1] : null,
                SourceBits = wbits,
                SourceFloat = wfloat,
            };
            layer.GetDataAsFloat(snap.DataL, 0, 1, 0, 0, len);
            if (stereo) layer.GetDataAsFloat(snap.DataR, 0, 1, 1, 0, len);
            return snap;
        }

        // The wavetable layer's native storage format (Tracker §7.3 WaveFormat:
        // Int16 / Float32 / Int32 / Int24), read by reflection so there's no hard
        // dependency on a member name across previews. Falls back to 32-bit float
        // — lossless w.r.t. GetDataAsFloat — so a wavetable lane is never silently
        // downconverted when the format can't be determined.
        static (int bits, bool isFloat) WavetableFormat(object layer, object wave)
        {
            foreach (var o in new[] { layer, wave })
            {
                if (o == null) continue;
                object v = null;
                try { v = o.GetType().GetProperty("Format")?.GetValue(o); } catch { }
                if (v == null) continue;
                switch (v.ToString())
                {
                    case "Int16":   return (16, false);
                    case "Int24":   return (24, false);
                    case "Int32":   return (32, false);
                    case "Float32": return (32, true);
                }
            }
            return (32, true);
        }

        // ---- file read (basic PCM WAV) --------------------------------------
        // Minimal 16/24/32-bit PCM + float WAV reader so a kit referencing
        // loose .wav files works without importing into the wavetable. For
        // exotic formats, use kind="wavetable" instead.
        WaveSnapshot BuildFromFile(string path, int rootNote)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            return WavReader.Read(path, rootNote);
        }

        static int BuzzByteToMidi(int b)   // M1 §2.1
        {
            int oct  = (b >> 4);
            int semi = (b & 0xF) - 1;
            if (semi < 0) semi = 0;
            int m = (oct + 1) * 12 + semi;
            return m < 0 ? 0 : (m > 127 ? 127 : m);
        }

        // =================================================================
        //  .pdrumgrid.xml  (schema in README)
        // =================================================================
        public void LoadFromFile(string path)
        {
            try
            {
                var parsed = ParseKitFile(path, out string kitName);
                if (parsed == null) return;
                Name = kitName;
                for (int i = 0; i < _lanes.Length; i++) _lanes[i] = parsed[i];
                ActiveKitPath = Path.GetFullPath(path);
                _rev++;
                PrebuildAll();            // decode embedded audio off the audio thread
            }
            catch { /* leave kit as-is on parse failure */ }
        }

        // Parse a .pdrumgrid.xml into a fresh lane array without touching the
        // live kit. Shared by LoadFromFile and MatchesKitFile (v1.7.0).
        // Returns null if the file isn't a DrumGrid kit.
        LaneSource[] ParseKitFile(string path, out string kitName)
        {
            kitName = null;
            var doc = XDocument.Load(path);
            var root = doc.Root;
            if (root == null || root.Name != "PedalDrumGrid") return null;
            kitName = (string)root.Attribute("name") ?? Path.GetFileNameWithoutExtension(path);
            string baseDir = Path.GetDirectoryName(Path.GetFullPath(path));

            var lanes = new LaneSource[_lanes.Length];
            for (int i = 0; i < lanes.Length; i++) lanes[i] = new LaneSource();

            foreach (var laneEl in root.Elements("Lane"))
            {
                int idx = (int?)laneEl.Attribute("index") ?? -1;
                if (idx < 0 || idx >= lanes.Length) continue;
                var ls = lanes[idx];
                ls.Name = (string)laneEl.Attribute("name");

                var sample = laneEl.Element("Sample");
                if (sample != null)
                {
                    string kind = (string)sample.Attribute("kind") ?? "file";
                    ls.RootNote = (int?)sample.Attribute("rootNote") ?? 60;
                    switch (kind)
                    {
                        case "embedded":
                            ls.Kind = LaneSourceKind.Embedded;
                            try { ls.EmbeddedWav = Convert.FromBase64String(sample.Value.Trim()); }
                            catch { ls.Kind = LaneSourceKind.None; }
                            break;
                        case "wavetable":
                            ls.Kind = LaneSourceKind.Wavetable;
                            ls.WaveIndex = (int?)sample.Attribute("waveIndex") ?? 0;
                            break;
                        default: // "file"
                            ls.Kind = LaneSourceKind.File;
                            string rel = (string)sample.Attribute("path") ?? "";
                            ls.FilePath = Path.IsPathRooted(rel) ? rel : Path.Combine(baseDir, rel);
                            if (string.IsNullOrEmpty(ls.Name))
                                ls.Name = Path.GetFileNameWithoutExtension(ls.FilePath);
                            break;
                    }
                }

                var def = laneEl.Element("Defaults");
                if (def != null)
                {
                    ls.Velocity   = (int?)def.Attribute("velocity") ?? 127;
                    ls.Pitch      = (int?)def.Attribute("pitch") ?? 0;
                    ls.ChokeGroup = (int?)def.Attribute("chokeGroup") ?? 0;
                    ls.Out        = (int?)def.Attribute("out") ?? 0;   // 0 if pre-v1.5
                }
            }
            return lanes;
        }

        // ---- Kit-file link (v1.7.0) ---------------------------------------
        // A linked song stores only the kit path, not the audio. That is safe
        // only while the live lanes are exactly what the kit file holds, so the
        // machine asks MatchesKitFile() before writing a linked state. The
        // answer is cached on (path, file length, write time, lane revision):
        // _rev bumps on every lane-source change, so a wave assigned after the
        // kit was loaded makes the next save embed again.
        int _rev;
        public int Revision => _rev;
        string _matchKey;
        bool   _matchResult;
        byte[] _matchHash;

        public bool MatchesKitFile() => MatchesKitFile(out _);

        public bool MatchesKitFile(out byte[] sha256)
        {
            sha256 = null;
            string path = ActiveKitPath;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                var fi = new FileInfo(path);
                string key = path + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks + "|" + _rev;
                if (key == _matchKey) { sha256 = _matchHash; return _matchResult; }

                bool ok = false;
                var parsed = ParseKitFile(path, out _);
                if (parsed != null)
                {
                    ok = true;
                    for (int i = 0; i < _lanes.Length && ok; i++)
                        ok = SameSource(_lanes[i], parsed[i]);
                }
                _matchHash = ok ? HashFile(path) : null;
                _matchResult = ok;
                _matchKey = key;
                sha256 = _matchHash;
                return ok;
            }
            catch { return false; }
        }

        static bool SameSource(LaneSource a, LaneSource b)
        {
            if (a.Kind != b.Kind) return false;
            if (a.ChokeGroup != b.ChokeGroup) return false;
            switch (a.Kind)
            {
                case LaneSourceKind.None:      return true;
                case LaneSourceKind.Wavetable: return a.WaveIndex == b.WaveIndex;
                case LaneSourceKind.File:
                    return a.RootNote == b.RootNote &&
                           string.Equals(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase);
                case LaneSourceKind.Embedded:
                    return a.RootNote == b.RootNote &&
                           (a.EmbeddedWav ?? Array.Empty<byte>()).AsSpan()
                               .SequenceEqual((b.EmbeddedWav ?? Array.Empty<byte>()).AsSpan());
            }
            return false;
        }

        public static byte[] HashFile(string path)
        {
            using var fs = File.OpenRead(path);
            return System.Security.Cryptography.SHA256.HashData(fs);
        }

        // Linked-state helpers: names ride in the song so a lane rename after
        // loading the kit doesn't force the audio back into the song.
        public void WriteLaneNames(BinaryWriter bw)
        {
            bw.Write((byte)_lanes.Length);
            foreach (var ls in _lanes) WriteStr(bw, ls.Name);
        }

        public static string[] ReadLaneNames(BinaryReader br)
        {
            int n = br.ReadByte();
            var names = new string[n];
            for (int i = 0; i < n; i++) names[i] = ReadStr(br);
            return names;
        }

        public void ApplyLaneNames(string[] names)
        {
            if (names == null) return;
            for (int i = 0; i < names.Length && i < _lanes.Length; i++)
                _lanes[i].Name = string.IsNullOrEmpty(names[i]) ? null : names[i];
        }

        // Empty every lane (linked kit file missing at load).
        public void ClearLanes()
        {
            for (int i = 0; i < _lanes.Length; i++) _lanes[i] = new LaneSource();
            _rev++;
        }

        public void SaveToFile(string path)
        {
            var root = new XElement("PedalDrumGrid",
                new XAttribute("version", 2),
                new XAttribute("name", Name ?? "Kit"));
            for (int i = 0; i < _lanes.Length; i++)
            {
                var ls = _lanes[i];
                if (ls.Kind == LaneSourceKind.None) continue;

                // Embed the actual audio: build the lane's snapshot and encode
                // it to a 16-bit WAV, base64 into the manifest. Makes the kit
                // self-contained (no external files / wavetable slots needed).
                XElement sample = null;
                var snap = GetSnapshot(i);
                if (snap != null)
                {
                    byte[] wav = WavWriter.Write(snap);
                    if (wav != null)
                        sample = new XElement("Sample",
                            new XAttribute("kind", "embedded"),
                            new XAttribute("rootNote", ls.RootNote),
                            Convert.ToBase64String(wav));
                }
                if (sample == null)   // couldn't resolve audio — fall back to a reference
                {
                    sample = ls.Kind == LaneSourceKind.Wavetable
                        ? new XElement("Sample", new XAttribute("kind", "wavetable"), new XAttribute("waveIndex", ls.WaveIndex))
                        : new XElement("Sample", new XAttribute("kind", "file"),
                            new XAttribute("path", ls.FilePath ?? ""), new XAttribute("rootNote", ls.RootNote));
                }

                var laneEl = new XElement("Lane", new XAttribute("index", i));
                if (!string.IsNullOrEmpty(ls.Name)) laneEl.Add(new XAttribute("name", ls.Name));
                laneEl.Add(sample);
                laneEl.Add(new XElement("Defaults",
                    new XAttribute("velocity", ls.Velocity),
                    new XAttribute("pitch", ls.Pitch),
                    new XAttribute("chokeGroup", ls.ChokeGroup),
                    new XAttribute("out", ls.Out)));
                root.Add(laneEl);
            }
            new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(path);
            ActiveKitPath = Path.GetFullPath(path);
        }
    }
}
