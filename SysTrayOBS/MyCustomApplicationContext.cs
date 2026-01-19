using mp.hooks;
using System;
using System.Diagnostics;
using System.Windows.Forms;
using SysTrayOBS;

namespace WinFormsApp1
{
    namespace TrayOnlyWinFormsDemo
    {
        public class MyCustomApplicationContext : ApplicationContext
        {
            private NotifyIcon trayIcon;
            KeyboardHook h = new KeyboardHook(true);
            ToolStripMenuItem exitMenuItem = null;
            ToolStripMenuItem toggleMicCamMenuItem = null;
            ToolStripMenuItem toggleSceneMenuItem = null;
            ToolStripMenuItem toggleStreamMenuItem = null;
            OBSProgram obsProgram = null;
            public MyCustomApplicationContext()
            {
                obsProgram = new OBSProgram();

                exitMenuItem = new ToolStripMenuItem("Exit", null, Exit);
                toggleMicCamMenuItem = new ToolStripMenuItem("Toggle MIC/CAM", null, ToggleMicAndWebcam);
                toggleSceneMenuItem = new ToolStripMenuItem("Toggle Scene", null, ToggleScene);
                toggleStreamMenuItem = new ToolStripMenuItem("Toggle Stream", null, ToggleStream);

                h.KeyDown += (s, e) =>
                {
                    if ((ConsoleKey)e.vkCode == ConsoleKey.F1)
                    {
                        Debug.WriteLine("F1 key pressed");
                        obsProgram.ToggleSetSceneCamAndMic();
                    }
                    if ((ConsoleKey)e.vkCode == ConsoleKey.F2)
                    {
                        Debug.WriteLine("F2 key pressed");
                        obsProgram.ToggleScene();
                    }
                    if ((ConsoleKey)e.vkCode == ConsoleKey.F3)
                    {
                        Debug.WriteLine("F2 key pressed");
                        obsProgram.ToggleStream();
                    }
                };

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

            void ToggleStream(object? sender, EventArgs e)
            {
                obsProgram.ToggleStream();
            }

            void ToggleMicAndWebcam(object? sender, EventArgs e)
            {
               obsProgram.ToggleSetSceneCamAndMic();
            }

            void ToggleScene(object? sender, EventArgs e)
            {
                obsProgram.ToggleScene();
            }

            void Exit(object? sender, EventArgs e)
            {
                trayIcon.Visible = false;
                h.unhook();
                Application.Exit();
            }
        }
    }

}
