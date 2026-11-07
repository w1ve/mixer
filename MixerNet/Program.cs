namespace MixerNet;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Mixer.exe --probe report.txt : device diagnostic, no UI.
        if (args.Length >= 2 && args[0] == "--probe")
        {
            File.WriteAllText(args[1], Probe.Run());
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
