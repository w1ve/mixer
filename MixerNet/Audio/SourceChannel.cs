using NAudio.Wave;

namespace MixerNet.Audio;

enum SampleKind { Unsupported, F32, I16, I24, I32 }

/// <summary>
/// Captures one device into a ring buffer of stereo float frames, and plays it
/// back at the output sample rate. The capture card and the output card run on
/// independent clocks, so the playback ratio is nudged (max ±1%) to keep the
/// buffer near its target fill. That avoids slow drift, clicks and growing delay.
/// </summary>
sealed class SourceChannel : IDisposable
{
    static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    readonly IWaveIn _capture;
    readonly object _lock = new();
    readonly float[] _ring; // interleaved L,R
    int _rd, _wr, _count;

    readonly SampleKind _kind;
    readonly int _channels, _bytesPerSample, _blockAlign;
    readonly double _baseRatio;
    readonly int _target; // target fill in samples (≈80 ms)
    double _ratio, _frac = 1.0;
    float _pl, _pr, _nl, _nr;
    bool _primed;
    volatile bool _disposed;

    public string Name { get; }
    public string Api { get; }
    public event Action<SourceChannel, Exception>? Failed;

    public SourceChannel(IWaveIn capture, string name, string api, int outRate)
    {
        _capture = capture;
        Name = name;
        Api = api;

        var wf = capture.WaveFormat;
        _kind = Detect(wf);
        if (_kind == SampleKind.Unsupported)
            throw new NotSupportedException($"Unsupported capture format: {wf}");

        _channels = Math.Max(1, wf.Channels);
        _blockAlign = wf.BlockAlign;
        _bytesPerSample = _blockAlign / _channels;

        int inRate = wf.SampleRate;
        _ring = new float[Math.Max(inRate, 9600) & ~1]; // ≈0.5 s of stereo frames
        _target = Math.Max((int)(inRate * 0.08) * 2, 4);
        _baseRatio = _ratio = (double)inRate / outRate;

        capture.DataAvailable += OnData;
        capture.RecordingStopped += OnStopped;
    }

    static SampleKind Detect(WaveFormat wf)
    {
        bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat;
        bool isPcm = wf.Encoding == WaveFormatEncoding.Pcm;
        int bps = wf.BlockAlign / Math.Max(1, wf.Channels);

        if (wf is WaveFormatExtensible x)
        {
            isFloat = x.SubFormat == SubtypeFloat;
            isPcm = x.SubFormat == SubtypePcm;
        }
        else if (wf.Encoding == WaveFormatEncoding.Extensible)
        {
            // Shared-mode mix formats are float32 in practice.
            isFloat = bps == 4;
        }

        if (isFloat && bps == 4) return SampleKind.F32;
        if (isPcm)
            return bps switch
            {
                2 => SampleKind.I16,
                3 => SampleKind.I24,
                4 => SampleKind.I32,
                _ => SampleKind.Unsupported,
            };
        return SampleKind.Unsupported;
    }

    float ReadSample(byte[] b, int o) => _kind switch
    {
        SampleKind.F32 => BitConverter.ToSingle(b, o),
        SampleKind.I16 => BitConverter.ToInt16(b, o) / 32768f,
        SampleKind.I24 => (b[o] | (b[o + 1] << 8) | ((sbyte)b[o + 2] << 16)) / 8388608f,
        SampleKind.I32 => BitConverter.ToInt32(b, o) / 2147483648f,
        _ => 0f,
    };

    void OnData(object? sender, WaveInEventArgs e)
    {
        var b = e.Buffer;
        int frames = e.BytesRecorded / _blockAlign;
        lock (_lock)
        {
            for (int f = 0, off = 0; f < frames; f++, off += _blockAlign)
            {
                if (_ring.Length - _count < 2) break; // overflow: drop rather than block
                float l = ReadSample(b, off);
                float r = _channels > 1 ? ReadSample(b, off + _bytesPerSample) : l;
                _ring[_wr] = l;
                _ring[_wr + 1] = r;
                _wr = (_wr + 2) % _ring.Length;
                _count += 2;
            }
        }
    }

    void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null && !_disposed) Failed?.Invoke(this, e.Exception);
    }

    /// <summary>Fills <paramref name="frames"/> stereo frames (interleaved) at the output rate.</summary>
    public void Fill(float[] dst, int frames)
    {
        lock (_lock)
        {
            BeginBlock();
            for (int i = 0; i < frames; i++)
                NextFrame(out dst[2 * i], out dst[2 * i + 1]);
        }
    }

    void BeginBlock()
    {
        if (!_primed)
        {
            if (_count >= _target) _primed = true;
            else return;
        }
        if (_count > _target * 3)
        {
            // Far too much buffered (e.g. a burst after a stall): jump back to target.
            int skip = (_count - _target) & ~1;
            _rd = (_rd + skip) % _ring.Length;
            _count -= skip;
        }
        double err = Math.Clamp((_count - _target) / (double)_target, -1.0, 1.0);
        _ratio = _baseRatio * (1.0 + 0.01 * err);
    }

    void NextFrame(out float l, out float r)
    {
        if (!_primed) { l = r = 0f; return; }
        while (_frac >= 1.0)
        {
            if (_count >= 2)
            {
                _pl = _nl; _pr = _nr;
                _nl = _ring[_rd]; _nr = _ring[_rd + 1];
                _rd = (_rd + 2) % _ring.Length;
                _count -= 2;
                _frac -= 1.0;
            }
            else
            {
                // Underrun (source silent / loopback idle): output silence and re-prime.
                _primed = false;
                l = r = 0f;
                return;
            }
        }
        float t = (float)_frac;
        l = _pl + (_nl - _pl) * t;
        r = _pr + (_nr - _pr) * t;
        _frac += _ratio;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _capture.DataAvailable -= OnData;
        _capture.RecordingStopped -= OnStopped;
        try { _capture.StopRecording(); } catch { /* already stopped */ }
        try { _capture.Dispose(); } catch { /* best effort */ }
    }
}
