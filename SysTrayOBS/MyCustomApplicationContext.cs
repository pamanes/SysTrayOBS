using Microsoft.Extensions.Configuration;
using mp.hooks;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;
using SysTrayOBS;

namespace WinFormsApp1
{
    namespace TrayOnlyWinFormsDemo
    {
        public class SceneItemsToToggle
        {
            public string SceneName { get; set; }
            public string[] SceneItems { get; set; }
        }
        public class Settings
        {
            public string Uri { get; set; }
            public string Password { get; set; }
            public string[] ScenesToToggle { get; set; }
            public SceneItemsToToggle SceneItemsToToggle { get; set; }
        }
        public class MyCustomApplicationContext : ApplicationContext
        {
            private static Settings _settings;
            private NotifyIcon trayIcon;
            ToolStripMenuItem exitMenuItem = null;
            ToolStripMenuItem toggleMicCamMenuItem = null;
            ToolStripMenuItem toggleSceneMenuItem = null;
            ToolStripMenuItem toggleStreamMenuItem = null;
            OBSClientSM sm = null;
            public MyCustomApplicationContext()
            {
                var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", false, reloadOnChange: true)
                .AddJsonFile($"appsettings.prod.json", true, reloadOnChange: true)
                .Build();
                _settings = configuration.GetSection("Settings").Get<Settings>();
                configuration.Bind(_settings);
                sm = new OBSClientSM(_settings.Uri, _settings.Password);
                exitMenuItem = new ToolStripMenuItem("Exit", null, async (s, e) =>
                {
                    try
                    {
                        trayIcon.Visible = false;
                        await sm.DisposeAsync();
                        Application.Exit();
                    }
                    catch{ }
                });
                toggleMicCamMenuItem = new ToolStripMenuItem($"Toggle {string.Join("/",_settings.SceneItemsToToggle.SceneItems)}", null, async (s, e) =>
                {
                    try
                    {
                        await sm.ToggleSceneItems(_settings.SceneItemsToToggle.SceneName, _settings.SceneItemsToToggle.SceneItems);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error toggling scene items: {ex.Message}");
                    }
                });                
                toggleSceneMenuItem = new ToolStripMenuItem($"Toggle {string.Join("/", _settings.ScenesToToggle)}", null, async (s, e) =>
                {
                    try
                    {
                        await sm.ToggleScenes(_settings.ScenesToToggle);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error toggling scene: {ex.Message}");
                    }
                });                
                toggleStreamMenuItem = new ToolStripMenuItem("Toggle Stream", null, async (s, e) =>
                {
                    try
                    {                                    
                        await sm.ToggleStreamAsync();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error toggling stream: {ex.Message}");
                    }
                });
                sm.StreamStateChanged.Subscribe((isStreaming) =>
                {
                    toggleStreamMenuItem.Image = isStreaming ? Resources.redtvimg : null;
                });
                sm.StateChanged.Subscribe((connected) =>
                {
                    Debug.WriteLine($"OBS Client State Changed: {connected}");
                    bool enabled = connected == ObsClientState.Ready;
                    trayIcon?.Icon = (enabled) ? Resources.icons8_twitch_32 : Resources.twitch32;
                    toggleMicCamMenuItem.Enabled = toggleSceneMenuItem.Enabled = toggleStreamMenuItem.Enabled = enabled;
                });                
                trayIcon = new NotifyIcon()
                {
                    Icon = Resources.twitch32,
                    ContextMenuStrip = new ContextMenuStrip()
                    {
                        Items = 
                        {                            
                            toggleMicCamMenuItem,
                            toggleSceneMenuItem,
                            toggleStreamMenuItem,
                            exitMenuItem,
                        }
                    },
                    Visible = true
                };
                Task.Run(async () => { await sm.StartAsync(); });
            }

            void Exit(object? sender, EventArgs e)
            {
                trayIcon.Visible = false;
                sm?.DisposeAsync();
                Application.Exit();
            }
        }
    }

}
