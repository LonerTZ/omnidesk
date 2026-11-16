using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace OmniDesk
{
    // Wrapper over Windows' undocumented virtual-desktop interfaces. Windows does not offer another way to move
    // a window owned by a different program to another desktop. The interfaces change between Windows builds, so
    // the wrapper refuses to work (Available = false) unless what they report matches the registry exactly.
    // Must be created and used on one STA thread.
    sealed class VirtualDesktops
    {
        static readonly Guid CLSID_ImmersiveShell = new Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239");
        static readonly Guid CLSID_VirtualDesktopManagerInternal = new Guid("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
        static readonly Guid CLSID_VirtualDesktopManager = new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A");
        static readonly Guid AppOnAllDesktops = new Guid("BB64D5B7-4DE3-4AB2-A87C-DB7601AEA7DC");
        static readonly Guid WindowOnAllDesktops = new Guid("C2DDEA68-66F2-4CF9-8264-1BFD00FBBBAC");

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIInspectable), Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513")]
        interface IApplicationView
        {
            int SetFocus();
            int SwitchTo();
            int TryInvokeBack(IntPtr callback);
            int GetThumbnailWindow(out IntPtr hwnd);
            int GetMonitor(out IntPtr monitor);
            int GetVisibility(out int visibility);
            int SetCloak(int cloakType, int unknown);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5")]
        interface IApplicationViewCollection
        {
            int GetViews(out IObjectArray array);
            int GetViewsByZOrder(out IObjectArray array);
            int GetViewsByAppUserModelId(string id, out IObjectArray array);
            int GetViewForHwnd(IntPtr hwnd, out IApplicationView view);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")]
        interface IVirtualDesktop
        {
            bool IsViewVisible(IApplicationView view);
            Guid GetId();
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("53F5CA0B-158F-4124-900C-057158060B27")]
        interface IVirtualDesktopManagerInternal
        {
            int GetCount();
            void MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop);
            bool CanViewMoveDesktops(IApplicationView view);
            IVirtualDesktop GetCurrentDesktop();
            void GetDesktops(out IObjectArray desktops);
            [PreserveSig]
            int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop);
            void SwitchDesktop(IVirtualDesktop desktop);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
        interface IVirtualDesktopManager
        {
            bool IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow);
            Guid GetWindowDesktopId(IntPtr topLevelWindow);
            void MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
        interface IObjectArray
        {
            void GetCount(out int count);
            void GetAt(int index, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object obj);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
        interface IServiceProvider10
        {
            [return: MarshalAs(UnmanagedType.IUnknown)]
            object QueryService(ref Guid service, ref Guid riid);
        }

        readonly Action<string> _log;
        IVirtualDesktopManagerInternal _vdm;
        IApplicationViewCollection _avc;
        IVirtualDesktopManager _pub;

        public bool Available { get; private set; }

        public VirtualDesktops(Action<string> log)
        {
            _log = log;
            try
            {
                var shell = (IServiceProvider10)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ImmersiveShell));
                Guid svc = CLSID_VirtualDesktopManagerInternal, riid = typeof(IVirtualDesktopManagerInternal).GUID;
                _vdm = (IVirtualDesktopManagerInternal)shell.QueryService(ref svc, ref riid);
                Guid avcId = typeof(IApplicationViewCollection).GUID;
                _avc = (IApplicationViewCollection)shell.QueryService(ref avcId, ref avcId);
                _pub = (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_VirtualDesktopManager));
                CheckAgainstRegistry();
                Available = true;
            }
            catch (Exception e)
            {
                Available = false;
                if (_log != null) _log("virtual desktop interface unavailable: " + e.Message);
            }
        }

        // The interface layout is guessed per Windows build; only trust it if it agrees with the registry.
        void CheckAgainstRegistry()
        {
            byte[] regIds = null, regCur = null;
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops"))
            {
                if (k != null)
                {
                    regIds = k.GetValue("VirtualDesktopIDs") as byte[];
                    regCur = k.GetValue("CurrentVirtualDesktop") as byte[];
                }
            }
            if (regIds == null || regCur == null) throw new InvalidOperationException("no desktop data in the registry");

            Guid[] ids = Ids();
            if (ids.Length != regIds.Length / 16) throw new InvalidOperationException("desktop count differs from the registry");
            for (int i = 0; i < ids.Length; i++)
            {
                byte[] one = new byte[16];
                Array.Copy(regIds, i * 16, one, 0, 16);
                if (ids[i] != new Guid(one)) throw new InvalidOperationException("desktop ids differ from the registry");
            }
            if (_vdm.GetCurrentDesktop().GetId() != new Guid(regCur))
                throw new InvalidOperationException("current desktop differs from the registry");
        }

        IVirtualDesktop[] List()
        {
            int count = _vdm.GetCount();
            IObjectArray arr;
            _vdm.GetDesktops(out arr);
            Guid iid = typeof(IVirtualDesktop).GUID;
            var list = new IVirtualDesktop[count];
            for (int i = 0; i < count; i++)
            {
                object o;
                arr.GetAt(i, ref iid, out o);
                list[i] = (IVirtualDesktop)o;
            }
            return list;
        }

        public Guid[] Ids()
        {
            IVirtualDesktop[] d = List();
            var ids = new Guid[d.Length];
            for (int i = 0; i < d.Length; i++) ids[i] = d[i].GetId();
            return ids;
        }

        public int Count { get { return _vdm.GetCount(); } }

        public int Current()
        {
            Guid cur = _vdm.GetCurrentDesktop().GetId();
            Guid[] ids = Ids();
            for (int i = 0; i < ids.Length; i++) if (ids[i] == cur) return i;
            return -1;
        }

        // Index of the desktop a window is on, or -1 for windows shown on every desktop or unknown to Windows.
        public int IndexOfWindow(IntPtr hwnd, Guid[] ids)
        {
            Guid id;
            try { id = _pub.GetWindowDesktopId(hwnd); }
            catch { return -1; }
            if (id == Guid.Empty || id == AppOnAllDesktops || id == WindowOnAllDesktops) return -1;
            for (int i = 0; i < ids.Length; i++) if (ids[i] == id) return i;
            return -1;
        }

        public bool Move(IntPtr hwnd, int index)
        {
            try
            {
                IVirtualDesktop[] d = List();
                if (index < 0 || index >= d.Length) return false;
                IApplicationView view;
                _avc.GetViewForHwnd(hwnd, out view);
                _vdm.MoveViewToDesktop(view, d[index]);
                return true;
            }
            catch (Exception e)
            {
                if (_log != null) _log("could not move window " + hwnd + " to desktop " + index + ": " + e.Message);
                return false;
            }
        }

        // Switch the visible desktop and wait until Windows reports it.
        public bool Switch(int index)
        {
            try
            {
                IVirtualDesktop[] d = List();
                if (index < 0 || index >= d.Length) return false;
                _vdm.SwitchDesktop(d[index]);
                for (int i = 0; i < 80; i++)
                {
                    if (Current() == index) return true;
                    Thread.Sleep(25);
                }
                return false;
            }
            catch (Exception e)
            {
                if (_log != null) _log("could not switch to desktop " + index + ": " + e.Message);
                return false;
            }
        }
    }
}
