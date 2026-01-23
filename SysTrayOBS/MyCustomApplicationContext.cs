using Microsoft.Extensions.Configuration;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SysTrayOBS
{
    public sealed class MyCustomApplicationContext : ApplicationContext
    {
        private readonly NotifyIcon _trayIcon;
        private readonly OBSClientSM _obs;
        private readonly Settings _settings;

        //private readonly ToolStripMenuItem _toggleMicCam;
        private readonly ToolStripMenuItem[] _toggleSceneItems;
        //private readonly ToolStripMenuItem _toggleScene;
        private readonly ToolStripMenuItem[] _toggleScenes;
        private readonly ToolStripMenuItem _toggleStream;
        private readonly ToolStripMenuItem _exit;

        public MyCustomApplicationContext()
        {
            _settings = LoadSettings();
            _obs = new OBSClientSM(_settings.Uri, _settings.Password);

            _toggleSceneItems = _settings.SceneItemsToToggle
            .Select(cfg =>
                CreateMenuItem(
                    $"Toggle {cfg.SceneName} Items: ({string.Join("/", cfg.SceneItems)})",
                    () => _obs.ToggleSceneItems(cfg.SceneName, cfg.SceneItems)))
            .ToArray();

            _toggleScenes = _settings.ScenesToToggle
                .Select(g =>
                    CreateMenuItem(
                        $"Toggle Scene: ({string.Join(" ↔ ", g.Scenes)})",
                        () => _obs.ToggleScenes(g.Scenes)))
                .ToArray();

            _toggleStream = CreateMenuItem(
                "Toggle Stream",
                _obs.ToggleStreamAsync);

            _exit = new ToolStripMenuItem("Exit", null, async (_, _) => await ShutdownAsync());

            var menu = new ContextMenuStrip();

            menu.Items.AddRange(_toggleSceneItems);
            menu.Items.AddRange(_toggleScenes);
            menu.Items.Add(_toggleStream);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_exit);

            _trayIcon = new NotifyIcon
            {
                Icon = Resources.twitch32,
                ContextMenuStrip = menu,
                Visible = true
            };

            WireUpStateSubscriptions();

            _ = Task.Run(_obs.StartAsync);
        }

        private static Settings LoadSettings()
        {
            var config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddJsonFile("appsettings.prod.json", optional: true, reloadOnChange: true)
                .Build();

            return config.GetRequiredSection("Settings").Get<Settings>()
                   ?? throw new InvalidOperationException("Settings not found.");
        }

        private ToolStripMenuItem CreateMenuItem(string text, Func<Task> action)
        {
            return new ToolStripMenuItem(text, null, async (_, _) =>
            {
                try
                {
                    await action();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "OBS Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });
        }

        private void WireUpStateSubscriptions()
        {
            _obs.StreamStateChanged.Subscribe(async(isStreaming) =>
            {
                _toggleStream.Image = isStreaming ? Resources.redtvimg : null;
            });

            _obs.StateChanged.Subscribe(async (state) =>
            {
                Debug.WriteLine($"OBS Client State: {state}");

                bool ready = state == ObsClientState.Ready;

                _trayIcon.Icon = ready
                    ? Resources.icons8_twitch_32
                    : Resources.twitch32;


                foreach (var item in _toggleScenes)
                    item.Enabled = ready;
                
                foreach (var item in _toggleSceneItems)
                    item.Enabled = ready;

                _toggleStream.Enabled = ready;

                if (ready)
                {
                    for (int i = 0; i < _toggleScenes.Length; i++)
                    {
                        var sceneGroup = _settings.ScenesToToggle[i];

                        _toggleScenes[i].Enabled = await _obs.ValidateScenesExistAsync(sceneGroup.Scenes);
                    }

                    for (int i = 0; i < _toggleSceneItems.Length; i++)
                    {
                        var cfg = _settings.SceneItemsToToggle[i];

                        _toggleSceneItems[i].Enabled =
                            await _obs.ValidateSceneItemsExistAsync(
                                cfg.SceneName,
                                cfg.SceneItems);
                    }
                }
            });
        }

        private async Task ShutdownAsync()
        {
            _trayIcon.Visible = false;

            try
            {
                await _obs.DisposeAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Shutdown error: {ex}");
            }
            finally
            {
                Application.Exit();
            }
        }
    }
}
