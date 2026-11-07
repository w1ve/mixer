using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MixerNet.Audio;

public enum OutputApi { Auto, Wasapi, DirectSound, WaveOut }

public sealed record DeviceItem(string Id, string Label)
{
    public static readonly DeviceItem None = new("", "(none)");
    public override string ToString() => Label;
}

/// <summary>
/// Owns the output player and both source captures. All device work runs on an
/// MTA worker thread; mixer parameters are plain volatile fields read by the audio thread.
/// </summary>
sealed class MixEngine : IDisposable
{
    // Live mixer parameters (UI thread writes, audio thread reads).
    public volatile float VolA = 1f, VolB = 1f, PanA, PanB, Master = 1f;
    public volatile bool MuteA, MuteB, Split, InvertA, InvertB;

    // Smoothed L/R correlation per source (after polarity fix): +1 in phase, -1 inverted.
    // NaN while the source is silent. Written by the audio thread only.
    readonly float[] _corr = { float.NaN, float.NaN };
    public float CorrelationA => Volatile.Read(ref _corr[0]);
    public float CorrelationB => Volatile.Read(ref _corr[1]);

    internal void UpdateCorrelation(int i, float[] buf, int frames)
    {
        double lr = 0, ll = 0, rr = 0;
        for (int f = 0; f < frames; f++)
        {
            double l = buf[2 * f], r = buf[2 * f + 1];
            lr += l * r; ll += l * l; rr += r * r;
        }
        // Ignore near-silence (< about -55 dBFS on either side) and keep the last value,
        // so the warning doesn't flicker during quiet passages. Reset on each Start.
        double floor = frames * 3e-6;
        if (ll < floor || rr < floor) return;
        float c = (float)(lr / Math.Sqrt(ll * rr));
        float prev = Volatile.Read(ref _corr[i]);
        Volatile.Write(ref _corr[i], float.IsNaN(prev) ? c : prev * 0.9f + c * 0.1f);
    }

    readonly int[] _peaks = new int[4]; // float bits: in A, in B, out L, out R
    readonly MtaWorker _worker = new();
    MMDeviceEnumerator? _enumerator;
    IWavePlayer? _player;
    MixProvider? _mix;
    SourceChannel? _a, _b;
    int _session;

    /// <summary>Raised (on an audio thread) when a running device fails.</summary>
    public event Action<string>? Error;

    MMDeviceEnumerator En => _enumerator ??= new MMDeviceEnumerator();

    // ------------------------------------------------------------------ public API

    public Task<(List<DeviceItem> Sources, List<DeviceItem> Outputs)> ListDevicesAsync() =>
        _worker.RunAsync(() =>
        {
            var sources = new List<DeviceItem>();
            var outputs = new List<DeviceItem>();
            foreach (var d in En.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                sources.Add(new DeviceItem("in:" + d.ID, d.FriendlyName));
            foreach (var d in En.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                // A render device can be captured as WASAPI loopback ("what's playing on it").
                sources.Add(new DeviceItem("loop:" + d.ID, d.FriendlyName + " (loopback)"));
                outputs.Add(new DeviceItem(d.ID, d.FriendlyName));
            }
            return (sources, outputs);
        });

    /// <summary>Starts the engine; returns a short description of the APIs in use.</summary>
    public Task<string> StartAsync(string? a, string? b, string outId, OutputApi api) =>
        _worker.RunAsync(() => StartCore(a, b, outId, api));

    public Task StopAsync() => _worker.RunAsync(() => { StopCore(); return 0; });

    public float[] TakeLevels()
    {
        var r = new float[4];
        for (int i = 0; i < 4; i++)
            r[i] = BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _peaks[i], 0));
        return r;
    }

    internal void Peak(int i, float v)
    {
        if (!(v > 0f) || !float.IsFinite(v)) return;
        int bits = BitConverter.SingleToInt32Bits(v); // positive floats order like ints
        int cur;
        do
        {
            cur = Volatile.Read(ref _peaks[i]);
            if (bits <= cur) return;
        } while (Interlocked.CompareExchange(ref _peaks[i], bits, cur) != cur);
    }

    public void Dispose()
    {
        try
        {
            _worker.RunAsync(() => { StopCore(); _enumerator?.Dispose(); return 0; }).Wait(3000);
        }
        catch { /* shutting down */ }
        _worker.Dispose();
    }

    // ------------------------------------------------------------------ start / stop

    string StartCore(string? a, string? b, string outId, OutputApi api)
    {
        StopCore();
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b))
            throw new InvalidOperationException("Select at least one source.");
        if (string.IsNullOrEmpty(outId))
            throw new InvalidOperationException("Select an output.");
        if (string.Equals(a, "loop:" + outId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(b, "loop:" + outId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A loopback source is the same device as the output. That would feed back.");

        int session = Interlocked.Increment(ref _session);
        Volatile.Write(ref _corr[0], float.NaN);
        Volatile.Write(ref _corr[1], float.NaN);

        MMDevice outDev;
        try { outDev = En.GetDevice(outId); }
        catch (Exception ex) { throw new InvalidOperationException($"Output device not found ({Describe(ex)}). Click Refresh."); }

        var (player, mix, outApi) = OpenOutput(outDev, api, session);
        _player = player;
        _mix = mix;

        try
        {
            if (!string.IsNullOrEmpty(a)) _a = OpenSource(a, mix.WaveFormat.SampleRate, "Source A", session);
            if (!string.IsNullOrEmpty(b)) _b = OpenSource(b, mix.WaveFormat.SampleRate, "Source B", session);
        }
        catch
        {
            StopCore();
            throw;
        }

        mix.A = _a;
        mix.B = _b;

        var info = $"Output: {outApi} @ {mix.WaveFormat.SampleRate} Hz";
        if (_a != null) info += $"  ·  A: {_a.Api}";
        if (_b != null) info += $"  ·  B: {_b.Api}";
        return info;
    }

    void StopCore()
    {
        Interlocked.Increment(ref _session); // ignore late errors from the old session
        if (_mix != null) { _mix.A = null; _mix.B = null; }
        if (_player != null)
        {
            try { _player.Stop(); } catch { }
            try { _player.Dispose(); } catch { }
            _player = null;
        }
        _a?.Dispose(); _a = null;
        _b?.Dispose(); _b = null;
        _mix = null;
    }

    void Fail(int session, string message)
    {
        if (session == Volatile.Read(ref _session)) Error?.Invoke(message);
    }

    // ------------------------------------------------------------------ output

    (IWavePlayer Player, MixProvider Mix, string Api) OpenOutput(MMDevice dev, OutputApi api, int session)
    {
        var order = api == OutputApi.Auto
            ? new[] { OutputApi.Wasapi, OutputApi.DirectSound, OutputApi.WaveOut }
            : new[] { api };
        var errors = new List<string>();
        string name = dev.FriendlyName;

        foreach (var kind in order)
        {
            IWavePlayer? p = null;
            try
            {
                MixProvider mix;
                switch (kind)
                {
                    case OutputApi.Wasapi:
                    {
                        int rate;
                        using (var ac = dev.AudioClient) rate = ac.MixFormat.SampleRate;
                        mix = new MixProvider(this, rate);
                        var w = new WasapiOut(dev, AudioClientShareMode.Shared, true, 60);
                        p = w;
                        w.Init(mix);
                        break;
                    }
                    case OutputApi.DirectSound:
                    {
                        var info = DirectSoundOut.Devices.FirstOrDefault(d =>
                                       string.Equals(d.Description, name, StringComparison.OrdinalIgnoreCase))
                                   ?? throw new InvalidOperationException("no matching DirectSound device");
                        mix = new MixProvider(this, 48000);
                        var ds = new DirectSoundOut(info.Guid, 80);
                        p = ds;
                        ds.Init(mix, true);
                        break;
                    }
                    default:
                    {
                        int n = FindWaveOut(name);
                        mix = new MixProvider(this, 48000);
                        var wo = new WaveOutEvent { DeviceNumber = n, DesiredLatency = 120, NumberOfBuffers = 3 };
                        p = wo;
                        wo.Init(mix, true);
                        break;
                    }
                }

                // Verify the device actually starts pulling audio before calling it a success.
                Exception? early = null;
                EventHandler<StoppedEventArgs> onEarlyStop = (_, e) =>
                    early = e.Exception ?? new InvalidOperationException("playback stopped immediately");
                p.PlaybackStopped += onEarlyStop;
                p.Play();
                var sw = Stopwatch.StartNew();
                while (Volatile.Read(ref mix.Reads) == 0 && early == null && sw.ElapsedMilliseconds < 1500)
                    Thread.Sleep(10);
                p.PlaybackStopped -= onEarlyStop;
                if (early != null) throw early;
                if (Volatile.Read(ref mix.Reads) == 0)
                    throw new TimeoutException("device accepted the stream but never requested audio");

                p.PlaybackStopped += (_, e) =>
                {
                    if (e.Exception != null) Fail(session, $"Output \"{name}\" stopped: {Describe(e.Exception)}");
                };
                return (p, mix, ApiName(kind));
            }
            catch (Exception ex)
            {
                try { p?.Dispose(); } catch { }
                errors.Add($"{ApiName(kind)}: {Describe(ex)}");
            }
        }

        throw new InvalidOperationException($"Output \"{name}\" could not be opened.\n" + string.Join("\n", errors));
    }

    static int FindWaveOut(string friendlyName)
    {
        // MME product names are truncated to 31 characters.
        for (int i = 0; i < WaveOut.DeviceCount; i++)
        {
            var caps = WaveOut.GetCapabilities(i);
            if (friendlyName.StartsWith(caps.ProductName, StringComparison.OrdinalIgnoreCase)) return i;
        }
        throw new InvalidOperationException("no matching WaveOut device");
    }

    static int FindWaveIn(string friendlyName)
    {
        for (int i = 0; i < WaveIn.DeviceCount; i++)
        {
            var caps = WaveIn.GetCapabilities(i);
            if (friendlyName.StartsWith(caps.ProductName, StringComparison.OrdinalIgnoreCase)) return i;
        }
        throw new InvalidOperationException("no matching WaveIn device");
    }

    // ------------------------------------------------------------------ sources

    SourceChannel OpenSource(string id, int outRate, string label, int session)
    {
        SourceChannel ch;
        try
        {
            ch = id.StartsWith("loop:") ? OpenLoopback(id[5..], outRate)
               : id.StartsWith("in:") ? OpenInput(id[3..], outRate)
               : throw new InvalidOperationException($"bad source id {id}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{label}: {ex.Message}");
        }
        ch.Failed += (c, ex) => Fail(session, $"{label} \"{c.Name}\" stopped: {Describe(ex)}");
        return ch;
    }

    SourceChannel OpenLoopback(string deviceId, int outRate)
    {
        var dev = En.GetDevice(deviceId);
        string name = dev.FriendlyName + " (loopback)";
        try
        {
            return StartChannel(new WasapiLoopbackCapture(dev), name, "WASAPI loopback", outRate);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"\"{name}\": {Describe(ex)}");
        }
    }

    SourceChannel OpenInput(string deviceId, int outRate)
    {
        var dev = En.GetDevice(deviceId);
        string name = dev.FriendlyName;
        try
        {
            return StartChannel(new WasapiCapture(dev), name, "WASAPI", outRate);
        }
        catch (Exception wasapiEx)
        {
            try
            {
                var wi = new WaveInEvent
                {
                    DeviceNumber = FindWaveIn(name),
                    WaveFormat = new WaveFormat(48000, 16, 2),
                    BufferMilliseconds = 30,
                };
                return StartChannel(wi, name, "WaveIn", outRate);
            }
            catch (Exception waveInEx)
            {
                throw new InvalidOperationException(
                    $"\"{name}\" could not be opened.\nWASAPI: {Describe(wasapiEx)}\nWaveIn: {Describe(waveInEx)}");
            }
        }
    }

    static SourceChannel StartChannel(IWaveIn capture, string name, string api, int outRate)
    {
        SourceChannel? ch = null;
        try
        {
            ch = new SourceChannel(capture, name, api, outRate);
            capture.StartRecording();
            return ch;
        }
        catch
        {
            if (ch != null) ch.Dispose(); else capture.Dispose();
            throw;
        }
    }

    // ------------------------------------------------------------------ helpers

    static string ApiName(OutputApi a) => a switch
    {
        OutputApi.Wasapi => "WASAPI",
        OutputApi.DirectSound => "DirectSound",
        OutputApi.WaveOut => "WaveOut",
        _ => "Auto",
    };

    public static string Describe(Exception ex)
    {
        string msg = ex.Message.Trim();
        if (ex is COMException || ((uint)ex.HResult & 0xFFFF0000u) == 0x88890000u)
        {
            string hr = $"0x{ex.HResult:X8}";
            string known = KnownHr(ex.HResult);
            // NAudio often reports just the bare HRESULT; replace it with something readable.
            if (msg.Length == 0 || msg.Equals(hr, StringComparison.OrdinalIgnoreCase) || msg.StartsWith("Exception from HRESULT"))
                msg = known.Length > 0 ? known.Trim() : "Windows audio error";
            else if (known.Length > 0)
                msg += $" ({known.Trim()})";
            msg += $" [{hr}]";
        }
        return msg;
    }

    static string KnownHr(int hr) => (uint)hr switch
    {
        0x88890004 => " AUDCLNT_E_DEVICE_INVALIDATED",
        0x88890008 => " AUDCLNT_E_UNSUPPORTED_FORMAT",
        0x8889000A => " AUDCLNT_E_DEVICE_IN_USE",
        0x8889000F => " AUDCLNT_E_ENDPOINT_CREATE_FAILED",
        0x88890010 => " AUDCLNT_E_SERVICE_NOT_RUNNING",
        0x88890026 => " AUDCLNT_E_RESOURCES_INVALIDATED",
        0x80070005 => " E_ACCESSDENIED",
        0x88880302 => " DirectSound could not open the device driver",
        _ => "",
    };
}
