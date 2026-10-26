using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace OmniDesk
{
    // Runs inside the service. Keeps one agent alive in every signed-in session, so an agent that
    // was killed, crashed, or left behind by an update comes back without the user doing anything.
    class Supervisor
    {
        static readonly string LogPath = Path.Combine(Shared.DataDir, "service.log");
        static readonly TimeSpan RelaunchGap = TimeSpan.FromSeconds(20);
        const uint CREATE_UNICODE_ENVIRONMENT = 0x400;

        readonly Dictionary<int, DateTime> _launched = new Dictionary<int, DateTime>();
        Thread _thread;
        volatile bool _stop;

        public void Start()
        {
            _thread = new Thread(Loop) { IsBackground = true };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
        }

        void Loop()
        {
            ClearQuitMarkers(null);
            while (!_stop)
            {
                try { Tick(); }
                catch (Exception e) { Shared.Log(LogPath, "supervisor error: " + e.Message); }
                for (int i = 0; i < 10 && !_stop; i++) Thread.Sleep(500);
            }
        }

        void Tick()
        {
            string appExe = Process.GetCurrentProcess().MainModule.FileName;

            // Which sessions already have an agent, and which have a desktop shell up.
            var hasAgent = new HashSet<int>();
            var hasShell = new HashSet<int>();

            foreach (Process p in Process.GetProcessesByName("OmniDesk"))
            {
                using (p)
                {
                    if (p.SessionId == 0) continue; // the service itself
                    if (IsStale(p, appExe))
                    {
                        // Left over from before an update: stop it so a current one is started below.
                        Shared.Log(LogPath, "stopping outdated agent " + p.Id + " in session " + p.SessionId);
                        try { p.Kill(); } catch { }
                        continue;
                    }
                    hasAgent.Add(p.SessionId);
                }
            }
            foreach (Process p in Process.GetProcessesByName("explorer"))
                using (p) hasShell.Add(p.SessionId);

            List<SessionInfo> ring = Sessions.Ring();
            var live = new HashSet<int>();
            foreach (SessionInfo s in ring) live.Add(s.Id);
            ClearQuitMarkers(live);

            foreach (SessionInfo s in ring)
            {
                if (hasAgent.Contains(s.Id)) continue;
                if (!hasShell.Contains(s.Id)) continue; // still signing in
                if (File.Exists(Shared.QuitFile(s.Id))) continue; // the user closed it on purpose

                DateTime last;
                if (_launched.TryGetValue(s.Id, out last) && DateTime.UtcNow - last < RelaunchGap) continue;
                _launched[s.Id] = DateTime.UtcNow;

                Launch(s, appExe);
            }
        }

        static bool IsStale(Process p, string appExe)
        {
            try
            {
                string path = p.MainModule.FileName;
                return !string.Equals(path, appExe, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false; // cannot tell, so leave it alone
            }
        }

        // Quit markers belong to a session; drop the ones whose session has signed out.
        static void ClearQuitMarkers(HashSet<int> liveSessions)
        {
            try
            {
                if (!Directory.Exists(Shared.DataDir)) return;
                foreach (string f in Directory.GetFiles(Shared.DataDir, "quit-*.txt"))
                {
                    int id;
                    string name = Path.GetFileNameWithoutExtension(f).Substring("quit-".Length);
                    if (liveSessions == null || !int.TryParse(name, out id) || !liveSessions.Contains(id))
                        File.Delete(f);
                }
            }
            catch { }
        }

        // Start the agent inside the user's own session, with their (non-elevated) token.
        static void Launch(SessionInfo s, string appExe)
        {
            IntPtr token;
            if (!Native.WTSQueryUserToken((uint)s.Id, out token))
            {
                Shared.Log(LogPath, "cannot get token for session " + s.Id + " (error " + Marshal.GetLastWin32Error() + ")");
                return;
            }

            IntPtr env = IntPtr.Zero;
            try
            {
                if (!Native.CreateEnvironmentBlock(out env, token, false)) env = IntPtr.Zero;

                var si = new Native.STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(Native.STARTUPINFO));
                si.lpDesktop = @"winsta0\default";

                Native.PROCESS_INFORMATION pi;
                bool ok = Native.CreateProcessAsUser(token, null, "\"" + appExe + "\" --agent",
                    IntPtr.Zero, IntPtr.Zero, false, CREATE_UNICODE_ENVIRONMENT, env,
                    Path.GetDirectoryName(appExe), ref si, out pi);

                if (ok)
                {
                    Native.CloseHandle(pi.hProcess);
                    Native.CloseHandle(pi.hThread);
                    Shared.Log(LogPath, "started agent for session " + s.Id + " (" + s.User + ")");
                }
                else
                {
                    Shared.Log(LogPath, "could not start agent for session " + s.Id + " (error " + Marshal.GetLastWin32Error() + ")");
                }
            }
            finally
            {
                if (env != IntPtr.Zero) Native.DestroyEnvironmentBlock(env);
                Native.CloseHandle(token);
            }
        }
    }
}
