using System;

namespace PedalDrumGrid
{
    // What a single hit should do, assembled from the row's two command slots.
    public struct TrigSpec
    {
        public float VelGain;        // 0..1
        public int   PitchSemis;     // base tuning + Pitch command
        public float StartOffset;    // 0..1 fraction of the sample (Offset/Reverse)
        public bool  Reverse;        // play backwards from StartOffset
        public int   DelaySamples;   // swing + Delay command
        public int   CutSamples;     // 0 = none; else stop this long after the hit starts
        public int   RetrigInterval; // 0 = none; else re-fire every N samples
        public int   RetrigLength;   // total samples to keep retriggering (from hit start)
    }

    // One voice per drum lane. One-shot sample playback with linear
    // interpolation, velocity gain, a short anti-click envelope, a deferred
    // re-trigger fade-cross (Tracker §4.2), choke, and a swing/note delay
    // countdown (Tracker §7.7). v1.1 adds start offset, reverse playback,
    // scheduled cut, and sample-accurate retrigger for the command columns.
    public sealed class Voice
    {
        const float FADE_STEP = 1f / 64f;     // ~1.3 ms @48k anti-click (Tracker)
        const float NEAR_ZERO = 1e-4f;

        WaveSnapshot _snap;
        double _pos;            // sub-sample read position
        double _inc;            // frames per output sample (pitch ratio; sample-rate folded in at Render)
        int    _dir;            // +1 forward, -1 reverse
        double _startPos;       // (re)start position in frames (for retrigger)
        float  _gain;           // current envelope gain
        float  _target;         // envelope target
        float  _velGain;        // velocity-scaled level
        bool   _active;

        // swing / note / command delay
        int _delaySamples;

        // scheduled cut (06) — counts from the hit start, -1 = none
        int _cutCountdown;

        // retrigger (02) — all in samples, 0 = inactive
        int _retrigInterval, _retrigCountdown, _retrigRemaining;

        // deferred re-trigger staging (a new hit while still sounding)
        bool _pendStaged;
        TrigSpec _pendSpec;
        WaveSnapshot _pendSnap;

        public bool Active => _active || _pendStaged || _delaySamples > 0 || _retrigInterval > 0;

        public void Trigger(in TrigSpec spec, WaveSnapshot snap)
        {
            if (snap == null || snap.Length <= 0) return;

            if (_active && _gain > NEAR_ZERO)
            {
                // Still sounding — stage a fade-cross instead of clicking.
                _pendStaged = true;
                _pendSpec   = spec;
                _pendSnap   = snap;
                _target     = 0f;                  // fade current out, fire on near-zero
                _retrigInterval = 0;               // old retrigger yields to the new hit
                _delaySamples = Math.Max(_delaySamples, spec.DelaySamples);
                return;
            }

            _delaySamples = spec.DelaySamples;
            SetupAndStart(spec, snap);
        }

        // Full setup for a fresh hit: position, pitch, direction, and the
        // cut/retrigger timers (which run from the hit start, i.e. after delay).
        void SetupAndStart(in TrigSpec spec, WaveSnapshot snap)
        {
            _snap    = snap;
            _velGain = spec.VelGain;
            _dir     = spec.Reverse ? -1 : +1;

            float off = spec.StartOffset; if (off < 0f) off = 0f; if (off > 1f) off = 1f;
            _startPos = off * (snap.Length - 1);
            _pos      = _startPos;

            _gain   = 0f; _target = 1f; _active = true; _pendStaged = false;
            _inc    = Math.Pow(2.0, spec.PitchSemis / 12.0);

            _cutCountdown    = spec.CutSamples > 0 ? spec.CutSamples : -1;
            _retrigInterval  = spec.RetrigInterval;
            _retrigCountdown = spec.RetrigInterval;
            _retrigRemaining = spec.RetrigLength;
        }

        // Re-fire for a retrigger: restart the sample, keep timers/params.
        void Restart()
        {
            _pos    = _startPos;
            _gain   = 0f; _target = 1f; _active = true;
        }

        public void Choke()    { if (_active) _target = 0f; _retrigInterval = 0; }
        public void StopFade() { _target = 0f; _delaySamples = 0; _pendStaged = false; _retrigInterval = 0; _cutCountdown = -1; }

        public void Render(float[] outL, float[] outR, int n, WaveSnapshot live, int engineRate)
        {
            Array.Clear(outL, 0, n);
            Array.Clear(outR, 0, n);
            if (!_active && !_pendStaged && _delaySamples <= 0 && _retrigInterval <= 0) return;

            for (int i = 0; i < n; i++)
            {
                // 1) initial delay (swing + Delay cmd) — silent countdown
                if (_delaySamples > 0)
                {
                    _delaySamples--;
                    if (_delaySamples == 0 && !_active && _pendStaged) FireStaged();
                    continue;
                }

                // 2) cut (06) — absolute timer from hit start
                if (_cutCountdown > 0)
                {
                    _cutCountdown--;
                    if (_cutCountdown == 0) { _target = 0f; _retrigInterval = 0; }
                }

                // 3) retrigger (02) — re-fire on interval until the window closes
                if (_retrigInterval > 0)
                {
                    if (_retrigRemaining > 0)
                    {
                        _retrigRemaining--;
                        if (--_retrigCountdown <= 0) { Restart(); _retrigCountdown = _retrigInterval; }
                    }
                    else _retrigInterval = 0;       // window ended
                }

                if (!_active)
                {
                    if (_pendStaged) { FireStaged(); }
                    else if (_retrigInterval > 0) { continue; }   // silent gap between retriggers
                    else return;                                  // truly done
                }

                var s = _snap;
                if (s == null) { _active = false; return; }

                // end-of-sample by direction
                bool ended = _dir > 0 ? (int)_pos >= s.Length - 1 : _pos <= 0.0;
                if (ended)
                {
                    _active = false;
                    if (_pendStaged) FireStaged();
                    else if (_retrigInterval > 0) continue;       // wait for next retrigger
                    else break;
                }
                else
                {
                    int idx = (int)_pos;
                    if (idx < 0) idx = 0;
                    if (idx > s.Length - 2) idx = s.Length - 2;
                    float frac = (float)(_pos - idx);
                    float l = Lerp(s.DataL[idx], s.DataL[idx + 1], frac);
                    float r = s.Stereo ? Lerp(s.DataR[idx], s.DataR[idx + 1], frac) : l;

                    if (_gain < _target)      _gain = Math.Min(_target, _gain + FADE_STEP);
                    else if (_gain > _target) _gain = Math.Max(_target, _gain - FADE_STEP);

                    float g = _gain * _velGain;
                    outL[i] = l * g;
                    outR[i] = r * g;

                    double inc = _inc * (s.SampleRate / (double)engineRate);
                    _pos += _dir > 0 ? inc : -inc;

                    if (_pendStaged && _gain <= NEAR_ZERO && _target == 0f) FireStaged();
                }
            }
        }

        void FireStaged() => SetupAndStart(_pendSpec, _pendSnap);

        static float Lerp(float a, float b, float f) => a + (b - a) * f;
    }
}
