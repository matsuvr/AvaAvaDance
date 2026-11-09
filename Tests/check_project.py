"""Static contract checks only; does NOT compile or run Unity."""
import json
from pathlib import Path
root = Path(__file__).resolve().parents[1]
assert '2022.3.22f1' in (root / 'ProjectSettings/ProjectVersion.txt').read_text()
json.loads((root / 'Packages/manifest.json').read_text())
runtime = (root / 'Assets/AvaAvaDance/Runtime/DanceApp.cs').read_text()
assert 'UnityEditor' not in runtime
assert 'RuntimeInitializeOnLoadMethod' in runtime
assert 'UIDocument' in runtime and 'PanelSettings' in runtime
assert 'Resources.Load<ThemeStyleSheet>("ClassicTheme")' in runtime
for asset in ['Classic.uss', 'ClassicTheme.tss']:
    assert (root / 'Assets/AvaAvaDance/Resources' / asset).exists()
for panel in ['sidebar', 'viewport', 'bone', 'morph', 'camera', 'light', 'accessory', 'playback']:
    assert '"' + panel + '"' in runtime, panel
assert 'Mathf.Clamp(value, 0, 10000)' in runtime
assert 'void RefreshTimelineLabels()' in runtime
assert 'timeline.Clear()' not in runtime.split('void DrawTimeline')[1].split('void RefreshTimelineLabels')[0]
assert '--- !u!104' in (root / 'Assets/Scenes/Main.unity').read_text()
for path in (root / 'Assets').rglob('*'):
    if path.is_file() and path.suffix != '.meta':
        assert Path(str(path) + '.meta').exists(), str(path)
print('PASS: static project contracts (not a Unity compile/play/build test)')
