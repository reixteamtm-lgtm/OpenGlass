using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace LiquidGlassStudio
{
    public partial class MainWindow : Window
    {
        readonly List<Action> _refreshers = new List<Action>();
        readonly DispatcherTimer _status = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        bool _loading;

        public MainWindow()
        {
            InitializeComponent();
            txtVersion.Text = "v" + typeof(MainWindow).Assembly.GetName().Version.ToString(3);

            btnClose.Click += (s, e) => Close();
            btnMin.Click += (s, e) => WindowState = WindowState.Minimized;
            btnMax.Click += (s, e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is FrameworkElement fe && !(fe.TemplatedParent is Control) ) { try { DragMove(); } catch { } } };
            StateChanged += (s, e) => Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            SourceInitialized += (s, e) => ApplyWindowGlass();

            lstNav.SelectionChanged += (s, e) => ShowPanel();

            BuildHome();
            BuildWindowPage();
            BuildTaskbarPage();
            BuildSettingsPage();
            RefreshAll();

            _status.Tick += (s, e) => UpdateStatus();
            _status.Start();
            Closed += (s, e) => _status.Stop();
            UpdateStatus();
        }

        // ---------- this window: let OpenGlass draw Liquid Glass behind it (or Acrylic without OpenGlass) ----------
        void ApplyWindowGlass()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var src = HwndSource.FromHwnd(hwnd);
            if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
            var m = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Native.DwmExtendFrameIntoClientArea(hwnd, ref m);
            int dark = 1; Native.DwmSetWindowAttribute(hwnd, 20, ref dark, 4);
            int round = 2; Native.DwmSetWindowAttribute(hwnd, 33, ref round, 4);
            GlassSettings.OpenGlassState(out bool og);
            int backdrop = og ? 1 : 3;
            Native.DwmSetWindowAttribute(hwnd, 38, ref backdrop, 4);
        }

        void ShowPanel()
        {
            if (!(lstNav.SelectedItem is ListBoxItem it)) return;
            var name = it.Tag as string;
            foreach (var p in new FrameworkElement[] { pnlHome, pnlWindow, pnlTaskbar, pnlSettings })
                p.Visibility = p.Name == name ? Visibility.Visible : Visibility.Collapsed;
        }

        void RefreshAll()
        {
            _loading = true;
            foreach (var r in _refreshers) r();
            _loading = false;
        }

        // ---------- generic rows ----------
        CheckBox BindToggle(CheckBox box, Func<bool> get, Action<bool> set)
        {
            _refreshers.Add(() => box.IsChecked = get());
            box.Click += (s, e) => { if (!_loading) set(box.IsChecked == true); };
            return box;
        }

        FrameworkElement SliderRow(string label, string hint, int min, int max, string unit, Func<int> get, Action<int> set)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 8) };
            var head = new Grid();
            head.Children.Add(new TextBlock { Text = label });
            var val = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, Foreground = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xA6)) };
            head.Children.Add(val);
            panel.Children.Add(head);
            var slider = new Slider { Minimum = min, Maximum = max, SmallChange = 1, LargeChange = Math.Max(1, (max - min) / 10), IsSnapToTickEnabled = true, TickFrequency = 1, Margin = new Thickness(0, 4, 0, 0) };
            slider.Style = (Style)FindResource("MacSlider");
            panel.Children.Add(slider);
            if (!string.IsNullOrEmpty(hint)) panel.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("RowSub") });
            _refreshers.Add(() => { var v = get(); slider.Value = v; val.Text = v + unit; });
            slider.ValueChanged += (s, e) =>
            {
                var v = (int)Math.Round(slider.Value);
                val.Text = v + unit;
                if (!_loading) set(v);
            };
            return panel;
        }

        static void AddRows(Panel host, IEnumerable<FrameworkElement> rows)
        {
            bool first = true;
            foreach (var r in rows)
            {
                if (!first) host.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.FindResource("RowLine") });
                host.Children.Add(r);
                first = false;
            }
        }

        void AddPresets(WrapPanel host, bool taskbar)
        {
            var names = new[] { ("clear", "Trong vắt"), ("soft", "Mờ nhẹ"), ("ios", "Kiểu iOS"), ("heavy", "Mờ hẳn") };
            foreach (var (key, text) in names)
            {
                var b = new Button { Content = text };
                b.Click += (s, e) => { GlassSettings.ApplyPreset(key, taskbar); RefreshAll(); };
                host.Children.Add(b);
            }
        }

        // ---------- pages ----------
        void BuildHome()
        {
            btnRestartOg.Click += (s, e) => GlassSettings.RestartOpenGlass();
            BindToggle(chkLiquid, () => GlassSettings.Get("LiquidGlass", 0) != 0, on =>
            {
                if (on) GlassSettings.Set("UseDirect3DRendering", 1);
                GlassSettings.Set("LiquidGlass", on ? 1 : 0);
            });
            BindToggle(chkTaskbar, () => GlassSettings.Get("GlassOverrideAccent", 0) != 0, on => GlassSettings.Set("GlassOverrideAccent", on ? 1 : 0));
            BindToggle(chkNoDark, () => GlassSettings.Get("LiquidGlassIgnoreMaximized", 1) != 0, on => GlassSettings.Set("LiquidGlassIgnoreMaximized", on ? 1 : 0));
            BindToggle(chkOutward, () => GlassSettings.Get("LiquidGlassOutward", 0) != 0, on => GlassSettings.Set("LiquidGlassOutward", on ? 1 : 0));
        }

        void BuildWindowPage()
        {
            AddPresets(wpWindowPresets, false);
            AddRows(spWindow, GlassSettings.Window.Select(v =>
                SliderRow(v.Label, v.Hint, v.Min, v.Max, v.Unit, () => GlassSettings.Get(v), x => GlassSettings.Set(v.Name, x))));
            btnWindowReset.Click += (s, e) =>
            {
                foreach (var v in GlassSettings.Window) GlassSettings.Delete(v.Name);
                RefreshAll();
            };
        }

        void BuildTaskbarPage()
        {
            var f = App.Feeder;
            BindToggle(chkDock, () => GlassSettings.GetStudio("Dock", 0) != 0, on =>
            {
                GlassSettings.SetStudio("Dock", on ? 1 : 0);
                if (f != null) { f.DockEnabled = on; f.Poke(); }
                if (on && GlassSettings.Get("GlassOverrideAccent", 0) == 0) { GlassSettings.Set("GlassOverrideAccent", 1); RefreshAll(); }
                GlassSettings.NotifyLater();
            });
            BindToggle(chkTrayPill, () => GlassSettings.GetStudio("TrayPill", 1) != 0, on =>
            {
                GlassSettings.SetStudio("TrayPill", on ? 1 : 0);
                if (f != null) { f.TrayEnabled = on; f.Poke(); }
                GlassSettings.NotifyLater();
            });
            BindToggle(chkFillUnder, () => GlassSettings.GetStudio("FillUnderDock", 0) != 0, on =>
            {
                GlassSettings.SetStudio("FillUnderDock", on ? 1 : 0);
                if (f != null) { f.FillUnderDock = on; f.Poke(); }
            });
            AddRows(spDock, new[]
            {
                SliderRow("Bo góc Dock", null, 0, 40, " px", () => GlassSettings.GetStudio("DockRadius", 18), v => { GlassSettings.SetStudio("DockRadius", v); if (f != null) { f.Radius = v; f.Poke(); } GlassSettings.NotifyLater(); }),
                SliderRow("Bóng đổ", null, 0, 100, "%", () => GlassSettings.GetStudio("DockShadow", 35), v => { GlassSettings.SetStudio("DockShadow", v); if (f != null) { f.Shadow = v; f.Poke(); } GlassSettings.NotifyLater(); }),
                SliderRow("Lề hai bên icon", null, 0, 30, " px", () => GlassSettings.GetStudio("DockPadX", 8), v => { GlassSettings.SetStudio("DockPadX", v); if (f != null) { f.PadX = v; f.Poke(); } GlassSettings.NotifyLater(); }),
                SliderRow("Lề trên dưới", null, 0, 10, " px", () => GlassSettings.GetStudio("DockPadY", 3), v => { GlassSettings.SetStudio("DockPadY", v); if (f != null) { f.PadY = v; f.Poke(); } GlassSettings.NotifyLater(); }),
            });
            AddPresets(wpTaskbarPresets, true);
            AddRows(spTaskbar, GlassSettings.Taskbar.Select(v =>
                SliderRow(v.Label, v.Hint, v.Min, v.Max, v.Unit, () => GlassSettings.Get(v), x => GlassSettings.Set(v.Name, x))));
            btnTaskbarReset.Click += (s, e) =>
            {
                foreach (var v in GlassSettings.Taskbar) GlassSettings.Delete(v.Name);
                RefreshAll();
            };
        }

        void BuildSettingsPage()
        {
            BindToggle(chkStartup, () => GlassSettings.StartsWithWindows, on => GlassSettings.StartsWithWindows = on);
            BindToggle(chkKeepRunning, () => GlassSettings.GetStudio("KeepRunning", 1) != 0, on => GlassSettings.SetStudio("KeepRunning", on ? 1 : 0));
        }

        void UpdateStatus()
        {
            var og = GlassSettings.OpenGlassState(out bool running);
            txtOg.Text = (running ? "● " : "○ ") + "OpenGlass " + og;
            txtOg.Foreground = new SolidColorBrush(running ? Color.FromRgb(0x30, 0xD1, 0x58) : Color.FromRgb(0xFF, 0x9F, 0x0A));
            var tb = GlassSettings.TranslucentTbRunning();
            txtTb.Text = (tb ? "● " : "○ ") + "TranslucentTB " + (tb ? "đang chạy" : "chưa chạy");
            txtTb.Foreground = new SolidColorBrush(tb ? Color.FromRgb(0x30, 0xD1, 0x58) : Color.FromRgb(0xFF, 0x9F, 0x0A));
            txtDockStatus.Text = App.Feeder?.Status ?? "";
        }
    }
}
