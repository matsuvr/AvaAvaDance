using System;
using System.IO;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        void CheckProjectTransportRestore(string directory)
        {
            string path = Path.Combine(directory, "transport-state.avadance");
            SaveProject(path);
            var original = CaptureProject();
            void Require(bool pass, string message)
            { if (!pass) throw new InvalidOperationException("Project transport: " + message); }
            void Click(Action action)
            {
                ShowDialog("Transport restoration smoke", out var body);
                var button = Action(body, "Run", true, () => { CloseDialog(); action(); });
                ClickSmokeButton(button);
            }
            try
            {
                RestoreTransportSettings("111", "222", !original.repeat);
                soundVolume.value = original.volume < .5f ? .75f : .25f;
                Click(() => OpenProject(path));
                Require(transportStart == original.start && transportEnd == original.end && transportRepeat == original.repeat,
                    "load must restore canonical settings inside a UI callback");
                Require(!ProjectChanged(), "load must remain unmodified after queued UI events");
                Require(soundtrack.volume == original.volume && soundVolume.value == original.volume, "load restores audio volume");
                RestoreTransportSettings("111", "222", true);
                soundVolume.value = .25f;
                Click(NewProject);
                Require(transportStart == "" && transportEnd == "" && !transportRepeat && !repeat.value && !ribbonRepeat.value,
                    "new project must reset both transports inside a UI callback");
                Require(!ProjectChanged(), "new project must remain unmodified after queued UI events");
                Require(soundtrack.volume == 1 && soundVolume.value == 1, "new project resets audio volume");
                UnityEngine.Debug.Log("AVA_PROJECT_TRANSPORT PASS load/new from UI callbacks preserve clean saved state and synchronize both layouts");
            }
            finally { if (modal != null) CloseDialog(); OpenProject(path); }
        }
    }
}
