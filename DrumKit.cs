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
    }

    // Lane source description.
    public enum LaneSourceKind { None, Wavetable, File, Embedded }

    public sealed class LaneSource
    {
        public LaneSourceKind Kind;
        public string Name;          // display name (sample/lane label)
        public int    WaveIndex;     // for Wavetable
        public string FilePath;      // for File (already resolved to absolute)
        public byte[] EmbeddedWav;   // for Embedded (raw 16-bit PCM WAV bytes)
        public int    RootNote = 60;
        public int    ChokeGroup;
        public int    Velocity = 127; // per-lane default (0..127)
        public int    Pitch    = 0;   // per-lane default (semitones)
        public int    Out      = 0;   // per-lane output bus (1-based); 0 = unspecified
        public WaveSnapshot Snapshot; // built lazily
    }

    // Owns the per-lane sources, the .pdrumgrid.xml load/save, and the bridge
    // to the ReBuzz wavetable. Snapshots are cached for the lifetime of the kit
    // and NOT invalidated on IWave.PropertyChanged (M1 §2 — that path nukes the
    // cache and clicks; reload the machine to refresh a replaced wave).
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
                case LaneSourceKind.Embedded:  detail = "embedded " + (ls.EmbeddedWav?.Length ?? 0) / 1024 + " KB"; break;
                default: return "(empty)";
            }
            return string.IsNullOrEmpty(ls.Name) ? detail : ls.Name + " — " + detail;
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
        public void WriteLanes(System.IO.BinaryWriter bw)
        {
            bw.Write((byte)_lanes.Length);
            foreach (var ls in _lanes)
            {
                bw.Write((byte)ls.Kind);
                WriteStr(bw, ls.Name);                // v3+: per-lane name
                bw.Write(ls.ChokeGroup);
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
                var kind = (LaneSourceKind)br.ReadByte();
                string name = withNames ? ReadStr(br) : null;
                int choke = br.ReadInt32();
                int waveIndex = 0, rootNote = 60;
                string path = null;
                byte[] wav = null;
                if (kind == LaneSourceKind.Wavetable)
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
                ls.Snapshot = null;     // rebuild lazily
            }
        }

        public void AssignWavetableLane(int lane, int waveIndex)
        {
            if ((uint)lane >= _lanes.Length) return;
            var ls = _lanes[lane];
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
        public void PrebuildAll()
        {
            for (int i = 0; i < _lanes.Length; i++)
            {
                var ls = _lanes[i];
                if (ls.Kind != LaneSourceKind.None && ls.Snapshot == null)
                    ls.Snapshot = Build(ls);
            }
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
            var snap = new WaveSnapshot
            {
                Length     = len,
                Stereo     = stereo,
                SampleRate = layer.SampleRate,
                RootMidi   = BuzzByteToMidi(layer.RootNote),
                DataL      = new float[len + 1],
                DataR      = stereo ? new float[len + 1] : null,
            };
            layer.GetDataAsFloat(snap.DataL, 0, 1, 0, 0, len);
            if (stereo) layer.GetDataAsFloat(snap.DataR, 0, 1, 1, 0, len);
            return snap;
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
                var doc = XDocument.Load(path);
                var root = doc.Root;
                if (root == null || root.Name != "PedalDrumGrid") return;
                Name = (string)root.Attribute("name") ?? Path.GetFileNameWithoutExtension(path);
                string baseDir = Path.GetDirectoryName(Path.GetFullPath(path));

                foreach (var lane in _lanes)
                {
                    lane.Kind = LaneSourceKind.None; lane.Snapshot = null; lane.Name = null;
                    lane.EmbeddedWav = null; lane.Velocity = 127; lane.Pitch = 0; lane.ChokeGroup = 0; lane.Out = 0;
                }

                foreach (var laneEl in root.Elements("Lane"))
                {
                    int idx = (int?)laneEl.Attribute("index") ?? -1;
                    if (idx < 0 || idx >= _lanes.Length) continue;
                    var ls = _lanes[idx];
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

                    ls.Snapshot = null;   // rebuild lazily
                }
                ActiveKitPath = Path.GetFullPath(path);
                PrebuildAll();            // decode embedded audio off the audio thread
            }
            catch { /* leave kit as-is on parse failure */ }
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
