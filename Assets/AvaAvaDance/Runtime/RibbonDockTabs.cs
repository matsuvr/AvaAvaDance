using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        VisualElement audioSettings, dopeSheetPane;
        Button timeTab, waveTab, graphTab, curveTab;
        bool audioDockTab, graphDockTab;

        void BuildRibbonTimelineTabs(VisualElement header, VisualElement dopeSheet, VisualElement editorTools)
        {
            dopeSheetPane = dopeSheet;
            audioSettings = Box(keyframeEditor, "audio-controls", "row", "audio-controls");
            audioSettings.RemoveFromHierarchy();
            keyframeEditor.Insert(keyframeEditor.IndexOf(header) + 1, audioSettings);
            Action(audioSettings, "WAV読込", true, () => Execute(Command.LoadWave));
            Action(audioSettings, "開始フレーム", true, () => Execute(Command.AudioPlacement));
            Action(audioSettings, "スクラブ", true, () => Execute(Command.ScrubWave));
            Action(audioSettings, "ミュート", true, () => Execute(Command.MuteWave));
            Action(audioSettings, "独立ウィンドウ", true, () => ToggleTimeWindow(true));
            timeTab = Action(header, "Dope Sheet", true, () => SetKeyframeTab(0)); timeTab.name = "keyframe-tab-dope";
            graphTab = Action(header, "Interpolation Curves", true, () => SetKeyframeTab(1)); graphTab.name = "keyframe-tab-graph";
            waveTab = Action(header, "Audio", true, () => SetKeyframeTab(2)); waveTab.name = "keyframe-tab-audio";
            Action(header, "Palette", true, () => SelectInspector(InspectorTab.Curve));
            Action(header, "Tools", true, () => editorTools.ToggleInClassList("hidden"));
            showInlineCurve = WorkspacePreferences.GetInt("Ribbon.InlineCurve") != 0;
            curveTab = Action(header, "Curve", true, () => {
                showRibbonCurve = !showRibbonCurve;
                WorkspacePreferences.SetInt("Workspace.InlineCurve", showRibbonCurve ? 1 : 0);
                PresentTimelineTab(ribbonProfile);
            });
        }

        void SetKeyframeTab(int index)
        {
            if (KeysCaptured()) return;
            graphDockTab = index == 1;
            audioDockTab = index == 2;
            PresentTimelineTab(ribbonProfile);
            if (!audioDockTab) RefreshTimelineLabels();
        }

        void PresentTimelineTab(bool moving)
        {
            audioSettings.EnableInClassList("hidden", !audioDockTab);
            waveform.EnableInClassList("dock-audio", audioDockTab);
            timeline.EnableInClassList("hidden", audioDockTab);
            dopeSheetPane.Q("time-scroll").EnableInClassList("hidden", audioDockTab);
            graphPane.EnableInClassList("hidden", !graphDockTab);
            dopeSheetPane.EnableInClassList("graph-tracks", graphDockTab);
            timeTab.EnableInClassList("active-tool", !graphDockTab && !audioDockTab);
            graphTab.EnableInClassList("active-tool", graphDockTab);
            waveTab.EnableInClassList("active-tool", audioDockTab);
            curveTab.EnableInClassList("hidden", graphDockTab || audioDockTab);
            curveTab.EnableInClassList("active-tool", showRibbonCurve);
            MoveCurveControls(); RefreshCurveViews(); waveform.MarkDirtyRepaint();
        }
    }
}
