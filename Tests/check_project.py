"""Static contract checks only; does NOT compile or run Unity."""
import json
from pathlib import Path
root = Path(__file__).resolve().parents[1]
assert '2022.3.22f1' in (root / 'ProjectSettings/ProjectVersion.txt').read_text(encoding="utf-8-sig")
json.loads((root / 'Packages/manifest.json').read_text(encoding="utf-8-sig"))
runtime = (root / 'Assets/AvaAvaDance/Runtime/DanceApp.cs').read_text(encoding="utf-8-sig")
assert 'UnityEditor' not in runtime
assert 'RuntimeInitializeOnLoadMethod' in runtime
assert 'UIDocument' in runtime and 'PanelSettings' in runtime
assert 'Resources.Load<ThemeStyleSheet>("ClassicTheme")' in runtime
for asset in ['Classic.uss', 'ClassicTheme.tss']:
    assert (root / 'Assets/AvaAvaDance/Resources' / asset).exists()
timeline = (root / 'Assets/AvaAvaDance/Runtime/MotionTimeline.cs').read_text(encoding="utf-8-sig")
scene = (root / 'Assets/AvaAvaDance/Runtime/SceneMotion.cs').read_text(encoding="utf-8-sig")
bone = (root / 'Assets/AvaAvaDance/Runtime/BoneMotion.cs').read_text(encoding="utf-8-sig")
morph = (root / 'Assets/AvaAvaDance/Runtime/MorphMotion.cs').read_text(encoding="utf-8-sig")
accessories = (root / 'Assets/AvaAvaDance/Runtime/Accessories.cs').read_text(encoding='utf-8')
for panel in ['sidebar', 'viewport', 'bone', 'morph', 'camera', 'light', 'accessory', 'playback']:
    assert '"' + panel + '"' in runtime + scene + bone + morph + accessories, panel
assert 'Mathf.Clamp(value, 0, MaxFrame)' in timeline
assert 'void RefreshTimelineLabels()' in timeline
assert 'timeline.Clear()' not in timeline.split('void DrawTimeline')[1].split('void BuildPlayback')[0]
assert '--- !u!104' in (root / 'Assets/Scenes/Main.unity').read_text(encoding="utf-8-sig")
for path in (root / 'Assets').rglob('*'):
    if path.is_file() and path.suffix != '.meta':
        assert Path(str(path) + '.meta').exists(), str(path)
print('PASS: static project contracts (not a Unity compile/play/build test)')
