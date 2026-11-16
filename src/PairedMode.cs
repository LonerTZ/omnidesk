using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace OmniDesk
{
    // Paired desktops: with several monitors, monitor 0 shows desktop S, monitor 1 shows S+1, and so on.
    //
    // Windows shows one real desktop at a time, so one real desktop (the "stage", always desktop S) is used to
    // show them all. The windows of desktops S+1, S+2... are borrowed onto the stage and placed on their monitor,
    // and sent back to their own desktop when the view moves on. Nothing about who-belongs-where is stored: a
    // window on the stage that sits on monitor m belongs to desktop S+m, so the state can always be rebuilt from
    // where the windows physically are.
    sealed class PairedMode
    {
        // For tests only: when set, no window outside this filter is ever touched.
        public static Func<IntPtr, bool> TestFilter;

        sealed class Item
        {
            public IntPtr H;
            public int Real;        // desktop the window is on right now
            public int Home;        // desktop it belongs to
            public Rectangle Orig;
            public bool Max;
            public bool Moved;
            public double Fx, Fy;   // centre as a fraction of the monitor it is on
        }

        readonly Action<string> _log;
        readonly Action<string> _notify;
        readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        readonly ManualResetEvent _ready = new ManualResetEvent(false);
        readonly int _pid = Process.GetCurrentProcess().Id;
        Thread _thread;
        VirtualDesktops _vd;

        volatile bool _wanted;
        volatile bool _active;
        volatile bool _atLeft;
        volatile bool _atRight;
        volatile bool _stop;
        int _s, _c, _m;

        public PairedMode(Action<string> log, Action<string> notify)
        {
            _log = log ?? delegate { };
            _notify = notify ?? delegate { };
            _thread = new Thread(Run) { IsBackground = true, Name = "OmniDesk paired" };
            _thread.SetApartmentState(ApartmentState.STA); // the desktop interfaces are COM
            _thread.Start();
        }

        // ---- what the key hook needs (cheap, never blocks) ----

        public bool Active { get { return _active; } }
        public bool AtLeftEdge { get { return _atLeft; } }
        public bool AtRightEdge { get { return _atRight; } }

        public bool Available
        {
            get { return _ready.WaitOne(5000) && _vd != null && _vd.Available; }
        }

        // ---- requests (queued onto the worker thread) ----

        public void SetWanted(bool wanted)
        {
            _queue.Add(delegate { _wanted = wanted; Reconcile(); });
        }

        public void Shift(int dir)
        {
            _queue.Add(delegate { if (_active) ShiftTo(_s + dir); });
        }

        // Land on the first pair of desktops when travelling right, the last pair when travelling left.
        public void Arrive(int dir)
        {
            _queue.Add(delegate { if (_active) ShiftTo(dir > 0 ? 0 : _c - _m); });
        }

        public void Shutdown()
        {
            var done = new ManualResetEvent(false);
            _queue.Add(delegate
            {
                _wanted = false;
                if (_active) Pack();
                _active = false;
                _stop = true;
                done.Set();
            });
            done.WaitOne(5000);
        }

        // ---- worker ----

        void Run()
        {
            _vd = new VirtualDesktops(_log);
            _ready.Set();
            while (!_stop)
            {
                Action a;
                try
                {
                    if (_queue.TryTake(out a, 500)) Safely(a);
                    else Safely(Tick);
                }
                catch (Exception e) { _log("paired worker: " + e.Message); }
            }
        }

        void Safely(Action a)
        {
            try { a(); }
            catch (Exception e)
            {
                _log("paired mode failed: " + e);
                try { if (_active) Pack(); } catch { }
                _active = false;
                _wanted = false;
                _atLeft = _atRight = false;
                _notify("Paired desktops stopped after an error. Your windows were sent back to their own desktops.");
            }
        }

        static Screen[] Monitors()
        {
            Screen[] s = Screen.AllScreens;
            Array.Sort(s, delegate(Screen a, Screen b)
            {
                int c = a.Bounds.X.CompareTo(b.Bounds.X);
                return c != 0 ? c : a.Bounds.Y.CompareTo(b.Bounds.Y);
            });
            return s;
        }

        void UpdateEdges()
        {
            _atLeft = _s <= 0;
            _atRight = _s >= _c - _m;
        }

        // Start or stop to match what the user wants and how many monitors are connected.
        void Reconcile()
        {
            bool shouldRun = _wanted && _vd.Available && Monitors().Length >= 2;
            if (shouldRun && !_active) Activate();
            else if (!shouldRun && _active)
            {
                Pack();
                _active = false;
                _atLeft = _atRight = false;
                _log("paired mode paused");
            }
        }

        void Activate()
        {
            Screen[] mons = Monitors();
            int c = _vd.Count;
            if (c < mons.Length)
            {
                _wanted = false;
                _notify("Paired desktops needs at least " + mons.Length + " desktops, one per monitor. You have " + c + ".");
                return;
            }

            int cur = _vd.Current();
            if (cur < 0) return;
            _c = c;
            _m = mons.Length;
            _s = cur;
            _active = true;
            _log("paired mode on: " + _m + " monitors, " + _c + " desktops, stage " + _s);

            int target = Math.Min(cur, _c - _m);
            if (target != cur) ShiftCore(cur, target);
            UpdateEdges();
        }

        // Runs every half second: reacts to plugging monitors, changing desktop count, or the view
        // being moved by something else (Task View, a touchpad swipe).
        void Tick()
        {
            if (!_vd.Available) return;
            Reconcile();
            if (!_active) return;

            if (_vd.Count != _c || Monitors().Length != _m)
            {
                Pack();
                _active = false;
                Activate();
                return;
            }

            int cur = _vd.Current();
            if (cur >= 0 && cur != _s)
            {
                _log("desktop changed from outside, " + _s + " -> " + cur);
                ShiftCore(_s, Math.Max(0, Math.Min(cur, _c - _m)));
            }
        }

        void ShiftTo(int target)
        {
            target = Math.Max(0, Math.Min(target, _c - _m));
            if (target == _s) { UpdateEdges(); return; }
            ShiftCore(_s, target);
        }

        // Move the view from desktop oldS to newS.
        void ShiftCore(int oldS, int newS)
        {
            Screen[] mons = Monitors();
            Guid[] ids = _vd.Ids();
            List<Item> wins = Collect(oldS, mons, ids);

            // 1. Bring every window that belongs in the new view onto the new stage and place it on its monitor.
            //    The new stage is not on screen yet, so none of this is seen.
            var moved = new List<Item>();
            foreach (Item w in wins)
            {
                if (w.Home < newS || w.Home >= newS + mons.Length) continue;
                if (w.Real != newS && !_vd.Move(w.H, newS)) continue;
                Place(w, mons[w.Home - newS]);
                w.Moved = true;
                moved.Add(w);
            }

            // 2. Show the new stage.
            if (_vd.Current() != newS && !_vd.Switch(newS))
            {
                _log("could not switch desktop; undoing");
                foreach (Item w in moved)
                {
                    if (w.Real != newS) _vd.Move(w.H, w.Real);
                    WinApi.Place(w.H, w.Orig, w.Max);
                }
                return;
            }

            // 3. Windows left on the old stage that do not belong there go back to their own desktop.
            foreach (Item w in wins)
                if (!w.Moved && w.Real != w.Home) _vd.Move(w.H, w.Home);

            _s = newS;
            UpdateEdges();
            _log("view moved to desktops " + newS + ".." + (newS + mons.Length - 1));
        }

        // Send borrowed windows back to their own desktops, leaving them where they are on screen.
        void Pack()
        {
            if (_s < 0 || _s >= _vd.Count) return;
            Screen[] mons = Monitors();
            foreach (Item w in Collect(_s, mons, _vd.Ids()))
                if (w.Real == _s && w.Home != _s) _vd.Move(w.H, w.Home);
        }

        List<Item> Collect(int stage, Screen[] mons, Guid[] ids)
        {
            var list = new List<Item>();
            foreach (IntPtr h in WinApi.TopLevel())
            {
                if (!WinApi.Eligible(h, _pid)) continue;
                if (TestFilter != null && !TestFilter(h)) continue;
                if (WinApi.IsMinimized(h)) continue;

                int real = _vd.IndexOfWindow(h, ids);
                if (real < 0) continue;

                Rectangle r = WinApi.Rect(h);
                if (r.Width <= 0 || r.Height <= 0) continue;
                int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                int mi = NearestMonitor(mons, cx, cy);
                Rectangle b = mons[mi].Bounds;

                var it = new Item
                {
                    H = h,
                    Real = real,
                    Orig = r,
                    Max = WinApi.IsMaximized(h),
                    Fx = Clamp01((cx - b.X) / (double)b.Width),
                    Fy = Clamp01((cy - b.Y) / (double)b.Height)
                };
                // On the stage, the monitor a window is on says which desktop it belongs to.
                it.Home = real == stage ? Math.Min(stage + mi, _c - 1) : real;
                list.Add(it);
            }
            return list;
        }

        static double Clamp01(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }

        static int NearestMonitor(Screen[] mons, int x, int y)
        {
            int best = 0;
            long bestDist = long.MaxValue;
            for (int i = 0; i < mons.Length; i++)
            {
                Rectangle b = mons[i].Bounds;
                int dx = x < b.Left ? b.Left - x : (x > b.Right ? x - b.Right : 0);
                int dy = y < b.Top ? b.Top - y : (y > b.Bottom ? y - b.Bottom : 0);
                long d = (long)dx * dx + (long)dy * dy;
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return best;
        }

        // Keep the window's size (shrunk to fit) and put its centre at the same relative spot on the target monitor.
        static void Place(Item w, Screen mon)
        {
            Rectangle b = mon.Bounds;
            Rectangle wa = mon.WorkingArea;
            int width = w.Orig.Width, height = w.Orig.Height;
            if (w.Max) { width = wa.Width * 3 / 4; height = wa.Height * 3 / 4; }
            width = Math.Min(width, wa.Width);
            height = Math.Min(height, wa.Height);

            int x = b.X + (int)(w.Fx * b.Width) - width / 2;
            int y = b.Y + (int)(w.Fy * b.Height) - height / 2;
            x = Math.Max(wa.Left, Math.Min(x, wa.Right - width));
            y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - height));

            WinApi.Place(w.H, new Rectangle(x, y, width, height), w.Max);
        }
    }
}
