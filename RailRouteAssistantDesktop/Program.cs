using System;
using System.Windows.Forms;

namespace RailRouteAssistantDesktop
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            ApplicationConfiguration.Initialize();
            if (!GameInstallationManager.PrepareInstallationAndLaunch(args)) return;
            Application.Run(new MainForm());
        }
    }
}
