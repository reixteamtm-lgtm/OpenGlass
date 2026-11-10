using System;
using System.Linq;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace LiquidGlassStudio
{
    public partial class App : Application
    {
        static Mutex s_single;
        static EventWaitHandle s_showSignal;
        internal static DockFeeder Feeder;
        Forms.NotifyIcon _tray;
        MainWindow _window;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            s_single = new Mutex(true, @"Local\LiquidGlassStudio.Single", out bool first);
            s_showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LiquidGlassStudio.Show");
            if (!first)
            {
                s_showSignal.Set();
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (s, ex) =>
            {
                ex.Handled = true;
                try { MessageBox.Show(ex.Exception.Message, "Liquid Glass Studio", MessageBoxButton.OK, MessageBoxImage.Warning); } catch { }
            };

            Feeder = new DockFeeder
            {
                DockEnabled = GlassSettings.GetStudio("Dock", 0) != 0,
                TrayEnabled = GlassSettings.GetStudio("TrayPill", 1) != 0,
                Radius = GlassSettings.GetStudio("DockRadius", 18),
                Shadow = GlassSettings.GetStudio("DockShadow", 35),
                PadX = GlassSettings.GetStudio("DockPadX", 8),
                PadY = GlassSettings.GetStudio("DockPadY", 3),
                FillUnderDock = GlassSettings.GetStudio("FillUnderDock", 0) != 0,
            };
            Feeder.Start();

            _tray = new Forms.NotifyIcon
            {
                Text = "Liquid Glass Studio",
                Visible = true,
                Icon = LoadIcon(),
            };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Mở Liquid Glass Studio", null, (s, a) => ShowWindow());
            menu.Items.Add("Khởi động lại OpenGlass", null, (s, a) => GlassSettings.RestartOpenGlass());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Thoát", null, (s, a) => ExitApp());
            _tray.ContextMenuStrip = menu;
            _tray.MouseClick += (s, a) => { if (a.Button == Forms.MouseButtons.Left) ShowWindow(); };

            // second launch -> bring the window up
            var listener = new Thread(() =>
            {
                while (true)
                {
                    s_showSignal.WaitOne();
                    Dispatcher.BeginInvoke(new Action(ShowWindow));
                }
            }) { IsBackground = true };
            listener.Start();

            if (!e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase)))
                ShowWindow();
        }

        static System.Drawing.Icon LoadIcon()
        {
            try
            {
                var info = GetResourceStream(new Uri("pack://application:,,,/app.ico"));
                if (info != null) return new System.Drawing.Icon(info.Stream);
            }
            catch { }
            return System.Drawing.SystemIcons.Application;
        }

        internal void ShowWindow()
        {
            if (_window == null)
            {
                _window = new MainWindow();
                _window.Closed += (s, a) =>
                {
                    _window = null;
                    // free the UI memory while living in the tray
                    try { Native.EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle); } catch { }
                    if (GlassSettings.GetStudio("KeepRunning", 1) == 0)
                        ExitApp();
                };
            }
            _window.Show();
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
        }

        internal void ExitApp()
        {
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
            try { Feeder?.Dispose(); } catch { }
            Shutdown();
        }
    }
}
