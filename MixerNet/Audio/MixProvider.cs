using NAudio.Wave;

namespace MixerNet.Audio;

/// <summary>Pulled by the output device: mixes A and B with volume/pan/mute/mode.</summary>
sealed class MixProvider : ISampleProvider
{
    readonly MixEngine _p;
    float[] _sa = Array.Empty<float>(), _sb = Array.Empty<float>();

    public volatile SourceChannel? A, B;
    public int Reads; // incremented on every pull; used to verify the device is running

    public WaveFormat WaveFormat { get; }

    public MixProvider(MixEngine p, int sampleRate)
    {
        _p = p;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
    }

    /// <summary>
    /// Moves a stereo signal left/right without dropping either channel's content.
    /// pan -1: all in left, 0: untouched, +1: all in right.
    /// </summary>
    static void PanStereo(ref float l, ref float r, float p)
    {
        if (p <= 0f) { l += r * -p; r *= 1f + p; }
        else { r += l * p; l *= 1f - p; }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        Interlocked.Increment(ref Reads);
        int frames = count / 2;
        if (_sa.Length < frames * 2)
        {
            _sa = new float[frames * 2];
            _sb = new float[frames * 2];
        }

        var a = A; var b = B;
        if (a != null) a.Fill(_sa, frames); else Array.Clear(_sa, 0, frames * 2);
        if (b != null) b.Fill(_sb, frames); else Array.Clear(_sb, 0, frames * 2);

        // Polarity fix: some sources deliver R inverted, which cancels when folded to mono.
        if (_p.InvertA) for (int i = 1; i < frames * 2; i += 2) _sa[i] = -_sa[i];
        if (_p.InvertB) for (int i = 1; i < frames * 2; i += 2) _sb[i] = -_sb[i];
        _p.UpdateCorrelation(0, _sa, frames);
        _p.UpdateCorrelation(1, _sb, frames);

        bool split = _p.Split;
        float master = _p.Master;
        float panA = _p.PanA, panB = _p.PanB;
        float ga = _p.MuteA ? 0f : _p.VolA;
        float gb = _p.MuteB ? 0f : _p.VolB;

        float pkA = 0, pkB = 0, pkL = 0, pkR = 0;
        for (int i = 0; i < frames; i++)
        {
            float al = _sa[2 * i], ar = _sa[2 * i + 1];
            float bl = _sb[2 * i], br = _sb[2 * i + 1];
            pkA = MathF.Max(pkA, MathF.Max(MathF.Abs(al), MathF.Abs(ar)));
            pkB = MathF.Max(pkB, MathF.Max(MathF.Abs(bl), MathF.Abs(br)));

            float l, r;
            if (split)
            {
                // A -> left, B -> right (each folded to mono).
                l = (al + ar) * 0.5f * ga;
                r = (bl + br) * 0.5f * gb;
            }
            else
            {
                al *= ga; ar *= ga; bl *= gb; br *= gb;
                PanStereo(ref al, ref ar, panA);
                PanStereo(ref bl, ref br, panB);
                l = al + bl;
                r = ar + br;
            }

            l = Math.Clamp(l * master, -1f, 1f);
            r = Math.Clamp(r * master, -1f, 1f);
            pkL = MathF.Max(pkL, MathF.Abs(l));
            pkR = MathF.Max(pkR, MathF.Abs(r));
            buffer[offset + 2 * i] = l;
            buffer[offset + 2 * i + 1] = r;
        }
        if ((count & 1) != 0) buffer[offset + count - 1] = 0f;

        _p.Peak(0, pkA);
        _p.Peak(1, pkB);
        _p.Peak(2, pkL);
        _p.Peak(3, pkR);
        return count;
    }
}
