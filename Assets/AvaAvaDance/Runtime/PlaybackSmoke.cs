using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        // Called by the Windows/Unity menu smoke test. The standalone harness
        // exercises frame arithmetic; this checks the real transport and UI state.
        void CheckPlaybackModes()
        {
            void Check(bool ok, string message)
            { if (!ok) throw new InvalidOperationException("Playback: " + message); }

            StopPlayback();
            var previous = (ribbonProfile, frame, transportStart, transportEnd, transportRepeat, physicsMode, forceOnPlayback);
            var oldBookmarks = bookmarks.ToArray();
            const string shortcut = "タイムライン|F11";
            bool hadShortcut = keyOverrides[1].TryGetValue(shortcut, out string oldShortcut);
            var oldKeys = selectedKeys.ToArray(); bool oldRange = rangeSelected;
            var tracks = sceneTracks.Concat(drawOrder.SelectMany(a => a.modelTracks)).ToArray();
            var drafts = tracks.Select(t => t.CurrentValue).ToArray();
            var test = new MotionTrack<float>("Playback smoke", 0, Mathf.Lerp, _ => {});
            test.Write(90, 1f); sceneTracks.Add(test);
            try
            {
                physicsMode = PhysicsMode.None; forceOnPlayback = false;
                ApplyProfile(false); playFrom.value = "8"; playTo.value = "15"; repeat.value = false;
                SetFrame(12); TogglePlayback();
                Check(playing && frame == 8 && playEnd == 15, "MMD explicit range");
                AdvancePlayback(startedAt + 7.1 / 30); Check(frame == 15 && playing, "inclusive final frame");
                AdvancePlayback(startedAt + 8.1 / 30); Check(frame == 15 && !playing, "MMD range ends");
                repeat.value = true; TogglePlayback(); AdvancePlayback(startedAt + 8.1 / 30);
                Check(playing && frame == 8 && audioCycle == 1, "MMD range loops with audio restart");
                StopPlayback();

                ApplyProfile(true); SetFrame(30); TogglePlayback();
                int end = PlaybackLastFrame;
                Check(playing && frame == 30 && playEnd == end, "MMM starts at playhead");
                AdvancePlayback(startedAt + (end - 30 + 1.1) / 30);
                Check(playing && frame == 0 && audioCycle == 1, "MMM repeats from zero");
                TogglePlayback(); Check(!playing && frame == 0 && !soundtrack.isPlaying, "play toggles stop");
                SetFrame(30); PlayFromBeginning(); Check(playing && frame == 0, "play from beginning");
                PlayFromBeginning(); Check(!playing, "beginning button stops playback");

                bookmarks.Clear(); bookmarks.UnionWith(new[] { 20, 40, 60 });
                SetFrame(30); PlayBetweenBookmarks();
                Check(playing && frame == 20 && playEnd == 39, "bookmark range");
                AdvancePlayback(startedAt + 19.1 / 30); Check(frame == 39 && audioCycle == 0, "bookmark endpoint");
                AdvancePlayback(startedAt + 20.1 / 30); Check(frame == 20 && audioCycle == 1, "bookmark loop");
                double clock = startedAt;
                ApplyProfile(false);
                Check(playing && startedAt == clock && playFrom.value == "8" && playTo.value == "15", "profile switch retains playback and MMD limits");
                AdvancePlayback(clock + 21.1 / 30); Check(frame == 21, "active bookmark loop survives profile switch");
                StopPlayback(); ApplyProfile(true); repeat.value = false;
                SetFrame(40); PlayBetweenBookmarks();
                Check(frame == 40 && playEnd == 59, "exact bookmark starts following section");
                AdvancePlayback(startedAt + 20.1 / 30); Check(!playing && frame == 59, "bookmark playback stops at section end");
                bookmarks.Clear(); SetFrame(30); PlayBetweenBookmarks();
                Check(frame == 0 && playEnd == end, "no bookmarks uses whole motion");
                PlayBetweenBookmarks(); Check(!playing, "bookmark button stops playback");

                Check(!ribbonById["transport.playFromBeginning"].HasDefault &&
                    !ribbonById["transport.playBetweenBookmarks"].HasDefault, "new actions have no invented default key");
                using (var key = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.F11 }))
                {
                    key.target = timeline;
                    keyOverrides[1][shortcut] = "transport.playFromBeginning";
                    SetFrame(30); Check(RouteNamedKey(key) && playing && frame == 0, "custom key starts from beginning");
                    Check(RouteNamedKey(key, repeated: true) && playing, "held custom key does not stop playback");
                    Check(RouteNamedKey(key) && !playing, "custom key toggles stop");
                    keyOverrides[1][shortcut] = "transport.playBetweenBookmarks";
                    bookmarks.UnionWith(new[] { 20, 40 }); SetFrame(30);
                    Check(RouteNamedKey(key) && playing && frame == 20 && playEnd == 39, "custom key starts bookmark range");
                    StopPlayback();
                }
                Debug.Log("AVA_PLAYBACK_MODES PASS MMD range, MMM current/start/bookmarks, loop cycles, stop, custom keys and profile switching");
            }
            finally
            {
                StopPlayback(); sceneTracks.Remove(test);
                if (hadShortcut) keyOverrides[1][shortcut] = oldShortcut; else keyOverrides[1].Remove(shortcut);
                bookmarks.Clear(); bookmarks.UnionWith(oldBookmarks);
                physicsMode = previous.physicsMode; forceOnPlayback = previous.forceOnPlayback;
                playFrom.value = previous.transportStart; playTo.value = previous.transportEnd; repeat.value = previous.transportRepeat;
                ApplyProfile(previous.ribbonProfile); SetFrame(previous.frame);
                selectedKeys.Clear(); foreach (var key in oldKeys) selectedKeys.Add(key); rangeSelected = oldRange;
                for (int i = 0; i < tracks.Length; i++) tracks[i].Restore(drafts[i]);
                RefreshRigPreview(); RestoreSolvedPose(); ApplyView(); SyncControls(); RefreshKeyButtons();
            }
        }
    }
}
