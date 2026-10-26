using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AccountSwapSetup
{
    // Self-contained installer: carries AccountSwap.exe as an embedded resource.
    // Double-click to install or update; "Uninstall.exe" (a copy of itself) removes everything.
    static class Setup
    {
        const string Product = "Account Swap";
        const string Version = "1.0.0";
        const string ServiceName = "AccountSwapSvc";
        const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AccountSwap";

        static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AccountSwap"); }
        }

        static string AppExe { get { return Path.Combine(InstallDir, "AccountSwap.exe"); } }
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
            StopEverything();

            Directory.CreateDirectory(InstallDir);
            using (Stream src = Assembly.GetExecutingAssembly().GetManifestResourceStream("AccountSwap.exe"))
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
                run.SetValue("AccountSwapAgent", "\"" + AppExe + "\" --agent");

            using (RegistryKey k = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", Product);
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "Account Swap");
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

            try { using (RegistryKey run = Registry.LocalMachine.OpenSubKey(RunKey, true)) { if (run != null) run.DeleteValue("AccountSwapAgent", false); } } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false); } catch { }

            try
            {
                string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AccountSwap");
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

        // ---- helpers ----

        static void StopEverything()
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

            foreach (Process p in Process.GetProcessesByName("AccountSwap"))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
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
