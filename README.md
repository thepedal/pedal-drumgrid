# Pedal DrumGrid

A 16-lane multi-out drum-pattern sampler for ReBuzz. The trigger grid is 16
global `Trig` switches in the pattern editor (type `1` to fire a hit); each lane
has its own audio output for independent processing, plus per-lane Velocity and
two tracker-style **Command/Argument** effect columns (v1.1). Samples come from the ReBuzz wavetable (assigned per lane in the
GUI) or from a self-contained `.pdrumgrid.xml` kit with embedded audio.

Status: **v1.3** — multi-out triggering, swing, kit load/save with embedded
samples and per-lane names/velocity/base-tuning, and `MachineState` persistence
are all working in ReBuzz (built/tested on the user's install). The two effect
command columns (delay / retrigger / offset / reverse / pitch / cut) and per-row
modulation of an in-flight roll (pitch / offset / reverse / cut) are in and
tested. Follows the project's Core/Build conventions. The per-lane voice DSP is
deliberately simple (linear interpolation, basic anti-click); a future pass could
lift Tracker's interpolation table and deferred-trigger fade-cross (Tracker §4.2,
§10).

---

## Architecture decisions (and where they come from)

| Decision | Rationale (notes ref) |
|---|---|
| Trigger grid = **16 global switches** (`Trig 1..16`), one per lane | Track params are laid out track-major, so a track-param trigger interleaves with the per-track columns and can't form one adjacent block. As distinct globals the triggers sit together at the far left (Core §9 `bool`→Switch; §25 `IsStateless` = pattern-only) and **can't collide** in `parametersChanged`. Velocity + the Command/Argument columns stay track params → grouped per-track on the right. |
| Column order: `[Trig 1..16][Swing  Swing Phase  Humanize  Master Gain][per-track: Velocity  Command 1  Argument 1  Command 2  Argument 2]` | Globals render before the track section (Roster song-authoring facts). Triggers declared first → leftmost; wave assignment is GUI-only (not a column). The machine **auto-initialises to 16 pattern tracks** (UI-thread, one-shot, bump-up-only) so all per-lane columns are present out of the box — no manual "add track" step. |
| **Multi-out** via the `IList<Sample[]>` Work overload | Defining that overload sets `MULTI_IO` automatically (Tracker §12.1). Out 0 = master sum, outs 1..N = per-lane dry (Tracker §12.3). |
| `OutputCount = LANES + 1` declared up front (static headroom) | Auto-syncing `OutputCount` to track count crashes on song reload when a saved connection references a channel ≥ current count (Tracker §12.2). Unconnected slots are `null` and cost nothing. |
| **Velocity + Commands read from the firing lane's pvalue at trigger time** | The trigger is a global param consumed in `Work`; the track setters can land too late to affect the same-row hit (pattern cells looked like they did nothing). Fix: in `SetTrig`, read the firing lane's **own** Velocity and Command/Argument pvalues (Tracker §16.3) — the sequencer fills all row pvalues before any setter runs. Velocity is **held** (kept on `NoValue`); commands are **momentary** (None when `NoValue`). Guards: only while `Buzz.Playing`, and only for `lane < TrackCount` (beyond the real track count the `int[256]` slot is still `0`, not `NoValue`, and would clobber the held velocity to 0 → silent lane). Same-row multi-lane hits each read their own pvalue, so Core §14 collisions don't matter. |
| Per-lane **wave assignment is GUI-only**, persisted in `MachineState` | Kept off the pattern grid so the trigger block butts against the per-track section. The GUI's 16 per-lane pickers point each lane at a wavetable slot; the kit is the single source of truth and its full per-lane state (wavetable slot or file path) is serialised in `MachineState` v2 (Core §39), since non-parameter state isn't auto-persisted. |
| **Swing** = per-step trigger *delay*, not a step clock | DrumGrid doesn't own a step clock — triggers arrive from pattern rows. So swing delays off-beat steps using sub-tick scheduling (Tracker §7.7 note-delay machinery), reusing Chord's ratio math (Chord §3, §11) recast as a delay. See "Swing" below. |
| State (kit ref, swing, gains) framed `byte[]` `MachineState` | Magic+version+length-prefixed framing (Core §39.1). Wave-slot assignments persist as ordinary parameters; the kit *file reference* and any embedded-kit blob go in `MachineState`. |
| GUI base class = `FrameworkElement` (if custom-painted) | `UserControl`'s template silently overpaints `OnRender` (Core §26.7). The kit/swing panel is control-composed, so it can stay `UserControl`; the optional lane meters/grid preview must be `FrameworkElement`. |

### Open choices you may want to change
- **Lane count.** Scaffold uses `LANES = 16` (→ `OutputCount = 17`), matching
  Tracker's `MAX_VOICES`. Drop to 8 if you want a tighter grid.
- **Per-step columns.** 16 global `Trig` switches + per-lane `Velocity`
  (byte 0–127) and two `Command`/`Argument` effect columns (v1.1, see below).
  Per-row pitch is now command `05` (the dedicated Pitch column was removed).
  Parameter *order* isn't locked against shipped presets yet (Build §3.3).
- **Voicing per lane.** Scaffold = 1 voice/lane with deferred re-trigger
  fade-cross + choke groups (the drum-machine model). A small voice pool per
  lane (for overlapping tom tails) is a later option.

---

## `.pdrumgrid.xml` kit file

A kit bundles, per lane: a sample source and lane defaults (velocity, pitch,
choke group, root note). **Save Kit embeds the actual audio** (16-bit PCM WAV,
base64) so a kit is fully self-contained — no external files or wavetable slots
needed to share or reload it. Three source kinds are understood on load:

```xml
<?xml version="1.0" encoding="utf-8"?>
<PedalDrumGrid version="2" name="808 Core">
  <!-- embedded: what Save Kit writes — audio baked in (base64 16-bit WAV) -->
  <Lane index="0" name="Kick">
    <Sample kind="embedded" rootNote="60">UklGRiQAAABXQVZF…(base64)…</Sample>
    <Defaults velocity="127" pitch="0" chokeGroup="0" />
  </Lane>
  <!-- file: reference a wav on disk (relative to the .pdrumgrid.xml, then absolute) -->
  <Lane index="1" name="Snare">
    <Sample kind="file" path="samples/808/snare.wav" rootNote="60" />
    <Defaults velocity="110" pitch="0" chokeGroup="0" />
  </Lane>
  <!-- wavetable: reference a ReBuzz wavetable slot (1-based) -->
  <Lane index="2" name="CH">
    <Sample kind="wavetable" waveIndex="7" />
    <Defaults velocity="90" pitch="0" chokeGroup="1" />
  </Lane>
</PedalDrumGrid>
```

- `kind="embedded"` — base64 16-bit PCM WAV, decoded at load. The output of
  Save Kit (it encodes each lane's in-memory snapshot via `WavWriter`,
  whatever the lane's original source). Self-contained.
- `kind="file"` — PCM wav read at load (`WavReader`); for non-wav, import into
  the wavetable first and use `wavetable`.
- `kind="wavetable"` — `waveIndex` is a 1-based ReBuzz wavetable slot, read live
  via `GetDataAsFloat` (Tracker §7.1).
- `Defaults` round-trip per-lane **velocity** and **base tuning** (captured from
  `_pendVel`/`_basePitch` on save, re-applied on load). The base tuning is added
  to every hit on the lane; the per-row Pitch command (`05`) adjusts on top of it.
  Also **chokeGroup** (lanes sharing a non-zero group cut each other — hi-hats).
- The per-lane **`name`** is captured from the wavetable wave when you assign a
  lane (or the file name for `file` lanes), is editable in the GUI, and persists
  in both the kit file and `MachineState` (v3).
- Save Kit always **embeds**; it's non-destructive (the live lanes keep their
  wavetable/file links, only the file gets the baked audio).

Snapshots are pre-built on the UI thread after load (`PrebuildAll`) so the first
hit never decodes on the audio thread (M1 §2).

The kit's full per-lane state — including embedded audio — is also serialised in
`MachineState` v2, so a song that uses an embedded kit stays self-contained on
reload (no missing samples). Velocity/pitch stay with the track params in the
song (ReBuzz-persisted) to avoid duplicate persistence (PedalTracker §13.1); the
kit *file* is where velocity round-trips.

Per-lane wavetable assignment is done in the **GUI** (16 pickers, populated from
the current wavetable). Picking a slot points that lane at the wavetable; Save
Kit then bakes its audio into the manifest.

---

## Swing (reusing Pedal Chord's ratio math)

Chord computes a long/short split of a step pair with the invariant
`long + short == 2·Speed`, which keeps average tempo locked at every swing value
(Chord §3.1, §11.3). DrumGrid doesn't generate steps — the pattern does — so the
same ratio is applied as a **delay on the off-beat step**:

```
ratio = 1 + Swing/100                  // 0..100  ->  1.0..2.0
long  = 2 * ratio / (ratio + 1)        // in units of one step (tick), 1.0..1.333
delayTicks (off-beat step) = long - 1  // 0 at Swing 0, 1/3 tick at Swing 100
delaySamples = delayTicks * MasterInfo.SamplesPerTick
```

Off-beat is chosen by step parity from `Song.PlayPosition` (Tracker §2.3), with a
`Swing Phase` switch to flip which step of the pair is delayed (Chord's
`SwingPhaseVal`). The delay is realised with the sub-tick note-delay countdown
(Tracker §7.7): on an off-beat trigger, stage the hit with
`v.TriggerDelaySamples = delaySamples` and fire it inside `Work()` when the
countdown reaches 0. `Humanize` adds non-cumulative jitter on top, so it never
drifts tempo (Chord §3). Because it's a per-trigger delay rather than a step
clock, it's tempo-locked and independent of the Sub-Tick Timing setting by
construction (Chord §11.3) — no `SubTickInfo` read at all.

---

## Effect commands (v1.1)

Each track has **two** `Command`/`Argument` slots per row. Both apply to that
row's hit, processed slot&nbsp;1 then slot&nbsp;2; on a conflicting field (e.g.
two Offsets) the second slot wins, independent fields (e.g. Pitch + Delay)
combine. Commands are **momentary** — they affect only the row they're on
(None when the pattern cell is empty), unlike the **held** Velocity column.

| Code | Command | Argument |
|---|---|---|
| `00` | None | — |
| `01` | Delay | subticks to delay this hit |
| `02` | Retrigger | hi nibble = interval (subticks), lo nibble = length (ticks) |
| `03` | Offset | start point, `00`=start … `FF`=end |
| `04` | Reverse + offset | start point (plays backwards from there) |
| `05` | Pitch | signed semitones: `00`=none, `01..7F`=+1..+127, `80..FE`=−128..−2 (so −12 = `F4`) |
| `06` | Cut | subticks after which the hit is cut |

**Sub-tick unit.** Delay / Retrigger interval / Cut are measured in a **fixed**
`1/12`-of-a-tick grid (`SUBTICKS_PER_TICK = 12`), *independent* of the engine's
Sub-Tick-Timing setting, so the same argument behaves identically with it on or
off. The voice schedules them sample-accurately (`subSamples = SamplesPerTick /
12`); Retrigger length is `lengthTicks × SamplesPerTick`.

**Argument is a byte (`00..FE`).** `FF` (255) is the byte `NoValue` sentinel
(Core §9 ceiling), so it can't be entered; Offset/Reverse map `arg/255` so the
"`FF`=end" model still holds and the enterable max `FE` lands at ~99.6 %. The
**Command** column uses `ValueDescriptions` so it shows the names above and
auto-ranges to `0..6`.

The voice (`Voice.cs`) gained start-offset, reverse playback (`_dir`), a
scheduled-cut countdown, and sample-accurate retrigger (silent gaps preserved
when the sample is shorter than the interval).

**Steering a roll from later rows (v1.2–v1.3).** While a retrigger is in flight,
a command in either slot on a pass-over row modulates the roll, applied to the
*next* re-fire: **Pitch** (`05`), **Offset** (`03`) and **Reverse** (`04`) change
the character of each subsequent hit (arpeggios, sample-walks, backward rolls),
and **Cut** (`06`) ends the roll that many subticks later. The per-row value is
captured in the command **setters** (during setter delivery, not in `Work` — same
reason as the §2 velocity capture), and only the slot whose setter fired is
applied: Pitch/Offset/Reverse are *hold* values (kept until changed — an empty
cell sends no setter), whereas Cut is a one-shot, so re-applying a held Cut from
another slot's setter must be avoided or the roll would never end. Steps are
discrete (they land on the next retrigger boundary) and per-row in granularity.
Delay (`01`) and Retrigger (`02`) stay trigger-row-only.

---

| File | Contents |
|---|---|
| `PedalDrumGrid.csproj` | Fully compliant with Build §1.2 (net10, UseWPF, no pdb/deps, NoWarn) + deploy to `Gear\Generators` (Build §1.3). |
| `PedalDrumGrid.cs` | Machine: params (trigger grid + globals + per-track Velocity/Command/Argument), setters with collision recovery, per-row command capture + translation, multi-out `Work`, swing-as-delay, transport stop, `MachineState`, GUI factory. |
| `Voice.cs` | Per-lane sample voice: linear-interp playback, velocity gain, deferred re-trigger fade, choke, start offset, reverse, scheduled cut, sample-accurate retrigger with live per-row modulation (`SetRetrigPitch`/`SetRetrigOffset`/`SetRetrigCut`). |
| `DrumKit.cs` | `.pdrumgrid.xml` load/save, wavetable snapshot via `GetDataAsFloat`, lane source resolution. |
| `Gui.cs` | Embedded param-window GUI: kit load/save, per-lane wave assignment, swing controls. (Stub — wire to your preferred layout.) |

## Build / deploy
Compliant per Build §1: `net10.0-windows`, `UseWPF=true`, `DebugType=none`,
`DebugSymbols=false`, `GenerateDependencyFile=false`, `NoWarn=MSB3277`.
`AssemblyName = "Pedal DrumGrid.NET"` (the `.NET` suffix routes it to the managed
loader — Build §2). Post-build copies the dll to
`C:\Program Files\ReBuzz\Gear\Generators\`.
