using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        VisualElement dockTabs,audioSettings;
        Button timeTab,waveTab,curveTab;
        bool audioDockTab;

        void BuildRibbonTimelineTabs(VisualElement sidebar)
        {
            audioSettings=Box(sidebar,"audio-controls","row");
            Action(audioSettings,"WAV読込",true,()=>Execute(Command.LoadWave));
            Action(audioSettings,"ミュート",true,()=>Execute(Command.MuteWave));
            Action(audioSettings,"独立ウィンドウ",true,()=>ToggleTimeWindow(true));
            dockTabs=Box(sidebar,"timeline-tabs","row");
            void Choose(bool wave){
                WorkspacePreferences.SetInt("Ribbon.DockTab",wave?1:0);PresentTimelineTab(true);
                if(!wave)RefreshTimelineLabels();
            }
            timeTab=Action(dockTabs,"タイムライン",true,()=>Choose(false));
            waveTab=Action(dockTabs,"オーディオ",true,()=>Choose(true));
            showInlineCurve=WorkspacePreferences.GetInt("Ribbon.InlineCurve")!=0;
            curveTab=Action(dockTabs,"区間の補間",true,()=>{showInlineCurve=!showInlineCurve;WorkspacePreferences.SetInt("Ribbon.InlineCurve",showInlineCurve?1:0);PresentTimelineTab(true);});
        }
        void PresentTimelineTab(bool moving)
        {
            audioDockTab=moving && WorkspacePreferences.GetInt("Ribbon.DockTab")!=0;
            dockTabs.EnableInClassList("hidden",!moving);
            audioSettings.EnableInClassList("hidden",!audioDockTab);
            waveform.EnableInClassList("dock-audio",audioDockTab);
            timeline.EnableInClassList("hidden",audioDockTab);
            MenuRoot.Q("frame-heading").EnableInClassList("hidden",moving);
            MenuRoot.Q<Label>("track-heading").text=moving?(audioDockTab?"オーディオ":"タイムライン"):"ボーン・フレーム操作";
            timeTab.EnableInClassList("active-tool",!audioDockTab);waveTab.EnableInClassList("active-tool",audioDockTab);
            curveTab.EnableInClassList("hidden",audioDockTab);curveTab.EnableInClassList("active-tool",showInlineCurve);RefreshCurveViews();
            waveform.MarkDirtyRepaint();
        }
    }
}
