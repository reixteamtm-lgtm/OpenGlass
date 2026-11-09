using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LiquidGlassStudio
{
    // One adjustable value: registry name, range, default (the value OpenGlass uses when the key is absent).
    internal sealed class GlassValue
    {
        public string Name;
        public string Label;
        public string Hint;
        public int Min, Max, Default;
        public string Unit;
        public string FallbackName; // taskbar values fall back to the window value
        public GlassValue(string name, string label, int min, int max, int def, string unit = "", string hint = null, string fallback = null)
        {
            Name = name; Label = label; Min = min; Max = max; Default = def; Unit = unit; Hint = hint; FallbackName = fallback;
        }
    }

    // Reads/writes OpenGlass settings in HKCU\Software\Microsoft\Windows\DWM and tells dwm.exe to reload them live.
    internal static class GlassSettings
    {
        public const string DwmKey = @"Software\Microsoft\Windows\DWM";
        public const string StudioKey = @"Software\LiquidGlassStudio";

        public static readonly GlassValue[] Window =
        {
            new GlassValue("LiquidGlassBlur", "Độ mờ thân kính", 0, 100, 85, "%", "0 = trong vắt, 100 = mờ hẳn"),
            new GlassValue("LiquidGlassRimBlur", "Độ mờ viền", 0, 100, 0, "%", "Viền trong thì thấy rõ khúc xạ"),
            new GlassValue("LiquidGlassFrost", "Độ nhám", 0, 32, 10, " px", "Làm mờ rộng hơn, như kính phun cát"),
            new GlassValue("LiquidGlassDim", "Độ tối", 0, 80, 15, "%", "Tối thân kính cho chữ dễ đọc"),
            new GlassValue("LiquidGlassRefraction", "Khúc xạ", 0, 80, 24, " px", "Độ bẻ cong hình phía sau ở viền"),
            new GlassValue("LiquidGlassBezel", "Độ dày viền cong", 2, 80, 18, " px"),
            new GlassValue("LiquidGlassChroma", "Tán sắc", 0, 60, 15, "%", "Viền cầu vồng đỏ / xanh ở mép"),
            new GlassValue("LiquidGlassSpecular", "Ánh sáng viền", 0, 200, 60, "%"),
            new GlassValue("LiquidGlassCornerRadius", "Bo góc kính", 0, 64, 8, " px"),
        };

        public static readonly GlassValue[] Taskbar =
        {
            new GlassValue("LiquidTaskbarBlur", "Độ mờ thân kính", 0, 100, 85, "%", null, "LiquidGlassBlur"),
            new GlassValue("LiquidTaskbarRimBlur", "Độ mờ viền", 0, 100, 0, "%", null, "LiquidGlassRimBlur"),
            new GlassValue("LiquidTaskbarFrost", "Độ nhám", 0, 32, 10, " px", null, "LiquidGlassFrost"),
            new GlassValue("LiquidTaskbarDim", "Độ tối", 0, 80, 15, "%", null, "LiquidGlassDim"),
            new GlassValue("LiquidTaskbarRefraction", "Khúc xạ", 0, 80, 24, " px", null, "LiquidGlassRefraction"),
            new GlassValue("LiquidTaskbarBezel", "Độ dày viền cong", 2, 80, 18, " px", null, "LiquidGlassBezel"),
            new GlassValue("LiquidTaskbarChroma", "Tán sắc", 0, 60, 15, "%", null, "LiquidGlassChroma"),
            new GlassValue("LiquidTaskbarSpecular", "Ánh sáng viền", 0, 200, 60, "%", null, "LiquidGlassSpecular"),
            new GlassValue("LiquidTaskbarCornerRadius", "Bo góc (khi không dùng Dock)", 0, 64, 8, " px", null, "LiquidGlassCornerRadius"),
        };

        // name -> values (suffix only; "Glass" or "Taskbar" prefix is added by Apply)
        public static readonly Dictionary<string, Dictionary<string, int>> Presets = new Dictionary<string, Dictionary<string, int>>
        {
            ["clear"] = new Dictionary<string, int> { ["Blur"] = 0, ["RimBlur"] = 0, ["Frost"] = 0, ["Dim"] = 0, ["Refraction"] = 28, ["Bezel"] = 20, ["Chroma"] = 18, ["Specular"] = 70 },
            ["soft"] = new Dictionary<string, int> { ["Blur"] = 60, ["RimBlur"] = 0, ["Frost"] = 6, ["Dim"] = 10, ["Refraction"] = 24, ["Bezel"] = 18, ["Chroma"] = 12, ["Specular"] = 60 },
            ["ios"] = new Dictionary<string, int> { ["Blur"] = 85, ["RimBlur"] = 0, ["Frost"] = 10, ["Dim"] = 15, ["Refraction"] = 24, ["Bezel"] = 18, ["Chroma"] = 15, ["Specular"] = 60 },
            ["heavy"] = new Dictionary<string, int> { ["Blur"] = 100, ["RimBlur"] = 20, ["Frost"] = 18, ["Dim"] = 25, ["Refraction"] = 20, ["Bezel"] = 16, ["Chroma"] = 10, ["Specular"] = 50 },
        };

        static int? ReadFrom(RegistryKey root, string key, string name)
        {
            try
            {
                using (var k = root.OpenSubKey(key))
                {
                    var v = k?.GetValue(name);
                    if (v is int i) return i;
                }
            }
            catch { }
            return null;
        }

        public static int? ReadRaw(string name) => ReadFrom(Registry.CurrentUser, DwmKey, name) ?? ReadFrom(Registry.LocalMachine, DwmKey, name);

        public static int Get(string name, int def) => ReadRaw(name) ?? def;

        public static int Get(GlassValue v)
        {
            var raw = ReadRaw(v.Name);
            if (raw.HasValue) return Clamp(raw.Value, v);
            if (v.FallbackName != null)
            {
                var w = Window.FirstOrDefault(x => x.Name == v.FallbackName);
                if (w != null) return Get(w);
            }
            return v.Default;
        }

        static int Clamp(int x, GlassValue v) => Math.Max(v.Min, Math.Min(v.Max, x));

        public static void Set(string name, int value)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(DwmKey))
                    k.SetValue(name, value, RegistryValueKind.DWord);
            }
            catch { }
            NotifyLater();
        }

        public static void Delete(string name)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(DwmKey, true))
                    k?.DeleteValue(name, false);
            }
            catch { }
            NotifyLater();
        }

        public static void ApplyPreset(string preset, bool taskbar)
        {
            var prefix = taskbar ? "LiquidTaskbar" : "LiquidGlass";
            foreach (var kv in Presets[preset])
                Set(prefix + kv.Key, kv.Value);
        }

        // ---- live reload: same messages the OpenGlass settings window uses ----
        static DispatcherTimer s_notify;
        public static void NotifyLater()
        {
            if (s_notify == null)
            {
                s_notify = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                s_notify.Tick += (s, e) => { s_notify.Stop(); NotifyNow(); };
            }
            s_notify.Stop();
            s_notify.Start();
        }

        public static void NotifyNow()
        {
            var dwm = Native.FindWindow("Dwm", null);
            if (dwm == IntPtr.Zero) return;
            Native.PostMessage(dwm, Native.WM_DWMCOLORIZATIONCOLORCHANGED, IntPtr.Zero, IntPtr.Zero);
            Native.SendMessageTimeout(dwm, Native.WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet", Native.SMTO_ABORTIFHUNG, 500, out _);
        }

        // ---- Studio's own settings ----
        public static int GetStudio(string name, int def)
        {
            return ReadFrom(Registry.CurrentUser, StudioKey, name) ?? def;
        }

        public static void SetStudio(string name, int value)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(StudioKey))
                    k.SetValue(name, value, RegistryValueKind.DWord);
            }
            catch { }
        }

        // ---- environment ----
        public static string OpenGlassState(out bool running)
        {
            running = false;
            try
            {
                using (var sc = new ServiceController("OpenGlassHost"))
                {
                    running = sc.Status == ServiceControllerStatus.Running;
                    return running ? "đang chạy" : "đã cài nhưng đang tắt";
                }
            }
            catch { return "chưa cài"; }
        }

        public static bool TranslucentTbRunning()
        {
            try { return Process.GetProcessesByName("TranslucentTB").Length > 0; } catch { return false; }
        }

        public static void RestartOpenGlass()
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -WindowStyle Hidden -Command \"Restart-Service OpenGlassHost -Force -ErrorAction SilentlyContinue; if ((Get-Service OpenGlassHost).Status -ne 'Running') { Start-Service OpenGlassHost }\"")
                { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
                Process.Start(psi);
            }
            catch { }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static bool StartsWithWindows
        {
            get
            {
                try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k?.GetValue("LiquidGlassStudio") != null; } catch { return false; }
            }
            set
            {
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                    {
                        if (value) k.SetValue("LiquidGlassStudio", "\"" + Process.GetCurrentProcess().MainModule.FileName + "\" --tray");
                        else k.DeleteValue("LiquidGlassStudio", false);
                    }
                }
                catch { }
            }
        }
    }
}
