using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;

namespace AccountSwap
{
    // Runs as LocalSystem. Agents ask it to swap the console to the next/previous live session.
    class SwapService : ServiceBase
    {
        static readonly string LogPath = Path.Combine(Shared.DataDir, "service.log");
        static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(1500);

        Thread _thread;
        volatile bool _stop;
        DateTime _lastSwap = DateTime.MinValue;

        public SwapService()
        {
            ServiceName = "AccountSwapSvc";
            CanStop = true;
        }

        protected override void OnStart(string[] args)
        {
            Shared.EnsureDataDir();
            Shared.Log(LogPath, "service started");
            _thread = new Thread(Loop) { IsBackground = true };
            _thread.Start();
        }

        protected override void OnStop()
        {
            _stop = true;
            try
            {
                using (var c = new NamedPipeClientStream(".", Shared.PipeName, PipeDirection.InOut))
                    c.Connect(500);
            }
            catch { }
            Shared.Log(LogPath, "service stopped");
        }

        static NamedPipeServerStream CreatePipe()
        {
            var ps = new PipeSecurity();
            ps.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
            ps.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            return new NamedPipeServerStream(Shared.PipeName, PipeDirection.InOut, 16,
                PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, ps);
        }

        void Loop()
        {
            while (!_stop)
            {
                try
                {
                    NamedPipeServerStream server = CreatePipe();
                    server.WaitForConnection();
                    if (_stop) { server.Dispose(); break; }
                    NamedPipeServerStream s = server;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try { Handle(s); }
                        catch (Exception e) { Shared.Log(LogPath, "handler error: " + e.Message); }
                        finally { s.Dispose(); }
                    });
                }
                catch (Exception e)
                {
                    Shared.Log(LogPath, "loop error: " + e.Message);
                    Thread.Sleep(250);
                }
            }
        }

        void Handle(NamedPipeServerStream pipe)
        {
            var reader = new StreamReader(pipe, new UTF8Encoding(false));
            var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

            uint client;
            if (!Native.GetNamedPipeClientSessionId(pipe.SafePipeHandle.DangerousGetHandle(), out client))
            {
                Reply(pipe, writer, "ERR");
                return;
            }

            string line = reader.ReadLine();
            int dir = 0;
            if (line == "SWAP +1") dir = 1;
            else if (line == "SWAP -1") dir = -1;
            if (dir == 0) { Reply(pipe, writer, "ERR"); return; }

            int console = (int)Native.WTSGetActiveConsoleSessionId();
            if ((int)client != console)
            {
                // Only whoever is at the physical screen may swap; RDP sessions must not move the console.
                Shared.Log(LogPath, "denied: session " + client + " is not the console (" + console + ")");
                Reply(pipe, writer, "DENIED");
                return;
            }

            SessionInfo target;
            lock (this)
            {
                if (DateTime.UtcNow - _lastSwap < MinGap) { Reply(pipe, writer, "BUSY"); return; }
                List<SessionInfo> ring = Sessions.Ring();
                int idx = ring.FindIndex(delegate(SessionInfo x) { return x.Id == console; });
                if (idx < 0 || ring.Count < 2)
                {
                    Shared.Log(LogPath, "no target (ring size " + ring.Count + ")");
                    Reply(pipe, writer, "NONE");
                    return;
                }
                target = ring[((idx + dir) % ring.Count + ring.Count) % ring.Count];
                _lastSwap = DateTime.UtcNow;
            }

            try
            {
                File.WriteAllText(Shared.ArrivalFile(target.Id),
                    (dir > 0 ? "+1" : "-1") + " " + DateTime.UtcNow.Ticks);
            }
            catch (Exception e) { Shared.Log(LogPath, "arrival note failed: " + e.Message); }

            Shared.Log(LogPath, "swap " + console + " -> " + target.Id + " (" + target.User + "), dir " + dir);
            Reply(pipe, writer, "OK " + target.Id);

            int targetId = target.Id;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(500); // let the agent lock its own session first
                Connect(targetId, console);
            });
        }

        static void Reply(NamedPipeServerStream pipe, StreamWriter writer, string text)
        {
            writer.WriteLine(text);
            writer.Flush();
            try { pipe.WaitForPipeDrain(); } catch { }
        }

        static void Connect(int target, int console)
        {
            if (Native.WTSConnectSession(target, console, "", false))
            {
                Shared.Log(LogPath, "connected session " + target + " to console");
                return;
            }
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Shared.Log(LogPath, "WTSConnectSession failed (" + err + "), falling back to tscon");
            try
            {
                string tscon = Path.Combine(Environment.SystemDirectory, "tscon.exe");
                var psi = new ProcessStartInfo(tscon, target + " /dest:console")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(5000);
                    Shared.Log(LogPath, "tscon exit " + (p.HasExited ? p.ExitCode.ToString() : "timeout"));
                }
            }
            catch (Exception e)
            {
                Shared.Log(LogPath, "tscon failed: " + e.Message);
            }
        }
    }
}
