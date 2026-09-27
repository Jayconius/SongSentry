using System;
using System.Threading;
using System.Windows.Forms;

namespace SongSentry
{
    static class Program
    {
        public const string Version = "1.0.0";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// "Start with Windows": a per-user Run entry that starts SongSentry hidden in the tray (no admin needed).
        public static bool StartsWithWindows()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue("SongSentry") != null;
            }
            catch { return false; }
        }

        public static bool SetStartWithWindows(bool on)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue("SongSentry", "\"" + Application.ExecutablePath + "\" --tray");
                    else if (k.GetValue("SongSentry") != null) k.DeleteValue("SongSentry");
                }
                return true;
            }
            catch (Exception e) { Log.Write("start with Windows: " + e.Message); return false; }
        }

        [STAThread]
        static void Main(string[] args)
        {
            bool first;
            using (var mutex = new Mutex(true, "SongSentry.SingleInstance", out first))
            using (var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "SongSentry.Show"))
            {
                if (!first) { showSignal.Set(); return; }   // already running: bring that window up instead

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Log.Write("UI error: " + e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("fatal: " + e.ExceptionObject);
                Log.Write("SongSentry " + Version + " starting");

                var settings = Settings.Load();
                if (StartsWithWindows()) SetStartWithWindows(true);   // keep the start-up entry pointing at this exe if it moved
                var obs = new ObsConnection();
                var engine = new Engine(settings, new ObsBackend(obs), null);
                var nowPlaying = new NowPlayingWatcher();

                obs.EventReceived += engine.OnObsEvent;
                obs.StateChanged += () => engine.OnObsState(obs.State == ObsState.Connected);
                nowPlaying.Changed += engine.OnMedia;
                engine.Skipper = nowPlaying.SkipNext;

                var library = new SongLibrary();
                library.Load();
                var recognizer = new Recognizer(settings, engine, library);
                var form = new MainForm(settings, engine, obs, nowPlaying, recognizer);
                engine.Start();
                recognizer.Start();
                nowPlaying.Start();
                engine.OnMedia(nowPlaying.Snapshot());
                obs.Configure(settings.Host, settings.Port, settings.Password);
                obs.Start();

                // A second launch signals us to show the window.
                var waiter = new Thread(() =>
                {
                    while (true)
                    {
                        showSignal.WaitOne();
                        try { form.BeginInvoke((Action)form.ShowFromTray); } catch { return; }
                    }
                }) { IsBackground = true, Name = "show-signal" };
                waiter.Start();

                bool hidden = settings.StartHidden || Array.IndexOf(args, "--tray") >= 0;
                if (hidden)
                {
                    form.CreateControl();
                    var h = form.Handle;   // create the handle so BeginInvoke works while hidden
                    Application.Run();
                }
                else Application.Run(form);

                recognizer.Dispose();   // also saves the song memory
                nowPlaying.Dispose();
                obs.Dispose();
                engine.Dispose();
                Log.Write("SongSentry stopped");
            }
        }
    }
}
