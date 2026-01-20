using Microsoft.Extensions.Configuration;
using mp.hooks;
using System;
using System.Diagnostics;
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
            //KeyboardHook h = new KeyboardHook(true);
            ToolStripMenuItem exitMenuItem = null;
            ToolStripMenuItem toggleMicCamMenuItem = null;
            ToolStripMenuItem toggleSceneMenuItem = null;
            ToolStripMenuItem toggleStreamMenuItem = null;
            OBSClient obsClient = null;
            public MyCustomApplicationContext()
            {
                var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", false, reloadOnChange: true)
                .AddJsonFile($"appsettings.prod.json", true, reloadOnChange: true)
                .Build();
                _settings = configuration.GetSection("Settings").Get<Settings>();
                configuration.Bind(_settings);
                obsClient = new  OBSClient(_settings.Uri, _settings.Password);
                exitMenuItem = new ToolStripMenuItem("Exit", null, Exit);
                toggleMicCamMenuItem = new ToolStripMenuItem($"Toggle {string.Join("/",_settings.SceneItemsToToggle.SceneItems)}", null, async (s, e) =>
                {
                    try
                    {
                        await obsClient.ToggleSceneItems(_settings.SceneItemsToToggle.SceneName, _settings.SceneItemsToToggle.SceneItems);
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
                        await obsClient.ToggleScenes(_settings.ScenesToToggle);
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
                        await obsClient.ToggleStream();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error toggling stream: {ex.Message}");
                    }
                });

                //h.KeyDown += async (s, e) =>
                //{
                //    if ((ConsoleKey)e.vkCode == ConsoleKey.F1)
                //    {
                //        Debug.WriteLine("F1 key pressed");
                //        await obsClient.ToggleSceneItems("Scene", "WEBCAM", "MIC");

                //    }
                //    if ((ConsoleKey)e.vkCode == ConsoleKey.F2)
                //    {
                //        Debug.WriteLine("F2 key pressed");
                //        await obsClient.ToggleScenes("Scene", "BRB");
                //    }
                //    if ((ConsoleKey)e.vkCode == ConsoleKey.F3)
                //    {
                //        Debug.WriteLine("F2 key pressed");
                //        await obsClient.ToggleStream();  
                //    }
                //};

                obsClient.StreamStateChanged.Subscribe(isStreaming =>
                {
                    Console.WriteLine($"Streaming state changed: {isStreaming}");
                    if (isStreaming)
                    {
                        toggleStreamMenuItem.Image = Resources.redtvimg;
                    }
                    else
                    {
                        toggleStreamMenuItem.Image = null;
                    }
                });

                obsClient.Init().Wait();

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
            }

            void Exit(object? sender, EventArgs e)
            {
                trayIcon.Visible = false;
                //h.unhook();
                Application.Exit();
            }
        }
    }

}
