using System.Text;
using MixerNet.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MixerNet;

/// <summary>
/// Diagnostic: Mixer.exe --probe report.txt
/// Opens every active device with every API for a moment and writes the results.
/// </summary>
static class Probe
{
    public static string Run()
    {
        string result = "";
        var t = new Thread(() => result = RunCore());
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        t.Join();
        return result;
    }

    static string RunCore()
    {
        var sb = new StringBuilder();
        using var en = new MMDeviceEnumerator();

        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            string name = d.FriendlyName;
            sb.AppendLine($"OUTPUT  {name}");
            sb.AppendLine("  WASAPI      " + Try(() =>
            {
                int rate;
                using (var ac = d.AudioClient) rate = ac.MixFormat.SampleRate;
                return Play(new WasapiOut(d, AudioClientShareMode.Shared, true, 60),
                    WaveFormat.CreateIeeeFloatWaveFormat(rate, 2));
            }));
            sb.AppendLine("  DirectSound " + Try(() =>
            {
                var info = DirectSoundOut.Devices.FirstOrDefault(x =>
                               string.Equals(x.Description, name, StringComparison.OrdinalIgnoreCase))
                           ?? throw new InvalidOperationException("no matching DirectSound device");
                return Play(new DirectSoundOut(info.Guid, 80), new WaveFormat(48000, 16, 2));
            }));
            sb.AppendLine("  WaveOut     " + Try(() =>
            {
                int n = -1;
                for (int i = 0; i < WaveOut.DeviceCount && n < 0; i++)
                    if (name.StartsWith(WaveOut.GetCapabilities(i).ProductName, StringComparison.OrdinalIgnoreCase)) n = i;
                if (n < 0) throw new InvalidOperationException("no matching WaveOut device");
                return Play(new WaveOutEvent { DeviceNumber = n }, new WaveFormat(48000, 16, 2));
            }));
            sb.AppendLine("  Loopback    " + Try(() => Capture(new WasapiLoopbackCapture(d))));
        }

        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            sb.AppendLine($"INPUT   {d.FriendlyName}");
            sb.AppendLine("  WASAPI      " + Try(() => Capture(new WasapiCapture(d))));
        }
        return sb.ToString();
    }

    static string Try(Func<string> f)
    {
        try { return "OK    " + f(); }
        catch (Exception ex) { return "FAIL  " + MixEngine.Describe(ex).Replace('\n', ' '); }
    }

    static string Play(IWavePlayer player, WaveFormat format)
    {
        using (player)
        {
            var src = new Counting(format);
            Exception? err = null;
            player.PlaybackStopped += (_, e) => err ??= e.Exception;
            player.Init(src);
            player.Play();
            Thread.Sleep(500);
            player.Stop();
            if (err != null) throw err;
            if (src.Reads == 0) throw new TimeoutException("never requested audio");
            return $"reads={src.Reads} {format.SampleRate}Hz";
        }
    }

    static string Capture(IWaveIn cap)
    {
        using (cap)
        {
            int packets = 0;
            float peak = 0;
            Exception? err = null;
            cap.DataAvailable += (_, e) =>
            {
                packets++;
                if (cap.WaveFormat.Encoding is WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible &&
                    cap.WaveFormat.BitsPerSample == 32)
                    for (int i = 0; i + 4 <= e.BytesRecorded; i += 4)
                        peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(e.Buffer, i)));
            };
            cap.RecordingStopped += (_, e) => err ??= e.Exception;
            cap.StartRecording();
            Thread.Sleep(600);
            cap.StopRecording();
            Thread.Sleep(50);
            if (err != null) throw err;
            var f = cap.WaveFormat;
            return $"packets={packets} peak={peak:F3} {f.SampleRate}Hz {f.Channels}ch {f.BitsPerSample}bit";
        }
    }

    sealed class Counting(WaveFormat format) : IWaveProvider
    {
        public int Reads;
        public WaveFormat WaveFormat { get; } = format;

        public int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            Array.Clear(buffer, offset, count);
            return count;
        }
    }
}
