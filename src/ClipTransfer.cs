using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace OmniDesk
{
    class ClipRecipient
    {
        public int Session;
        public string Sid;
        public string User;
    }

    class ClipItem
    {
        public uint Id;
        public string Name;   // set for registered formats, whose numbers differ between sessions
        public byte[] Data;
    }

    // Carries the clipboard to the other signed-in accounts when the user swaps to them.
    //
    // The sending agent copies every format it can into a private folder under ProgramData, writes a manifest last,
    // and each receiving agent that has the feature switched on puts the same formats on its own clipboard.
    // Copied files are copied into the folder as well, because the other account usually cannot read the originals.
    static class ClipTransfer
    {
        public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

        // Lets a test point the transfer folder somewhere else.
        internal static string RootOverride;

        public static string Root
        {
            get { return RootOverride ?? Path.Combine(Shared.DataDir, "clip"); }
        }

        static string OptInFile(int session)
        {
            return Path.Combine(Shared.DataDir, "clip-optin-" + session + ".txt");
        }

        // ---- who takes part ----

        // An agent whose user ticked "Share clipboard when swapping" leaves a note with its process id.
        public static void SetOptIn(int session, bool on)
        {
            string f = OptInFile(session);
            try
            {
                if (on) File.WriteAllText(f, Process.GetCurrentProcess().Id.ToString());
                else if (File.Exists(f)) File.Delete(f);
            }
            catch { }
        }

        // Other live sessions whose agent is running with sharing on.
        public static List<ClipRecipient> Recipients(int self)
        {
            var list = new List<ClipRecipient>();
            foreach (SessionInfo s in Sessions.Ring())
            {
                if (s.Id == self) continue;
                try
                {
                    string f = OptInFile(s.Id);
                    if (!File.Exists(f)) continue;
                    int pid;
                    if (!int.TryParse(File.ReadAllText(f).Trim(), out pid)) continue;
                    Process p;
                    try { p = Process.GetProcessById(pid); } catch { continue; }
                    if (p.SessionId != s.Id) continue;
                    var sid = (SecurityIdentifier)new NTAccount(s.Domain, s.User).Translate(typeof(SecurityIdentifier));
                    list.Add(new ClipRecipient { Session = s.Id, Sid = sid.Value, User = s.User });
                }
                catch { }
            }
            return list;
        }

        // ---- folder and cleanup ----

        // Anyone may create a transfer folder here; each folder carries its own, tighter, permissions.
        public static void EnsureRoot()
        {
            string root = Root;
            if (Directory.Exists(root)) return;
            var ds = new DirectorySecurity();
            ds.SetAccessRuleProtection(true, false);
            ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute | FileSystemRights.CreateDirectories,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            Directory.CreateDirectory(root, ds);
        }

        // Deletes transfers older than the time limit. Run by the service, and by the sender for its own folder.
        public static void Sweep()
        {
            try
            {
                if (!Directory.Exists(Root)) return;
                foreach (string d in Directory.GetDirectories(Root))
                {
                    try
                    {
                        if (DateTime.UtcNow - Directory.GetCreationTimeUtc(d) > Ttl)
                            Directory.Delete(d, true);
                    }
                    catch { }
                }
            }
            catch { }
        }

        static DirectorySecurity MakeSecurity(IList<string> readerSids)
        {
            var ds = new DirectorySecurity();
            ds.SetAccessRuleProtection(true, false);
            InheritanceFlags inh = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User,
                FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            foreach (string sid in readerSids)
                ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid),
                    FileSystemRights.ReadAndExecute, inh, PropagationFlags.None, AccessControlType.Allow));
            return ds;
        }

        // ---- sending ----

        // Runs on a worker thread so the swap itself never waits for it.
        public static bool SendToOthers(int session, string user)
        {
            List<ClipRecipient> rcpts = Recipients(session);
            if (rcpts.Count == 0) return false;
            var sids = new List<string>();
            foreach (ClipRecipient r in rcpts) sids.Add(r.Sid);

            string dir = Capture(session, user, sids);
            if (dir == null) return false;

            // Take our own copy away when the time is up (the service sweeps too, in case this process is gone).
            var t = new System.Threading.Timer(delegate(object o)
            {
                try { Directory.Delete(dir, true); } catch { }
            }, null, (int)Ttl.TotalMilliseconds + 2000, Timeout.Infinite);
            GC.KeepAlive(t);
            return true;
        }

        // Copies the current clipboard into a new transfer folder and returns its path, or null if there was nothing.
        public static string Capture(int session, string user, IList<string> readerSids)
        {
            List<ClipItem> items = ReadClipboard();
            if (items.Count == 0) return null;

            EnsureRoot();
            string dir = Path.Combine(Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir, MakeSecurity(readerSids));

            var manifest = new StringBuilder();
            manifest.AppendLine("OMNICLIP\t1");
            manifest.AppendLine("from\t" + session + "\t" + user);
            manifest.AppendLine("created\t" + DateTime.UtcNow.Ticks);

            int n = 0;
            foreach (ClipItem it in items)
            {
                byte[] data = it.Data;
                if (it.Id == CF_HDROP && it.Name == null)
                {
                    data = StageFiles(dir, data);
                    if (data == null) continue;
                }
                else if (it.Name == "Preferred DropEffect")
                {
                    data = BitConverter.GetBytes(1); // a "cut" must not try to move files out of the transfer folder
                }
                string file = "d" + n + ".bin";
                n++;
                File.WriteAllBytes(Path.Combine(dir, file), data);
                manifest.AppendLine("F\t" + it.Id + "\t" + (it.Name ?? "") + "\t" + file);
            }
            if (n == 0) { try { Directory.Delete(dir, true); } catch { } return null; }

            // The manifest appears last and in one step, so a receiver never reads half a transfer.
            string tmp = Path.Combine(dir, "manifest.tmp");
            File.WriteAllText(tmp, manifest.ToString(), new UTF8Encoding(false));
            File.Move(tmp, Path.Combine(dir, "manifest.txt"));
            return dir;
        }

        const uint CF_HDROP = 15;

        // Copies the files and folders a copied file list points to, and returns a file list pointing at the copies.
        static byte[] StageFiles(string dir, byte[] drop)
        {
            if (drop.Length < 20) return null;
            int offset = BitConverter.ToInt32(drop, 0);
            bool wide = BitConverter.ToInt32(drop, 16) != 0;
            if (offset < 20 || offset >= drop.Length) return null;

            string all = wide
                ? Encoding.Unicode.GetString(drop, offset, drop.Length - offset)
                : Encoding.Default.GetString(drop, offset, drop.Length - offset);
            var staged = new List<string>();
            int index = 0;
            foreach (string path in all.Split('\0'))
            {
                if (path.Length == 0) continue;
                try
                {
                    string name = Path.GetFileName(path.TrimEnd('\\', '/'));
                    if (name.Length == 0) continue;
                    string dest = Path.Combine(Path.Combine(Path.Combine(dir, "files"), index.ToString()), name);
                    index++;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    if (Directory.Exists(path)) CopyDirectory(path, dest);
                    else File.Copy(path, dest);
                    staged.Add(dest);
                }
                catch { }
            }
            if (staged.Count == 0) return null;

            byte[] list = Encoding.Unicode.GetBytes(string.Join("\0", staged.ToArray()) + "\0\0");
            var result = new byte[20 + list.Length];
            BitConverter.GetBytes(20).CopyTo(result, 0);   // offset of the file names
            BitConverter.GetBytes(1).CopyTo(result, 16);   // wide characters
            Array.Copy(list, 0, result, 20, list.Length);
            return result;
        }

        static void CopyDirectory(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from))
                File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
            foreach (string d in Directory.GetDirectories(from))
                CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
        }

        // Formats that only make sense inside the program that put them there, or that point into its memory.
        static readonly string[] Blocked =
        {
            "Ole Private Data", "DataObject", "OleClipboardPersistOnFlush", "Shell IDList Array", "Shell Object Offsets",
            "FileName", "FileNameW", "FileGroupDescriptor", "FileGroupDescriptorW", "FileContents", "DragContext",
            "DragImageBits", "Embed Source", "Link Source", "Link Source Descriptor", "Object Descriptor", "Native",
            "OwnerLink", "ObjectLink", "Embedded Object", "UsingDefaultDragImage"
        };

        static bool Skip(uint f)
        {
            // Formats that are a handle to a graphics object, not memory: bitmap, metafile, palette, enhanced metafile,
            // owner-display formats, and the private and GDI ranges. A copied picture also arrives as DIB, which is kept.
            return f == 2 || f == 3 || f == 9 || f == 14 || (f >= 0x80 && f <= 0x8F) || (f >= 0x200 && f <= 0x3FF);
        }

        static List<ClipItem> ReadClipboard()
        {
            var items = new List<ClipItem>();
            if (!OpenWithRetry(IntPtr.Zero)) return items;
            try
            {
                uint fmt = 0;
                while ((fmt = N.EnumClipboardFormats(fmt)) != 0)
                {
                    if (Skip(fmt)) continue;
                    string name = null;
                    if (fmt >= 0xC000)
                    {
                        var sb = new StringBuilder(256);
                        if (N.GetClipboardFormatName(fmt, sb, 256) == 0) continue;
                        name = sb.ToString();
                        if (Array.IndexOf(Blocked, name) >= 0) continue;
                    }
                    IntPtr h = N.GetClipboardData(fmt);   // asks the owner to produce it if it was left for later
                    if (h == IntPtr.Zero) continue;
                    long size = (long)N.GlobalSize(h).ToUInt64();
                    if (size <= 0 || size > int.MaxValue) continue;
                    IntPtr p = N.GlobalLock(h);
                    if (p == IntPtr.Zero) continue;
                    try
                    {
                        var data = new byte[(int)size];
                        Marshal.Copy(p, data, 0, data.Length);
                        items.Add(new ClipItem { Id = fmt, Name = name, Data = data });
                    }
                    finally { N.GlobalUnlock(h); }
                }
            }
            finally { N.CloseClipboard(); }
            return items;
        }

        // ---- receiving ----

        // Transfer folders (newest last) that have a finished manifest.
        public static List<string> Ready()
        {
            var list = new List<string>();
            try
            {
                if (!Directory.Exists(Root)) return list;
                foreach (string d in Directory.GetDirectories(Root))
                    if (File.Exists(Path.Combine(d, "manifest.txt"))) list.Add(d);
                list.Sort(delegate(string a, string b)
                {
                    return Directory.GetCreationTimeUtc(a).CompareTo(Directory.GetCreationTimeUtc(b));
                });
            }
            catch { }
            return list;
        }

        // Reads a transfer's header. Returns false if it is unreadable or not for this session to use.
        public static bool Peek(string dir, out int fromSession, out string fromUser, out DateTime created)
        {
            fromSession = 0; fromUser = ""; created = DateTime.MinValue;
            try
            {
                string[] lines = File.ReadAllLines(Path.Combine(dir, "manifest.txt"), Encoding.UTF8);
                if (lines.Length < 3 || lines[0] != "OMNICLIP\t1") return false;
                string[] from = lines[1].Split('\t');
                string[] when = lines[2].Split('\t');
                long ticks;
                if (from.Length < 3 || from[0] != "from" || !int.TryParse(from[1], out fromSession)) return false;
                if (when.Length < 2 || when[0] != "created" || !long.TryParse(when[1], out ticks)) return false;
                fromUser = from[2];
                created = new DateTime(ticks, DateTimeKind.Utc);
                return true;
            }
            catch { return false; }
        }

        // Puts a transfer on this session's clipboard. Returns the clipboard sequence number afterwards, or 0 on failure.
        public static uint Apply(string dir, IntPtr owner)
        {
            var items = new List<ClipItem>();
            try
            {
                foreach (string line in File.ReadAllLines(Path.Combine(dir, "manifest.txt"), Encoding.UTF8))
                {
                    string[] f = line.Split('\t');
                    if (f.Length < 4 || f[0] != "F") continue;
                    uint id;
                    if (!uint.TryParse(f[1], out id)) continue;
                    items.Add(new ClipItem
                    {
                        Id = id,
                        Name = f[2].Length == 0 ? null : f[2],
                        Data = File.ReadAllBytes(Path.Combine(dir, f[3]))
                    });
                }
            }
            catch { return 0; }
            if (items.Count == 0) return 0;

            if (!OpenWithRetry(owner)) return 0;
            try
            {
                N.EmptyClipboard();
                foreach (ClipItem it in items)
                {
                    uint id = it.Name == null ? it.Id : N.RegisterClipboardFormat(it.Name);
                    if (id == 0 || it.Data.Length == 0) continue;
                    IntPtr h = N.GlobalAlloc(0x2, new UIntPtr((uint)it.Data.Length)); // GMEM_MOVEABLE
                    if (h == IntPtr.Zero) continue;
                    IntPtr p = N.GlobalLock(h);
                    if (p == IntPtr.Zero) { N.GlobalFree(h); continue; }
                    Marshal.Copy(it.Data, 0, p, it.Data.Length);
                    N.GlobalUnlock(h);
                    if (N.SetClipboardData(id, h) == IntPtr.Zero) N.GlobalFree(h);
                }
            }
            finally { N.CloseClipboard(); }
            return N.GetClipboardSequenceNumber();
        }

        public static uint SequenceNumber()
        {
            return N.GetClipboardSequenceNumber();
        }

        static bool OpenWithRetry(IntPtr owner)
        {
            for (int i = 0; i < 30; i++)
            {
                if (N.OpenClipboard(owner)) return true;
                Thread.Sleep(100); // another program has it open for a moment
            }
            return false;
        }

        static class N
        {
            [DllImport("user32.dll", SetLastError = true)] public static extern bool OpenClipboard(IntPtr owner);
            [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseClipboard();
            [DllImport("user32.dll", SetLastError = true)] public static extern bool EmptyClipboard();
            [DllImport("user32.dll")] public static extern uint EnumClipboardFormats(uint format);
            [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr GetClipboardData(uint format);
            [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetClipboardData(uint format, IntPtr mem);
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern int GetClipboardFormatName(uint format, StringBuilder name, int max);
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern uint RegisterClipboardFormat(string name);
            [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
            [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
            [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GlobalLock(IntPtr mem);
            [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr mem);
            [DllImport("kernel32.dll")] public static extern UIntPtr GlobalSize(IntPtr mem);
            [DllImport("kernel32.dll")] public static extern IntPtr GlobalFree(IntPtr mem);
        }
    }
}
