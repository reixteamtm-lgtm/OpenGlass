using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;

namespace LiquidGlassStudio
{
    // Finds where the taskbar icons and the notification area are (UI Automation) and publishes the dock pill
    // rectangles in the shared memory block Local\LiquidGlassDock, which OpenGlass reads inside dwm.exe.
    // Runs on its own thread once every 1.5 s; when Studio quits, OpenGlass sees the stale heartbeat and
    // goes back to a full-width glass taskbar after 5 s.
    internal sealed class DockFeeder : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct SharedBlock
        {
            public uint Magic;
            public uint Version;
            public ulong Tick;
            public int Dock;
            public int Tray;
            public float TbL, TbT, TbR, TbB;
            public float DL, DT, DR, DB;
            public float TL, TT, TR, TB;
            public float Radius;
            public float Shadow;
            public float R0, R1;
        }

        const uint Magic = 0x4B44474Cu;

        IntPtr _mapping, _view;
        Thread _thread;
        volatile bool _stop;
        readonly AutoResetEvent _wake = new AutoResetEvent(false);

        public volatile bool DockEnabled;
        public volatile bool TrayEnabled;
        public volatile int Radius = 18;
        public volatile int Shadow = 35;   // percent
        public volatile int PadX = 8;
        public volatile int PadY = 3;
        public volatile string Status = "";
        public volatile bool FillUnderDock;   // maximized windows extend under the dock (work area = whole monitor)
        bool _workAreaChanged;

        public bool Start()
        {
            if (!CreateMapping()) return false;
            _thread = new Thread(Loop) { IsBackground = true, Name = "DockFeeder", Priority = ThreadPriority.BelowNormal };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
            return true;
        }

        public void Poke() => _wake.Set();

        bool CreateMapping()
        {
            IntPtr sd;
            uint sdSize;
            // owner: full access; everyone (incl. the DWM-n account): read; SYSTEM: full
            if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptor("D:(A;;GA;;;OW)(A;;GR;;;WD)(A;;GA;;;SY)", 1, out sd, out sdSize))
                sd = IntPtr.Zero;
            var sa = new Native.SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf(typeof(Native.SECURITY_ATTRIBUTES)), lpSecurityDescriptor = sd, bInheritHandle = 0 };
            _mapping = Native.CreateFileMapping(new IntPtr(-1), ref sa, Native.PAGE_READWRITE, 0, (uint)Marshal.SizeOf(typeof(SharedBlock)), @"Local\LiquidGlassDock");
            if (sd != IntPtr.Zero) Native.LocalFree(sd);
            if (_mapping == IntPtr.Zero) { Status = "Không tạo được vùng nhớ chung (lỗi " + Marshal.GetLastWin32Error() + ")"; return false; }
            _view = Native.MapViewOfFile(_mapping, Native.FILE_MAP_ALL_ACCESS, 0, 0, (UIntPtr)(uint)Marshal.SizeOf(typeof(SharedBlock)));
            if (_view == IntPtr.Zero) { Native.CloseHandle(_mapping); _mapping = IntPtr.Zero; return false; }
            return true;
        }

        void Loop()
        {
            while (!_stop)
            {
                try { Publish(); }
                catch (Exception ex) { Status = "Lỗi: " + ex.Message; }
                _wake.WaitOne(1500);
            }
            // tell OpenGlass immediately that the dock is gone, and give the taskbar its space back
            try { Write(new SharedBlock { Magic = Magic, Version = 1, Tick = Native.GetTickCount64() }); } catch { }
            try
            {
                if (_workAreaChanged)
                {
                    var h = Native.FindWindow("Shell_TrayWnd", null);
                    var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO)) };
                    if (h != IntPtr.Zero && Native.GetWindowRect(h, out var tb) && Native.GetMonitorInfo(Native.MonitorFromWindow(h, 2), ref mi))
                        RestoreWorkArea(mi.rcMonitor, tb);
                }
            }
            catch { }
        }

        AutomationElement _root;
        IntPtr _rootHwnd;
        static readonly System.Windows.Automation.Condition ButtonCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);

        // macOS-like: with the dock on, maximized windows reach the bottom of the screen and the dock floats over them.
        // Explorer recalculates the work area now and then, so this is re-checked on every pass.
        void UpdateWorkArea(IntPtr taskbar, Native.RECT tb)
        {
            var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO)) };
            if (!Native.GetMonitorInfo(Native.MonitorFromWindow(taskbar, 2), ref mi)) return;
            var cur = new Native.RECT();
            Native.SystemParametersInfo(Native.SPI_GETWORKAREA, 0, ref cur, 0);
            var mon = mi.rcMonitor;
            if (FillUnderDock && DockEnabled)
            {
                if (cur.Left != mon.Left || cur.Top != mon.Top || cur.Right != mon.Right || cur.Bottom != mon.Bottom)
                {
                    Native.SystemParametersInfo(Native.SPI_SETWORKAREA, 0, ref mon, Native.SPIF_SENDCHANGE);
                    _workAreaChanged = true;
                }
            }
            else if (_workAreaChanged)
            {
                RestoreWorkArea(mon, tb);
            }
        }

        void RestoreWorkArea(Native.RECT mon, Native.RECT tb)
        {
            var work = mon;
            int tw = tb.Right - tb.Left, th = tb.Bottom - tb.Top;
            if (tw >= th) { if (tb.Top > mon.Top) work.Bottom = tb.Top; else work.Top = tb.Bottom; }
            else { if (tb.Left > mon.Left) work.Right = tb.Left; else work.Left = tb.Right; }
            Native.SystemParametersInfo(Native.SPI_SETWORKAREA, 0, ref work, Native.SPIF_SENDCHANGE);
            _workAreaChanged = false;
        }

        void Publish()
        {
            var block = new SharedBlock { Magic = Magic, Version = 1, Tick = Native.GetTickCount64() };
            var hwnd = Native.FindWindow("Shell_TrayWnd", null);
            if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var tb))
            {
                Status = "Không tìm thấy taskbar";
                Write(block);
                return;
            }
            try { UpdateWorkArea(hwnd, tb); } catch { }
            block.TbL = tb.Left; block.TbT = tb.Top; block.TbR = tb.Right; block.TbB = tb.Bottom;

            if (!DockEnabled)
            {
                Status = "Dock đang tắt: taskbar là một tấm kính dài";
                Write(block);
                return;
            }

            if (_root == null || _rootHwnd != hwnd)
            {
                _root = AutomationElement.FromHandle(hwnd);
                _rootHwnd = hwnd;
            }

            Rect dock = Rect.Empty, tray = Rect.Empty;
            int apps = 0;
            var tbRect = new Rect(tb.Left, tb.Top, tb.Right - tb.Left, tb.Bottom - tb.Top);
            AutomationElementCollection buttons;
            try { buttons = _root.FindAll(TreeScope.Descendants, ButtonCondition); }
            catch { _root = null; throw; }
            foreach (AutomationElement el in buttons)
            {
                Rect r;
                string cls, id;
                try
                {
                    var cur = el.Current;
                    if (cur.IsOffscreen) continue;
                    r = cur.BoundingRectangle;
                    cls = cur.ClassName ?? "";
                    id = cur.AutomationId ?? "";
                }
                catch { continue; }
                if (r.IsEmpty || r.Width < 4 || r.Height < 4) continue;
                if (!tbRect.Contains(new Point(r.X + r.Width / 2, r.Y + r.Height / 2))) continue;
                if (cls.IndexOf("ShowDesktop", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                bool isTray = cls.StartsWith("SystemTray.", StringComparison.OrdinalIgnoreCase)
                    || id.IndexOf("SystemTray", StringComparison.OrdinalIgnoreCase) >= 0
                    || id.IndexOf("NotifyItemIcon", StringComparison.OrdinalIgnoreCase) >= 0
                    || cls.IndexOf("TrayButton", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isTray) tray.Union(r);
                else { dock.Union(r); apps++; }
            }

            if (dock.IsEmpty)
            {
                Status = "Chưa đọc được vị trí icon trên taskbar";
                Write(block);
                return;
            }

            float h = tb.Bottom - tb.Top;
            block.Dock = 1;
            block.Radius = Radius;
            block.Shadow = Shadow / 100f;
            SetRel(ref block.DL, ref block.DT, ref block.DR, ref block.DB, dock, tb, h);
            if (TrayEnabled && !tray.IsEmpty)
            {
                block.Tray = 1;
                SetRel(ref block.TL, ref block.TT, ref block.TR, ref block.TB, tray, tb, h);
            }
            Status = "Dock: " + apps + " nút, rộng " + (int)(block.DR - block.DL) + " px" + (block.Tray == 1 ? ", có ô khay hệ thống" : "");
            Write(block);
        }

        void SetRel(ref float l, ref float t, ref float r, ref float b, Rect area, Native.RECT tb, float h)
        {
            l = (float)(area.Left - tb.Left - PadX);
            r = (float)(area.Right - tb.Left + PadX);
            t = Math.Max(1f, (float)(area.Top - tb.Top - PadY));
            b = Math.Min(h - 1f, (float)(area.Bottom - tb.Top + PadY));
            l = Math.Max(1f, l);
            r = Math.Min(tb.Right - tb.Left - 1f, r);
        }

        void Write(SharedBlock block)
        {
            if (_view == IntPtr.Zero) return;
            Marshal.StructureToPtr(block, _view, false);
        }

        public void Dispose()
        {
            _stop = true;
            _wake.Set();
            try { _thread?.Join(2000); } catch { }
            if (_view != IntPtr.Zero) Native.UnmapViewOfFile(_view);
            if (_mapping != IntPtr.Zero) Native.CloseHandle(_mapping);
            _view = _mapping = IntPtr.Zero;
        }
    }
}
