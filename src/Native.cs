using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AccountSwap
{
    static class Native
    {
        public const int WTS_SESSION_UNLOCK = 0x8;
        public const int WM_WTSSESSION_CHANGE = 0x02B1;

        [StructLayout(LayoutKind.Sequential)]
        public struct WTS_SESSION_INFO
        {
            public int SessionId;
            public IntPtr pWinStationName;
            public int State;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSEnumerateSessions(IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int pCount);

        [DllImport("wtsapi32.dll")]
        public static extern void WTSFreeMemory(IntPtr p);

        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool WTSQuerySessionInformation(IntPtr hServer, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool WTSConnectSession(int logonId, int targetLogonId, string password, bool wait);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int flags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

        [DllImport("kernel32.dll")]
        public static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetNamedPipeClientSessionId(IntPtr pipe, out uint clientSessionId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string name);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);

        [DllImport("user32.dll")]
        public static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint count, INPUT[] inputs, int size);

        [DllImport("user32.dll")]
        public static extern bool LockWorkStation();
    }

    class SessionInfo
    {
        public int Id;
        public int State;
        public string User;
    }

    static class Sessions
    {
        const int StateActive = 0;
        const int StateDisconnected = 4;

        // Live sessions with a real signed-in user, in session-id order. Session 0 (services) is excluded.
        public static List<SessionInfo> Ring()
        {
            var list = new List<SessionInfo>();
            IntPtr p;
            int count;
            if (!Native.WTSEnumerateSessions(IntPtr.Zero, 0, 1, out p, out count))
                return list;
            try
            {
                int size = Marshal.SizeOf(typeof(Native.WTS_SESSION_INFO));
                for (int i = 0; i < count; i++)
                {
                    var info = (Native.WTS_SESSION_INFO)Marshal.PtrToStructure(
                        new IntPtr(p.ToInt64() + (long)i * size), typeof(Native.WTS_SESSION_INFO));
                    if (info.SessionId == 0) continue;
                    if (info.State != StateActive && info.State != StateDisconnected) continue;
                    string user = QueryUser(info.SessionId);
                    if (string.IsNullOrEmpty(user)) continue;
                    list.Add(new SessionInfo { Id = info.SessionId, State = info.State, User = user });
                }
            }
            finally
            {
                Native.WTSFreeMemory(p);
            }
            list.Sort(delegate(SessionInfo a, SessionInfo b) { return a.Id.CompareTo(b.Id); });
            return list;
        }

        static string QueryUser(int sessionId)
        {
            IntPtr buf;
            int bytes;
            const int WTSUserName = 5;
            if (!Native.WTSQuerySessionInformation(IntPtr.Zero, sessionId, WTSUserName, out buf, out bytes))
                return null;
            try { return Marshal.PtrToStringUni(buf); }
            finally { Native.WTSFreeMemory(buf); }
        }
    }

    static class Shared
    {
        public const string PipeName = "AccountSwapPipe";

        public static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AccountSwap");

        public static string ArrivalFile(int sessionId)
        {
            return Path.Combine(DataDir, "arrival-" + sessionId + ".txt");
        }

        // Service-side: any local user may drop/read their own session's arrival note.
        public static void EnsureDataDir()
        {
            Directory.CreateDirectory(DataDir);
            var sec = Directory.GetAccessControl(DataDir);
            sec.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            Directory.SetAccessControl(DataDir, sec);
        }

        static readonly object LogLock = new object();

        public static void Log(string path, string line)
        {
            try
            {
                lock (LogLock)
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 512 * 1024) File.Delete(path);
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + line + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
