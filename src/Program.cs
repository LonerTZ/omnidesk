using System;
using System.ServiceProcess;

namespace OmniDesk
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "--agent";
            switch (mode)
            {
                case "--service":
                    ServiceBase.Run(new SwapService());
                    return 0;
                case "--agent":
                    return Agent.Run(false);
                case "--debug":
                    return Agent.Run(true);
                case "--sessions":
                    PrintSessions();
                    return 0;
                default:
                    Console.WriteLine("OmniDesk.exe [--agent | --debug | --service | --sessions]");
                    return 1;
            }
        }

        static void PrintSessions()
        {
            Console.WriteLine("console session: " + Native.WTSGetActiveConsoleSessionId());
            foreach (SessionInfo s in Sessions.Ring())
                Console.WriteLine("  session " + s.Id + "  " + s.User + "  " + (s.State == 0 ? "Active" : "Disconnected"));
        }
    }
}
