using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OmniDesk
{
    // Per-session helper: watches Ctrl+Win+Left/Right, swaps accounts when pressed past the last/first
    // virtual desktop, and applies the optional "nearest end desktop" arrival after an unlock.
    class Agent : Form
    {
        const int WH_KEYBOARD_LL = 13;
        const int VK_LEFT = 0x25;
        const int VK_RIGHT = 0x27;
        const int VK_CONTROL = 0x11;
        const int VK_LWIN = 0x5B;
        const int VK_RWIN = 0x5C;
        const int VK_LCONTROL = 0xA2;
        const int VK_MASK = 0xE8; // unassigned key, used so Start does not open when the Win release follows a swallowed key
        const uint LLKHF_INJECTED = 0x10;
        const uint KEYEVENTF_EXTENDEDKEY = 0x1;
        const uint KEYEVENTF_KEYUP = 0x2;

        const int WTS_CONSOLE_CONNECT = 0x1;

        const string SettingsKey = @"Software\OmniDesk";
        const string DesktopsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

        static readonly TimeSpan ArrivalTtl = TimeSpan.FromSeconds(60);

        static bool _debug;
        static string LogPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OmniDesk", "agent.log");
            }
        }

        // Per-key detail, only with --debug.
        static void Log(string line)
        {
            if (_debug) Shared.Log(LogPath, line);
        }

        // Lifecycle and failures, always kept so a stopped agent leaves a trace.
        static void LogAlways(string line)
        {
            Shared.Log(LogPath, line);
        }

        public static int Run(bool debug)
        {
            _debug = debug;
            bool created;
            using (var mutex = new Mutex(true, @"Local\OmniDeskAgent", out created))
            {
                if (!created) return 0;

                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
                {
                    LogAlways("ui thread exception: " + e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    LogAlways("unhandled exception: " + e.ExceptionObject);
                };

                // A starved hook callback is silently dropped by Windows, so give this process a little headroom.
                try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }

                // Without this Windows scales the coordinates this process sees on high-DPI monitors, and any window
                // this process places would land in the wrong spot. Must come before any window exists.
                try
                {
                    if (!Native.SetProcessDpiAwarenessContext(new IntPtr(-4))) // per-monitor v2
                        Native.SetProcessDPIAware();
                }
                catch { try { Native.SetProcessDPIAware(); } catch { } }

                Application.EnableVisualStyles();
                Application.Run(new Agent());
                LogAlways("agent exited");
            }
            return 0;
        }

        readonly Native.HookProc _proc;
        IntPtr _hook = IntPtr.Zero;
        NotifyIcon _tray;
        ToolStripMenuItem _arrivalItem;
        ToolStripMenuItem _mouseItem;
        readonly Native.HookProc _mouseProc;
        IntPtr _mouseHook = IntPtr.Zero;
        volatile bool _mouseOn;
        int[] _mouseMap = new int[5];
        readonly bool[] _mouseHeld = new bool[5];
        long _tiltAt;
        volatile Action<int> _detect;
        System.Windows.Forms.Timer _watchdog;
        bool _heldLeft, _heldRight;
        long _swallowedAt;

        Agent()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            WindowState = FormWindowState.Minimized;
            Opacity = 0;

            _arrivalItem = new ToolStripMenuItem("Land on nearest end desktop");
            _arrivalItem.CheckOnClick = true;
            _arrivalItem.Checked = NearestEnd;
            _arrivalItem.CheckedChanged += delegate { NearestEnd = _arrivalItem.Checked; };

            _mouseItem = new ToolStripMenuItem("Mouse buttons switch desktops");
            _mouseItem.CheckOnClick = true;
            _mouseItem.Checked = MouseSettings.Enabled;
            _mouseItem.CheckedChanged += delegate { MouseSettings.Enabled = _mouseItem.Checked; LoadMouse(); };
            var mouseChoose = new ToolStripMenuItem("Choose mouse buttons...", null, delegate { ChooseMouseButtons(); });

            var menu = new ContextMenuStrip();
            menu.Items.Add(_mouseItem);
            menu.Items.Add(mouseChoose);
            menu.Items.Add(_arrivalItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, delegate { Quit(); });

            _tray = new NotifyIcon();
            _tray.Icon = LoadTrayIcon();
            _tray.Text = "OmniDesk";
            _tray.ContextMenuStrip = menu;
            _tray.Visible = true;

            _proc = Hook;
            InstallHook();
            _mouseProc = MouseHook;
            LoadMouse();

            // Warm up the registry and marshalling code so the first real key press is not slow.
            int i, c;
            Desktops(out i, out c);

            // Windows drops a keyboard hook without telling anyone if it ever answers too slowly,
            // so put it back regularly instead of trusting that it is still there.
            _watchdog = new System.Windows.Forms.Timer();
            _watchdog.Interval = 60 * 1000;
            _watchdog.Tick += delegate { InstallHook(); if (_mouseHook != IntPtr.Zero) InstallMouseHook(); };
            _watchdog.Start();

            LogAlways("agent started in session " + Process.GetCurrentProcess().SessionId);
        }

        static Icon LoadTrayIcon()
        {
            try
            {
                using (Stream s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("OmniDesk.ico"))
                    if (s != null) return new Icon(s, SystemInformation.SmallIconSize);
            }
            catch { }
            return SystemIcons.Application;
        }

        void InstallHook()
        {
            if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
            _hook = Native.SetWindowsHookEx(WH_KEYBOARD_LL, _proc,
                Native.GetModuleHandle(Process.GetCurrentProcess().MainModule.ModuleName), 0);
            if (_hook == IntPtr.Zero)
                LogAlways("could not install keyboard hook (error " + Marshal.GetLastWin32Error() + ")");
            else
                Log("keyboard hook installed");
        }

        // ---- mouse buttons ----

        const int WH_MOUSE_LL = 14;
        const int WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208;
        const int WM_XBUTTONDOWN = 0x20B, WM_XBUTTONUP = 0x20C;
        const int WM_MOUSEHWHEEL = 0x20E;
        const uint LLMHF_INJECTED = 0x1;

        void LoadMouse()
        {
            _mouseMap = MouseSettings.LoadActions();
            _mouseOn = MouseSettings.Enabled;
            UpdateMouseHook();
        }

        // The mouse hook sees every mouse movement, so it is only installed while something needs it.
        void UpdateMouseHook()
        {
            bool needed = (_mouseOn && MouseSettings.AnyMapped(_mouseMap)) || _detect != null;
            if (needed) { if (_mouseHook == IntPtr.Zero) InstallMouseHook(); }
            else RemoveMouseHook();
        }

        void InstallMouseHook()
        {
            RemoveMouseHook();
            _mouseHook = Native.SetWindowsHookEx(WH_MOUSE_LL, _mouseProc,
                Native.GetModuleHandle(Process.GetCurrentProcess().MainModule.ModuleName), 0);
            if (_mouseHook == IntPtr.Zero)
                LogAlways("could not install mouse hook (error " + Marshal.GetLastWin32Error() + ")");
        }

        void RemoveMouseHook()
        {
            if (_mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
            for (int i = 0; i < _mouseHeld.Length; i++) _mouseHeld[i] = false;
        }

        IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                int msg = wParam.ToInt32();
                // Mouse movement is by far the most common message: leave it alone straight away.
                if (msg == WM_MBUTTONDOWN || msg == WM_MBUTTONUP || msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP || msg == WM_MOUSEHWHEEL)
                {
                    try
                    {
                        uint flags = (uint)Marshal.ReadInt32(lParam, 12);   // MSLLHOOKSTRUCT.flags
                        if ((flags & LLMHF_INJECTED) == 0)
                        {
                            int data = Marshal.ReadInt32(lParam, 8);        // MSLLHOOKSTRUCT.mouseData
                            int id = -1;
                            bool down = false, up = false;
                            switch (msg)
                            {
                                case WM_MBUTTONDOWN: id = MouseSettings.Middle; down = true; break;
                                case WM_MBUTTONUP: id = MouseSettings.Middle; up = true; break;
                                case WM_XBUTTONDOWN: id = ((data >> 16) & 0xFFFF) == 1 ? MouseSettings.Side1 : MouseSettings.Side2; down = true; break;
                                case WM_XBUTTONUP: id = ((data >> 16) & 0xFFFF) == 1 ? MouseSettings.Side1 : MouseSettings.Side2; up = true; break;
                                case WM_MOUSEHWHEEL: id = (short)((data >> 16) & 0xFFFF) < 0 ? MouseSettings.TiltLeft : MouseSettings.TiltRight; down = true; break;
                            }
                            if (id >= 0 && HandleMouse(id, down, up, msg == WM_MOUSEHWHEEL))
                                return (IntPtr)1;
                        }
                    }
                    catch (Exception e)
                    {
                        LogAlways("mouse hook error: " + e.Message);
                    }
                }
            }
            return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
        }

        // Returns true when the event should be swallowed.
        bool HandleMouse(int id, bool down, bool up, bool isTilt)
        {
            Action<int> detect = _detect;
            if (detect != null && down)
            {
                // The settings window is waiting to be told which button this is.
                _detect = null;
                _mouseHeld[id] = !isTilt;
                BeginInvoke(new Action(delegate { detect(id); UpdateMouseHook(); }));
                return true;
            }

            if (up)
            {
                if (!_mouseHeld[id]) return false;
                _mouseHeld[id] = false;
                return true; // the matching press was swallowed
            }

            int action = _mouseMap[id];
            if (!down || !_mouseOn || action == MouseSettings.None) return false;

            if (isTilt)
            {
                // A tilt can arrive as several events; treat a burst as one.
                long now = Stopwatch.GetTimestamp();
                if (now - _tiltAt < Stopwatch.Frequency / 4) return true;
                _tiltAt = now;
            }
            else
            {
                _mouseHeld[id] = true;
            }

            int dir = action == MouseSettings.Next ? 1 : -1;
            bool ctrl = IsDown(VK_CONTROL);   // read now: the worker thread runs a moment later
            ThreadPool.QueueUserWorkItem(delegate { SwitchBy(dir, ctrl); });
            return true;
        }

        // A plain press switches desktop and stops at the last/first one. Holding Ctrl at that edge
        // swaps accounts, so a stray extra click can never throw you onto another user's lock screen.
        void SwitchBy(int dir, bool ctrl)
        {
            try
            {
                bool edge = AtEdge(dir);
                Log("mouse switch, dir " + dir + (edge ? " (edge" + (ctrl ? ", ctrl)" : ")") : ""));

                if (!edge) SendCombo(dir > 0 ? VK_RIGHT : VK_LEFT);
                else if (ctrl) Swap(dir);
            }
            catch (Exception e)
            {
                LogAlways("mouse switch failed: " + e.Message);
            }
        }

        void ChooseMouseButtons()
        {
            using (var form = new MouseSettingsForm(
                MouseSettings.LoadActions(),
                delegate(Action<int> found) { _detect = found; UpdateMouseHook(); },
                delegate { _detect = null; UpdateMouseHook(); }))
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    MouseSettings.SaveActions(form.Actions);
                    LoadMouse();
                }
            }
        }

        void Quit()
        {
            // Tell the service this was deliberate so it does not start the agent again.
            try { File.WriteAllText(Shared.QuitFile(Process.GetCurrentProcess().SessionId), "quit"); } catch { }
            Close();
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.WTSRegisterSessionNotification(Handle, 0); // NOTIFY_FOR_THIS_SESSION
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_watchdog != null) _watchdog.Stop();
            if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
            RemoveMouseHook();
            Native.WTSUnRegisterSessionNotification(Handle);
            _tray.Visible = false;
            base.OnFormClosed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_WTSSESSION_CHANGE)
            {
                int what = m.WParam.ToInt32();
                if (what == Native.WTS_SESSION_UNLOCK || what == WTS_CONSOLE_CONNECT)
                {
                    _heldLeft = _heldRight = false;
                    InstallHook();
                    if (_mouseHook != IntPtr.Zero) InstallMouseHook();
                }
                if (what == Native.WTS_SESSION_UNLOCK) OnUnlock();
            }
            base.WndProc(ref m);
        }

        // ---- settings ----

        static bool NearestEnd
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(SettingsKey))
                        return k == null || Convert.ToInt32(k.GetValue("ArrivalNearestEnd", 1)) != 0; // on unless the user turned it off
                }
                catch { return true; }
            }
            set
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(SettingsKey))
                        k.SetValue("ArrivalNearestEnd", value ? 1 : 0, RegistryValueKind.DWord);
                }
                catch { }
            }
        }

        // ---- virtual desktops (read from the registry, not the undocumented COM API) ----

        static bool Desktops(out int index, out int count)
        {
            index = 0;
            count = 1;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(DesktopsKey))
                {
                    byte[] ids = k == null ? null : k.GetValue("VirtualDesktopIDs") as byte[];
                    if (ids == null || ids.Length < 32) return true; // zero or one desktop
                    byte[] cur = k.GetValue("CurrentVirtualDesktop") as byte[];
                    if (cur == null || cur.Length != 16) return false;
                    count = ids.Length / 16;
                    for (int i = 0; i < count; i++)
                    {
                        bool match = true;
                        for (int b = 0; b < 16; b++)
                            if (ids[i * 16 + b] != cur[b]) { match = false; break; }
                        if (match) { index = i; return true; }
                    }
                    return false;
                }
            }
            catch { return false; }
        }

        static bool AtEdge(int dir)
        {
            int index, count;
            if (!Desktops(out index, out count)) return false;
            return dir > 0 ? index == count - 1 : index == 0;
        }

        // ---- keyboard hook ----

        static bool IsDown(int vk)
        {
            return (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        // Runs for every key on the machine, so it must stay quick and must never throw.
        IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                try
                {
                    int vk = Marshal.ReadInt32(lParam, 0);       // KBDLLHOOKSTRUCT.vkCode
                    if (vk == VK_LEFT || vk == VK_RIGHT)
                    {
                        uint flags = (uint)Marshal.ReadInt32(lParam, 8); // KBDLLHOOKSTRUCT.flags
                        if ((flags & LLKHF_INJECTED) == 0 && HandleArrow(vk, wParam.ToInt32()))
                            return (IntPtr)1;
                    }
                }
                catch (Exception e)
                {
                    LogAlways("hook error: " + e.Message);
                }
            }
            return Native.CallNextHookEx(_hook, code, wParam, lParam);
        }

        // Returns true when the key should be swallowed.
        bool HandleArrow(int vk, int msg)
        {
            bool down = msg == 0x100 || msg == 0x104;
            bool up = msg == 0x101 || msg == 0x105;
            int dir = vk == VK_RIGHT ? 1 : -1;
            bool held = dir > 0 ? _heldRight : _heldLeft;

            if (down && held && Stopwatch.GetTimestamp() - _swallowedAt < Stopwatch.Frequency)
                return true; // auto-repeat of a press we already swallowed

            if (down && IsDown(VK_CONTROL) && (IsDown(VK_LWIN) || IsDown(VK_RWIN)) && AtEdge(dir))
            {
                if (dir > 0) _heldRight = true; else _heldLeft = true;
                _swallowedAt = Stopwatch.GetTimestamp();
                Log("edge hit, dir " + dir);
                SendMask();
                ThreadPool.QueueUserWorkItem(delegate { Swap(dir); });
                return true;
            }

            if (up && held)
            {
                if (dir > 0) _heldRight = false; else _heldLeft = false;
                return true;
            }
            return false;
        }

        static void Swap(int dir)
        {
            try
            {
                string reply;
                using (var c = new NamedPipeClientStream(".", Shared.PipeName, PipeDirection.InOut))
                {
                    c.Connect(1000);
                    var w = new StreamWriter(c, new UTF8Encoding(false)) { AutoFlush = true };
                    w.WriteLine(dir > 0 ? "SWAP +1" : "SWAP -1");
                    var r = new StreamReader(c, new UTF8Encoding(false));
                    reply = r.ReadLine();
                }
                Log("service replied: " + reply);
                if (reply != null && reply.StartsWith("OK"))
                    Native.LockWorkStation(); // leave this session locked behind us
            }
            catch (Exception e)
            {
                LogAlways("swap request failed: " + e.Message);
            }
        }

        // ---- arrival ----

        void OnUnlock()
        {
            int sid = Process.GetCurrentProcess().SessionId;
            string f = Shared.ArrivalFile(sid);
            string text;
            try
            {
                if (!File.Exists(f)) return;
                text = File.ReadAllText(f);
                File.Delete(f);
            }
            catch { return; }

            string[] parts = text.Trim().Split(' ');
            long ticks;
            if (parts.Length != 2 || !long.TryParse(parts[1], out ticks)) return;
            if (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > ArrivalTtl) { Log("arrival note expired"); return; }
            if (!NearestEnd) return;

            int dir = parts[0] == "+1" ? 1 : -1;
            int index, count;
            if (!Desktops(out index, out count) || count < 2) return;

            // Travelled right: land on the first desktop. Travelled left: land on the last.
            int presses = dir > 0 ? index : count - 1 - index;
            int arrow = dir > 0 ? VK_LEFT : VK_RIGHT;
            Log("arrival: dir " + dir + ", desktop " + index + "/" + count + ", " + presses + " presses");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Thread.Sleep(800); // let the shell settle after unlock
                    for (int i = 0; i < presses; i++)
                    {
                        SendCombo(arrow);
                        Thread.Sleep(250);
                    }
                }
                catch (Exception e)
                {
                    LogAlways("arrival failed: " + e.Message);
                }
            });
        }

        // ---- synthetic input (flagged injected, so our own hook ignores it) ----

        static Native.INPUT Key(int vk, bool up, bool extended)
        {
            var i = new Native.INPUT();
            i.type = 1; // INPUT_KEYBOARD
            i.u.ki.wVk = (ushort)vk;
            i.u.ki.dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (extended ? KEYEVENTF_EXTENDEDKEY : 0);
            return i;
        }

        static void Send(params Native.INPUT[] inputs)
        {
            Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        static void SendMask()
        {
            Send(Key(VK_MASK, false, false), Key(VK_MASK, true, false));
        }

        static void SendCombo(int arrow)
        {
            Send(
                Key(VK_LCONTROL, false, false), Key(VK_LWIN, false, false),
                Key(arrow, false, true), Key(arrow, true, true),
                Key(VK_LWIN, true, false), Key(VK_LCONTROL, true, false));
        }
    }
}
