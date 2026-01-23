using System;

namespace SysTrayOBS
{
    public class SceneToggleGroup
    {
        public string[] Scenes { get; set; } = Array.Empty<string>();
    }
    public sealed class SceneItemsToToggle
    {
        public string SceneName { get; init; } = string.Empty;
        public string[] SceneItems { get; init; } = Array.Empty<string>();
    }

    public sealed class Settings
    {
        public string Uri { get; init; } = string.Empty;
        public string Password { get; init; } = string.Empty;
        public SceneToggleGroup[] ScenesToToggle { get; set; } = [];
        public SceneItemsToToggle[] SceneItemsToToggle { get; set; } = [];
    }
}
