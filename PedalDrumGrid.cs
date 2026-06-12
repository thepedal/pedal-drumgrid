using System;
using System.Collections.Generic;
using BuzzGUI.Interfaces;
using Buzz.MachineInterface;

namespace PedalDrumGrid
{
    // ---------------------------------------------------------------------
    // Pedal DrumGrid — multi-out drum-pattern sampler.
    //   * One pattern TRACK per drum lane; the pattern editor IS the trigger
    //     grid (bool Trigger -> ParameterType.Switch -> "1" fires, Core §9).
    //   * Multi-out: out 0 = master sum, outs 1..LANES = per-lane dry
    //     (Tracker §12). OutputCount declared with full headroom (§12.2).
    //   * Simultaneous same-step hits recovered via the shape-tolerant
    //     pvalues reader (Core §14/§42, Tracker §16) — load-bearing.
    //   * Swing = per-step trigger delay reusing Chord's ratio math (§3,§11).
    // ---------------------------------------------------------------------
    [MachineDecl(
        Name        = "Pedal DrumGrid",
        ShortName   = "DrumGrid",
        Author      = "thepedal",
        MaxTracks   = PedalDrumGridMachine.LANES,
        OutputCount = PedalDrumGridMachine.LANES + 1)]   // +1 master (§12.2)
    public class PedalDrumGridMachine : IBuzzMachine
    {
        public const int LANES        = 16;
        public const int MAX_BLOCK    = 4096;            // generous scratch (§12.5)
        public const float OUT_SCALE  = 32768f;          // float ±1 -> Buzz ±32768 (Core §38)

        readonly IBuzzMachineHost host;
        IBuzz Buzz => host?.Machine?.Graph?.Buzz;

        // ---- per-lane voices + per-lane stereo scratch -------------------
        readonly Voice[]  _voices  = new Voice[LANES];
        readonly float[][] _trackL = new float[LANES][];
        readonly float[][] _trackR = new float[LANES][];

        // ---- per-lane pending step data (set by setters, consumed in Work)-
        readonly bool[] _hasTrig    = new bool[LANES];
        readonly bool[] _pendTrig   = new bool[LANES];
        readonly int[]  _pendVel    = new int[LANES];
        readonly int[]  _pendPitch  = new int[LANES];

        // ---- the loaded kit / wavetable bridge ---------------------------
        readonly DrumKit _kit;

        // ---- globals -----------------------------------------------------
        int  _swing;        // 0..100
        int  _swingPhase;   // 0/1 which step of the pair is delayed
        int  _humanize;     // 0..100
        int  _masterGain;   // 0..200, 100 = unity

        // ---- transport / row tracking ------------------------------------
        int  _prevSongPos = int.MinValue;
        bool _wasPlaying;
        long _workCalls;
        readonly Random _rng = new Random();

        public PedalDrumGridMachine(IBuzzMachineHost host)
        {
            this.host = host;
            _kit = new DrumKit(() => Buzz);
            for (int t = 0; t < LANES; t++)
            {
                _voices[t]  = new Voice();
                _trackL[t]  = new float[MAX_BLOCK];
                _trackR[t]  = new float[MAX_BLOCK];
                _pendVel[t] = 127;
            }
        }

        // ---- DIAGNOSTICS (Core §10 / Tracker §8) -------------------------
        //  Off for release. The guarded calls compile out under `const false`
        //  (dead-code elimination), so there's zero runtime cost; flip to true
        //  to re-enable the trigger/assign/fire trace in the debug console.
        const bool DBG = false;
        bool _dbgWorkLogged;
        void Dbg(string s) { try { Buzz?.DCWriteLine("[DrumGrid] " + s); } catch { } }

        // =================================================================
        //  PARAMETERS
        // =================================================================
        //  COLUMN LAYOUT in the pattern editor (left -> right):
        //    [ Trig 1 .. Trig 16 ]   global switches  -> the trigger grid
        //    [ Swing  Swing Phase  Humanize  Master Gain ]  other globals
        //    [ per track: Velocity, Pitch ]            track params, repeated
        //  (Per-lane wave assignment is GUI-only now, not a pattern column.)
        //
        //  Triggers MUST be globals, not track params: track params are laid
        //  out track-major (T0:all, T1:all, ...), so a track-param Trigger
        //  would interleave with Velocity/Pitch and could never form a single
        //  adjacent block. As 16 distinct globals they sit together at the far
        //  left AND can't collide in parametersChanged (it's keyed by param),
        //  so the trigger-collision recovery is only needed for Velocity/Pitch.
        // -----------------------------------------------------------------

        // ---- Trigger grid: 16 global switches, declared FIRST (leftmost). ---
        //  IsStateless -> pattern-only (no rack sliders, Core §25) and re-fires
        //  on consecutive steps (fast hats).
        [ParameterDecl(Name = "Trig 1",  IsStateless = true, Description = "Lane 1 hit (1 = trigger)")]  public bool Trig1  { get => false; set => SetTrig(0,  value); }
        [ParameterDecl(Name = "Trig 2",  IsStateless = true, Description = "Lane 2 hit (1 = trigger)")]  public bool Trig2  { get => false; set => SetTrig(1,  value); }
        [ParameterDecl(Name = "Trig 3",  IsStateless = true, Description = "Lane 3 hit (1 = trigger)")]  public bool Trig3  { get => false; set => SetTrig(2,  value); }
        [ParameterDecl(Name = "Trig 4",  IsStateless = true, Description = "Lane 4 hit (1 = trigger)")]  public bool Trig4  { get => false; set => SetTrig(3,  value); }
        [ParameterDecl(Name = "Trig 5",  IsStateless = true, Description = "Lane 5 hit (1 = trigger)")]  public bool Trig5  { get => false; set => SetTrig(4,  value); }
        [ParameterDecl(Name = "Trig 6",  IsStateless = true, Description = "Lane 6 hit (1 = trigger)")]  public bool Trig6  { get => false; set => SetTrig(5,  value); }
        [ParameterDecl(Name = "Trig 7",  IsStateless = true, Description = "Lane 7 hit (1 = trigger)")]  public bool Trig7  { get => false; set => SetTrig(6,  value); }
        [ParameterDecl(Name = "Trig 8",  IsStateless = true, Description = "Lane 8 hit (1 = trigger)")]  public bool Trig8  { get => false; set => SetTrig(7,  value); }
        [ParameterDecl(Name = "Trig 9",  IsStateless = true, Description = "Lane 9 hit (1 = trigger)")]  public bool Trig9  { get => false; set => SetTrig(8,  value); }
        [ParameterDecl(Name = "Trig 10", IsStateless = true, Description = "Lane 10 hit (1 = trigger)")] public bool Trig10 { get => false; set => SetTrig(9,  value); }
        [ParameterDecl(Name = "Trig 11", IsStateless = true, Description = "Lane 11 hit (1 = trigger)")] public bool Trig11 { get => false; set => SetTrig(10, value); }
        [ParameterDecl(Name = "Trig 12", IsStateless = true, Description = "Lane 12 hit (1 = trigger)")] public bool Trig12 { get => false; set => SetTrig(11, value); }
        [ParameterDecl(Name = "Trig 13", IsStateless = true, Description = "Lane 13 hit (1 = trigger)")] public bool Trig13 { get => false; set => SetTrig(12, value); }
        [ParameterDecl(Name = "Trig 14", IsStateless = true, Description = "Lane 14 hit (1 = trigger)")] public bool Trig14 { get => false; set => SetTrig(13, value); }
        [ParameterDecl(Name = "Trig 15", IsStateless = true, Description = "Lane 15 hit (1 = trigger)")] public bool Trig15 { get => false; set => SetTrig(14, value); }
        [ParameterDecl(Name = "Trig 16", IsStateless = true, Description = "Lane 16 hit (1 = trigger)")] public bool Trig16 { get => false; set => SetTrig(15, value); }

        void SetTrig(int lane, bool value)
        {
            if ((uint)lane >= LANES) return;
            if (DBG) Dbg($"SetTrig lane={lane} val={value}");
            _pendTrig[lane] = value;     // true = hit, explicit false = rest
            _hasTrig[lane]  = true;
            // No sibling poll needed — each Trig is its own global parameter.
        }

        // ---- Other globals (auto-persisted by ReBuzz, Core §39.3) -----------

        [ParameterDecl(Name = "Swing", MinValue = 0, MaxValue = 100, DefValue = 0,
            Description = "0 = straight, 100 ~ 2:1 shuffle")]
        public int Swing { get => _swing; set => _swing = value; }

        [ParameterDecl(Name = "Swing Phase", MinValue = 0, MaxValue = 1, DefValue = 0,
            Description = "Which step of the pair gets the delay")]
        public int SwingPhase { get => _swingPhase; set => _swingPhase = value; }

        [ParameterDecl(Name = "Humanize", MinValue = 0, MaxValue = 100, DefValue = 0,
            Description = "Timing jitter (non-cumulative)")]
        public int Humanize { get => _humanize; set => _humanize = value; }

        [ParameterDecl(Name = "Master Gain", MinValue = 0, MaxValue = 200, DefValue = 100,
            Description = "Master mix gain on out 0 (100 = unity)")]
        public int MasterGain { get => _masterGain; set => _masterGain = value; }

        // Per-lane wave assignment is NOT a parameter — it lives in the GUI
        // (kept off the pattern grid) and persists via MachineState. The kit is
        // the single source of truth for each lane's source (wavetable slot or
        // kit-file sample); the GUI calls AssignLaneWave to point a lane at a
        // wavetable slot.

        // Called from the GUI (UI thread). slot is 1-based wavetable index,
        // 0 = none.
        public void AssignLaneWave(int lane, int slot)
        {
            if ((uint)lane >= LANES) return;
            if (DBG) Dbg($"AssignLaneWave lane={lane} slot={slot}");
            _kit.AssignWavetableLane(lane, slot);
        }

        // Enumerate non-empty wavetable slots for the GUI's per-lane pickers.
        // 1-based index to match BuildFromWavetable / the old IsWaveNumber
        // convention. UI-thread only.
        public System.Collections.Generic.List<WaveEntry> GetWavetableEntries()
        {
            var list = new System.Collections.Generic.List<WaveEntry>
            {
                new WaveEntry(0, "— none —")
            };
            try
            {
                var waves = Buzz?.Song?.Wavetable?.Waves;
                if (waves != null)
                {
                    for (int i = 0; i < waves.Count; i++)
                    {
                        var w = waves[i];
                        if (w == null || w.Layers == null || w.Layers.Count == 0) continue;
                        string name = string.IsNullOrEmpty(w.Name) ? ("Wave " + (i + 1)) : w.Name;
                        list.Add(new WaveEntry(i + 1, $"{i + 1:00}: {name}"));
                    }
                }
            }
            catch { }
            return list;
        }

        // ---- Track parameters (group 2): per-lane Velocity + Pitch ----------
        //  Rendered to the right of the global block, grouped per track:
        //  [T0:Vel T0:Pitch][T1:Vel T1:Pitch]...  Stateful (held) so a value
        //  entered once carries to following hits on that lane until changed.
        //  Track index t == lane t == output slot t+1.

        [ParameterDecl(Name = "Velocity", MinValue = 0, MaxValue = 127, DefValue = 127,
            Description = "Per-lane velocity (0..127), held until changed")]
        public void SetVelocity(int value, int track)
        {
            if ((uint)track >= LANES) return;
            _pendVel[track] = value;
        }

        [ParameterDecl(Name = "Pitch", MinValue = 0, MaxValue = 96, DefValue = 48,
            Description = "Per-lane semitone offset, 48 = no shift (±48)")]
        public void SetPitch(int value, int track)
        {
            if ((uint)track >= LANES) return;
            _pendPitch[track] = value - 48;
        }

        // =================================================================
        //  NOTE on Velocity/Pitch collisions (Core §14)
        // =================================================================
        // Velocity & Pitch are track params; if two lanes change them on the
        // SAME row, parametersChanged keeps only the last writer and the other
        // lane keeps its previously-held value for that row. For HELD, sparsely
        // edited drum velocities this is imperceptible, so we accept it rather
        // than poll sibling pvalues. An earlier pvalue-poll recovery was removed
        // because, at load/track-create time, the int[256] pvalues array is all
        // 0 (not the NoValue sentinel) and the poll clobbered every sibling
        // lane's held velocity to 0 -> silent lanes. A lane is now silent only
        // if its velocity is explicitly set to 0. (If dense same-row multi-lane
        // accents ever matter, re-add recovery guarded on Buzz.Playing AND the
        // real TrackCount, or move Velocity to non-colliding per-lane globals.)

        // =================================================================
        //  WORK — multi-out (defining this overload sets MULTI_IO, §12.1)
        // =================================================================
        public bool Work(IList<Sample[]> output, int n, WorkModes mode)
        {
            _workCalls++;
            if (n <= 0) return false;
            if (n > MAX_BLOCK) n = MAX_BLOCK;

            if (DBG && !_dbgWorkLogged)
            {
                _dbgWorkLogged = true;
                var sb = new System.Text.StringBuilder("outputs connected: ");
                for (int c = 0; c < output.Count; c++) sb.Append(output[c] == null ? "." : c.ToString()).Append(' ');
                Dbg(sb.ToString());
            }

            CheckTransport();

            // New-row edge via PlayPosition (Tracker §2.3) — not PosInTick.
            int songPos = -1;
            try { songPos = Buzz?.Song?.PlayPosition ?? -1; } catch { }
            bool newRow = songPos != _prevSongPos;
            _prevSongPos = songPos;

            if (newRow) ApplyPendingTriggers(songPos);

            // Render each lane into its own scratch, advancing delays/voices.
            for (int t = 0; t < LANES; t++)
            {
                var v = _voices[t];
                v.Render(_trackL[t], _trackR[t], n, _kit.GetSnapshot(t), EngineRate());
            }

            // Publish out 0 = summed master (with master gain), outs 1..N dry.
            float mg = _masterGain / 100f;
            if (output[0] != null)
            {
                var o = output[0];
                for (int i = 0; i < n; i++)
                {
                    float l = 0f, r = 0f;
                    for (int t = 0; t < LANES; t++) { l += _trackL[t][i]; r += _trackR[t][i]; }
                    o[i].L = l * mg * OUT_SCALE;
                    o[i].R = r * mg * OUT_SCALE;
                }
            }
            for (int t = 0; t < LANES; t++)
            {
                var o = (t + 1 < output.Count) ? output[t + 1] : null;
                if (o == null) continue;                 // unconnected slot (§12.1)
                for (int i = 0; i < n; i++)
                {
                    o[i].L = _trackL[t][i] * OUT_SCALE;   // per-lane DRY (§12.3)
                    o[i].R = _trackR[t][i] * OUT_SCALE;
                }
            }
            return true;
        }

        // Apply this row's pending hits, scheduling swing delay on off-beats.
        void ApplyPendingTriggers(int songPos)
        {
            int spt = SamplesPerTick();
            for (int t = 0; t < LANES; t++)
            {
                if (!_hasTrig[t]) continue;
                _hasTrig[t] = false;
                if (!_pendTrig[t]) { continue; }         // explicit 0 = rest

                int delay = SwingDelaySamples(songPos, spt);
                float velGain = _pendVel[t] / 127f;
                int pitch = _pendPitch[t];

                var snap = _kit.GetSnapshot(t);
                if (DBG) Dbg($"fire lane={t} vel={_pendVel[t]} snap={(snap == null ? "NULL" : snap.Length + "smp")}");
                ChokeGroupCut(t);
                _voices[t].Trigger(velGain, pitch, delay, snap);
            }
        }

        // Chord §3/§11 ratio math, recast as an off-beat delay (README "Swing").
        int SwingDelaySamples(int songPos, int samplesPerTick)
        {
            if (_swing <= 0 || songPos < 0) return 0;
            bool offBeat = ((songPos & 1) == (_swingPhase & 1));
            if (!offBeat) return 0;
            double ratio   = 1.0 + _swing / 100.0;             // 1..2
            double longTks = 2.0 * ratio / (ratio + 1.0);      // 1..1.333 (per step pair)
            double delayT  = longTks - 1.0;                    // 0..0.333 tick
            int delay = (int)Math.Round(delayT * samplesPerTick);
            if (_humanize > 0)
            {
                int drift = (int)Math.Round(samplesPerTick * _humanize / 200.0);
                if (drift > 0) delay += _rng.Next(0, drift + 1);
            }
            if (delay < 0) delay = 0;
            return delay;
        }

        void ChokeGroupCut(int firingLane)
        {
            int g = _kit.ChokeGroup(firingLane);
            if (g == 0) return;
            for (int t = 0; t < LANES; t++)
                if (t != firingLane && _kit.ChokeGroup(t) == g)
                    _voices[t].Choke();
        }

        void CheckTransport()
        {
            bool now;
            try { now = Buzz?.Playing ?? false; } catch { now = _wasPlaying; }
            if (_wasPlaying && !now)
                for (int t = 0; t < LANES; t++) _voices[t].StopFade();   // Tracker §3
            _wasPlaying = now;
        }

        int SamplesPerTick()
        {
            try { return Math.Max(1, host?.MasterInfo?.SamplesPerTick ?? 4800); }
            catch { return 4800; }
        }
        int EngineRate()
        {
            try { return Math.Max(8000, host?.MasterInfo?.SamplesPerSec ?? 44100); }
            catch { return 44100; }
        }

        // =================================================================
        //  STATE PERSISTENCE (Core §39 framing)
        // =================================================================
        // v3 adds per-lane names. v2 (no names) and v1 (path only) still read.
        const uint MAGIC = 0x44475250u;   // "PRGD"
        const byte VERSION = 3;

        public byte[] MachineState
        {
            get
            {
                try
                {
                    using var ms = new System.IO.MemoryStream();
                    using var bw = new System.IO.BinaryWriter(ms);
                    bw.Write(MAGIC);
                    bw.Write(VERSION);
                    string kitPath = _kit.ActiveKitPath ?? "";
                    var pb = System.Text.Encoding.UTF8.GetBytes(kitPath);
                    bw.Write((ushort)pb.Length);
                    bw.Write(pb);
                    _kit.WriteLanes(bw);          // full per-lane source state
                    return ms.ToArray();
                }
                catch { return Array.Empty<byte>(); }
            }
            set
            {
                if (value == null || value.Length < 5) return;
                try
                {
                    using var ms = new System.IO.MemoryStream(value);
                    using var br = new System.IO.BinaryReader(ms);
                    if (br.ReadUInt32() != MAGIC) return;
                    byte v = br.ReadByte();
                    if (v < 1 || v > 3) return;            // unknown future version
                    int len = br.ReadUInt16();
                    string kitPath = System.Text.Encoding.UTF8.GetString(br.ReadBytes(len));
                    _kit.SetActiveKitPath(string.IsNullOrEmpty(kitPath) ? null : kitPath);
                    if (v >= 2)
                        _kit.ReadLanes(br, withNames: v >= 3);   // self-contained lane state
                    else if (!string.IsNullOrEmpty(kitPath))
                        _kit.LoadFromFile(kitPath);        // v1: reload from file
                    _kit.PrebuildAll();                    // decode off the audio thread
                }
                catch { /* corrupt — start fresh */ }
            }
        }

        // Expose to the GUI.
        public DrumKit Kit => _kit;
        public IBuzzMachineHost Host => host;

        // Kit load/save routed through the machine so per-lane velocity/pitch
        // (held in _pendVel/_pendPitch) round-trip with the kit file.
        public void LoadKit(string path)
        {
            _kit.LoadFromFile(path);
            for (int t = 0; t < LANES; t++)
            {
                _pendVel[t]   = _kit.GetLaneVelocity(t);
                _pendPitch[t] = _kit.GetLanePitch(t);
            }
        }

        public void SaveKit(string path)
        {
            for (int t = 0; t < LANES; t++)
                _kit.SetLaneDefaults(t, _pendVel[t], _pendPitch[t]);
            _kit.SaveToFile(path);
        }
    }

    // A wavetable slot option for the GUI per-lane pickers. ToString drives the
    // ComboBox display.
    public sealed class WaveEntry
    {
        public int Index { get; }       // 1-based wavetable slot, 0 = none
        public string Display { get; }
        public WaveEntry(int index, string display) { Index = index; Display = display; }
        public override string ToString() => Display;
    }
}
