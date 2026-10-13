using System;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

namespace SpotifyIsland
{
    public partial class App : Application
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "SpotifyIsland";

        private Mutex _mutex;
        private MainWindow _window;
        private System.Windows.Forms.NotifyIcon _tray;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Only allow one copy to run at a time.
            _mutex = new Mutex(true, "SpotifyIsland_SingleInstance", out bool isFirst);
            if (!isFirst)
            {
                Shutdown();
                return;
            }

            _window = new MainWindow(); // stays hidden until Spotify plays something
            BuildTrayIcon();
        }

        private void BuildTrayIcon()
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();

            var startupItem = new System.Windows.Forms.ToolStripMenuItem("Start with Windows")
            {
                Checked = IsStartupEnabled(),
                CheckOnClick = true
            };
            startupItem.CheckedChanged += (_, _) => SetStartup(startupItem.Checked);
            menu.Items.Add(startupItem);

            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => Shutdown());

            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Text = "Spotify Island",
                Visible = true,
                ContextMenuStrip = menu
            };
        }

        private static bool IsStartupEnabled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(RunValueName) != null;
        }

        private static void SetStartup(bool enable)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null) return;

            if (enable)
                key.SetValue(RunValueName, "\"" + Environment.ProcessPath + "\"");
            else
                key.DeleteValue(RunValueName, false);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            base.OnExit(e);
        }
    }
}
