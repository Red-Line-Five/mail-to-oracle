using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace OwaToOracle
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool isNew;
            using (var mutex = new Mutex(true, "OwaToOracle_SingleInstance", out isNew))
            {
                if (!isNew) return;      // only one copy may run

                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                ServicePointManager.Expect100Continue = false;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // watchdog: log the crash and start the program again
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => Crash(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash(e.ExceptionObject as Exception);

                Application.Run(new MainForm(args.Contains("--tray")));
            }
        }

        private static void Crash(Exception ex)
        {
            Log.Write("CRASH: " + ex);
            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--tray") { UseShellExecute = true }); }
            catch { }
            Environment.Exit(1);
        }
    }
}
