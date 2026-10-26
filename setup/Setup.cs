using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OmniDeskSetup
{
    // Self-contained installer: carries OmniDesk.exe as an embedded resource.
    // Double-click to install or update; "Uninstall.exe" (a copy of itself) removes everything.
    static class Setup
    {
        const string Product = "OmniDesk";
        const string Version = "1.1.1";
        const string ServiceName = "OmniDeskSvc";
        const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OmniDesk";

        static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OmniDesk"); }
        }

        static string AppExe { get { return Path.Combine(InstallDir, "OmniDesk.exe"); } }
        static string UninstallExe { get { return Path.Combine(InstallDir, "Uninstall.exe"); } }

        static bool _quiet;

        [STAThread]
        static int Main(string[] args)
        {
            bool uninstall = false;
            foreach (string a in args)
            {
                string x = a.ToLowerInvariant();
                if (x == "/quiet" || x == "/q" || x == "/s") _quiet = true;
                if (x == "/uninstall" || x == "/u") uninstall = true;
            }

            Application.EnableVisualStyles();
            try
            {
                if (uninstall)
                {
                    if (!Ask("Remove " + Product + " from this computer?")) return 0;
                    Uninstall();
                    Info(Product + " was removed.");
                    return 0;
                }

                bool exists = File.Exists(AppExe);
                string prompt = exists
                    ? Product + " is already installed. Update it?"
                    : "Install " + Product + "?\n\nSwap between your signed-in Windows accounts with Ctrl+Win+Left/Right, " +
                      "by pressing past your first or last virtual desktop.\n\nThe swap lands on the other account's lock screen.";
                if (!Ask(prompt)) return 0;

                Install();
                Info(Product + " is installed and running.\n\n" +
                     "Every account you sign in to will start it automatically. Accounts that are already signed in " +
                     "need to sign out and in once.");
                return 0;
            }
            catch (Exception e)
            {
                Error(Product + " setup failed:\n\n" + e.Message);
                return 1;
            }
        }

        // ---- install ----

        static void Install()
        {
            RemoveLegacyInstall();

            // Only the service is stopped. Agents already running in other accounts are left alone:
            // their exe is renamed out of the way, and the new service replaces them with current ones.
            StopService();

            Directory.CreateDirectory(InstallDir);
            SetAsideRunningExe();
            using (Stream src = Assembly.GetExecutingAssembly().GetManifestResourceStream("OmniDesk.exe"))
            using (FileStream dst = new FileStream(AppExe, FileMode.Create, FileAccess.Write))
            {
                if (src == null) throw new InvalidOperationException("Installer is missing its payload.");
                src.CopyTo(dst);
            }

            string self = Assembly.GetExecutingAssembly().Location;
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(UninstallExe), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, UninstallExe, true);

            RemoveService();
            string output;
            int code = Sc("create " + ServiceName + " binPath= \"\\\"" + AppExe + "\\\" --service\" start= auto DisplayName= \"" + Product + "\"", out output);
            if (code != 0) throw new InvalidOperationException("Could not create the service (sc exit " + code + "):\n" + output);
            Sc("description " + ServiceName + " \"Swaps the console between signed-in Windows accounts on Ctrl+Win+Left/Right at the virtual-desktop edge.\"", out output);
            Sc("failure " + ServiceName + " reset= 86400 actions= restart/5000/restart/5000/restart/5000", out output);

            using (RegistryKey run = Registry.LocalMachine.CreateSubKey(RunKey))
                run.SetValue("OmniDeskAgent", "\"" + AppExe + "\" --agent");

            using (RegistryKey k = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", Product);
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "OmniDesk");
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("DisplayIcon", AppExe);
                k.SetValue("UninstallString", "\"" + UninstallExe + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + UninstallExe + "\" /uninstall /quiet");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }

            using (ServiceController sc = new ServiceController(ServiceName))
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            }

            StartAgentForCurrentUser();
        }

        // Launch the agent at normal (non-admin) rights for whoever double-clicked the installer.
        static void StartAgentForCurrentUser()
        {
            try
            {
                var psi = new ProcessStartInfo("runas.exe",
                    "/trustlevel:0x20000 \"\\\"" + AppExe + "\\\" --agent\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(psi);
            }
            catch { }
        }

        // ---- uninstall ----

        static void Uninstall()
        {
            StopEverything();
            RemoveService();

            try { using (RegistryKey run = Registry.LocalMachine.OpenSubKey(RunKey, true)) { if (run != null) run.DeleteValue("OmniDeskAgent", false); } } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false); } catch { }

            try
            {
                string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OmniDesk");
                if (Directory.Exists(data)) Directory.Delete(data, true);
            }
            catch { }

            // This process may be running from the install folder, so delete it from a helper after exit.
            try
            {
                var psi = new ProcessStartInfo("cmd.exe",
                    "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + InstallDir + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(psi);
            }
            catch { }
        }

        // The app was first released as "Account Swap". Remove that install so the two never run side by side.
        static void RemoveLegacyInstall()
        {
            const string legacyService = "AccountSwapSvc";
            string output;
            try
            {
                using (ServiceController sc = new ServiceController(legacyService))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                    }
                }
                Sc("delete " + legacyService, out output);
            }
            catch (InvalidOperationException) { } // not installed

            foreach (Process p in Process.GetProcessesByName("AccountSwap"))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }

            try { using (RegistryKey run = Registry.LocalMachine.OpenSubKey(RunKey, true)) { if (run != null) run.DeleteValue("AccountSwapAgent", false); } } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AccountSwap", false); } catch { }
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AccountSwap");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch { }
            try
            {
                string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AccountSwap");
                if (Directory.Exists(data)) Directory.Delete(data, true);
            }
            catch { }
        }

        // ---- helpers ----

        static void StopEverything()
        {
            StopService();
            KillAgents();
        }

        // A running exe can be renamed but not overwritten. Rename it, and tidy up older leftovers.
        static void SetAsideRunningExe()
        {
            foreach (string old in Directory.GetFiles(InstallDir, "OmniDesk.exe.*.old"))
            {
                try { File.Delete(old); } catch { } // still in use by an agent that has not been replaced yet
            }
            if (File.Exists(AppExe))
                File.Move(AppExe, AppExe + "." + DateTime.UtcNow.Ticks + ".old");
        }

        static void KillAgents()
        {
            foreach (Process p in Process.GetProcessesByName("OmniDesk"))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
        }

        static void StopService()
        {
            try
            {
                using (ServiceController sc = new ServiceController(ServiceName))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                    }
                }
            }
            catch (InvalidOperationException) { } // service not installed
        }

        static void RemoveService()
        {
            string output;
            if (Sc("query " + ServiceName, out output) != 1060)
            {
                Sc("delete " + ServiceName, out output);
                for (int i = 0; i < 20; i++)
                {
                    if (Sc("query " + ServiceName, out output) == 1060) break;
                    Thread.Sleep(250);
                }
            }
        }

        static int Sc(string args, out string output)
        {
            var psi = new ProcessStartInfo("sc.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (Process p = Process.Start(psi))
            {
                output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        static bool Ask(string text)
        {
            if (_quiet) return true;
            return MessageBox.Show(text, Product, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        static void Info(string text)
        {
            if (!_quiet) MessageBox.Show(text, Product, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        static void Error(string text)
        {
            if (!_quiet) MessageBox.Show(text, Product, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
