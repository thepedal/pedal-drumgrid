using System;

namespace PedalDrumGrid
{
    // One voice per drum lane. One-shot sample playback with linear
    // interpolation, velocity gain, a short anti-click envelope, a deferred
    // re-trigger fade-cross (Tracker §4.2), choke, and a swing/note delay
    // countdown (Tracker §7.7). For production, lift Tracker's interpolation
    // table (Interpolation.cs) and the full fade-cross machinery.
    public sealed class Voice
    {
        const float FADE_STEP = 1f / 64f;     // ~1.3 ms @48k anti-click (Tracker)
        const float NEAR_ZERO = 1e-4f;

        WaveSnapshot _snap;
        double _pos;            // sub-sample read position
        double _inc;            // frames per output sample (pitch * rate ratio)
        float  _gain;           // current envelope gain
        float  _target;         // envelope target
        float  _velGain;        // velocity-scaled level
        bool   _active;

        // swing / note delay
        int _delaySamples;

        // deferred re-trigger staging (a new hit while still sounding)
        bool _pendStaged;
        float _pendVel;
        int   _pendPitch;
        WaveSnapshot _pendSnap;

        public bool Active => _active || _pendStaged || _delaySamples > 0;

        public void Trigger(float velGain, int pitchSemis, int delaySamples, WaveSnapshot snap)
        {
            if (snap == null || snap.Length <= 0) return;

            if (_active && _gain > NEAR_ZERO)
            {
                // Still sounding — stage a fade-cross instead of clicking.
                _pendStaged = true;
                _pendVel    = velGain;
                _pendPitch  = pitchSemis;
                _pendSnap   = snap;
                _target     = 0f;              // fade current out, fire on near-zero
                _delaySamples = Math.Max(_delaySamples, delaySamples);
                return;
            }

            _delaySamples = delaySamples;
            StartNow(velGain, pitchSemis, snap);
        }

        void StartNow(float velGain, int pitchSemis, WaveSnapshot snap)
        {
            _snap    = snap;
            _pos     = 0;
            _velGain = velGain;
            _gain    = 0f;          // ramp up from zero (anti-click)
            _target  = 1f;
            _active  = true;
            _pendStaged = false;

            // pitch: semitone offset relative to the snapshot's root.
            double rate = Math.Pow(2.0, pitchSemis / 12.0);
            _inc = rate; // base; sample-rate ratio folded in at Render time
        }

        public void Choke() { if (_active) _target = 0f; }

        public void StopFade() { _target = 0f; _delaySamples = 0; _pendStaged = false; }

        public void Render(float[] outL, float[] outR, int n, WaveSnapshot live, int engineRate)
        {
            Array.Clear(outL, 0, n);
            Array.Clear(outR, 0, n);
            if (!_active && !_pendStaged && _delaySamples <= 0) return;

            for (int i = 0; i < n; i++)
            {
                // swing / note delay: hold silent until the countdown elapses
                if (_delaySamples > 0) { _delaySamples--; if (_delaySamples == 0 && !_active && _pendStaged) FireStaged(); continue; }

                if (!_active)
                {
                    if (_pendStaged) FireStaged();
                    else return;
                }

                var s = _snap;
                if (s == null) { _active = false; return; }

                // sample-rate correction: snap.SampleRate -> engine rate
                double inc = _inc * (s.SampleRate / (double)engineRate);

                int idx = (int)_pos;
                if (idx >= s.Length - 1)
                {
                    _active = false;
                    if (_pendStaged) { FireStaged(); }
                    else break;
                }
                else
                {
                    float frac = (float)(_pos - idx);
                    float l = Lerp(s.DataL[idx], s.DataL[idx + 1], frac);
                    float r = s.Stereo ? Lerp(s.DataR[idx], s.DataR[idx + 1], frac) : l;

                    // anti-click envelope
                    if (_gain < _target)      _gain = Math.Min(_target, _gain + FADE_STEP);
                    else if (_gain > _target) _gain = Math.Max(_target, _gain - FADE_STEP);

                    float g = _gain * _velGain;
                    outL[i] = l * g;
                    outR[i] = r * g;

                    _pos += inc;

                    // fire a staged re-trigger once the current note faded out
                    if (_pendStaged && _gain <= NEAR_ZERO && _target == 0f)
                        FireStaged();
                }
            }
        }

        void FireStaged()
        {
            StartNow(_pendVel, _pendPitch, _pendSnap);
        }

        static float Lerp(float a, float b, float f) => a + (b - a) * f;
    }
}
