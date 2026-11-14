# /// script
# requires-python = ">=3.10,<3.15"
# dependencies = ["mcp==1.30.0"]
# ///
"""Exercise the shipped stdio server against a fresh Windows player with private test preferences."""
import asyncio
import base64
import ctypes
import json
import subprocess
import tempfile
import wave
from pathlib import Path

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

ROOT = Path(__file__).resolve().parents[1]


async def exercise(session, pid, directory):
    async def call(name, **args):
        result = await session.call_tool("ava_" + name, args)
        assert not result.isError, (name, result.content)
        if result.content[0].type == "image":
            return base64.b64decode(result.content[0].data)
        return json.loads(result.content[0].text)

    async def until(probe, message):
        for _ in range(60):
            result = await probe()
            if result:
                return result
            await asyncio.sleep(0.25)
        raise AssertionError(message)

    async def ready():
        return any(item["pid"] == pid for item in (await call("instances"))["instances"])

    async def ui(query, label=None):
        return next((e for e in (await call("ui", query=query))["items"] if label is None or e["label"] == label), None)

    async def click(query, label):
        row = await ui(query, label)
        assert row, label
        return await call("control", id=row["id"], action="activate")

    await session.initialize()
    assert len((await session.list_tools()).tools) >= 20
    await until(ready, "Player did not publish an endpoint")
    assert (await call("connect", pid=pid))["pid"] == pid
    summary = await call("state")
    player_window = next(w["window"] for w in (await call("native_ui"))["windows"] if w["class"] == "UnityWndClass")
    hwnd_arg = ctypes.c_void_p(player_window)
    ctypes.windll.user32.ShowWindow(hwnd_arg, 6)  # SW_MINIMIZE
    try:
        await asyncio.sleep(12)  # Let the background activity lease expire.
        assert (await call("transport", action="seek", frame=2))["frame"] == 2
    finally:
        ctypes.windll.user32.ShowWindow(hwnd_arg, 9)  # SW_RESTORE
    menus = (await call("commands", limit=256))["items"]
    assert any(m["id"] == "core.LoadPose" and not m["enabled"] for m in menus)
    assert not any(m["id"].startswith("ava.vmd-sizing.") for m in menus)
    assert all(p["id"] != "ava.vmd-sizing" for p in (await call("state", section="extensions"))["items"])
    ext = "ava.mme.assign"
    assert any(m["id"] == ext for m in menus)
    print(f"Discovered {len(menus)} menu commands and {len((await session.list_tools()).tools)} MCP tools", flush=True)

    frame = 18
    assert (await call("transport", action="seek", frame=frame))["frame"] == frame
    camera = next(t for t in (await call("tracks"))["items"] if t["channel"] == "Camera")
    handle = {"owner": camera["owner"], "track": camera["id"]}
    keys = await call("keys", **handle)
    value = keys["current"].copy()
    value["Distance"] += 2
    edit = {"Owner": camera["owner"], "Track": camera["id"], "Frame": frame,
            "Revision": keys["revision"], "Value": value}
    await call("edit_keys", document=summary["document"], edits=[edit])
    assert (await call("sample", **handle, frame=frame))["Distance"] == value["Distance"]
    stale = await session.call_tool("ava_edit_keys", {"document": summary["document"], "edits": [edit]})
    assert stale.isError
    await call("transport", action="undo")
    assert (await call("sample", **handle, frame=frame))["Distance"] == keys["current"]["Distance"]
    await call("transport", action="redo")
    assert (await call("preview", size=240)).startswith(b"\x89PNG")
    assert (await call("preview", size=320, surface="workspace")).startswith(b"\x89PNG")
    await call("command", id="core.OutputSize")
    assert (await call("state"))["dialog"]
    width = await ui("幅", "幅")
    await call("control", id=width["id"], action="set", value=320)
    ok = await ui("OK", "OK")
    pos = {"id": ok["id"], "x": ok["bounds"][2] / 2, "y": ok["bounds"][3] / 2}
    await call("input", kind="down", **pos)
    await call("input", kind="up", **pos)
    assert not (await call("state"))["dialog"]
    assert (await call("state", section="render"))["output"]["width"] == 320
    print("Passed key edit, stale revision, undo/redo, preview and modal button", flush=True)

    picker = await ui("unitypackage")
    assert picker and picker["type"] == "Button"
    fixture = ROOT / "Tests" / "triangle.unitypackage"
    if not fixture.exists():
        subprocess.run(["python", str(ROOT / "Tests" / "make_smoke_package.py")], cwd=ROOT, check=True)
    await call("control", id=picker["id"], action="activate", paths=[str(fixture)])
    consent = await until(lambda: ui("このアプリで利用できるアバターです", "このアプリで利用できるアバターです"), "No import dialog")
    await call("control", id=consent["id"], action="set", value=True)
    await click("読み込む", "読み込む")
    scene = await call("state", section="scene")
    owner = scene["Objects"][0]["Id"]
    assert (await call("state", section="materials"))["items"]
    assert (await call("tracks", owner=owner))["items"]
    await call("select", owner="scene", track=camera["id"])
    for section in ["timeline", "audio", "render", "physics", "extensions", "project", "document"]:
        assert await call("state", section=section)
    project_page = await call("state", section="project", pointer="/sceneMotion/tracks", limit=1)
    assert len(project_page["items"]) == 1 and project_page["nextOffset"] == 1
    path = directory / "test.avadance"
    await call("command", id="core.SaveAs", paths=[str(path)])
    assert path.is_file() and not (await call("state", section="document"))["modified"]
    await call("command", id="core.New")
    assert not (await call("state", section="scene"))["Objects"]
    await call("command", id="core.Open", paths=[str(path)])
    assert (await call("state", section="scene"))["Objects"][0]["Id"] == owner
    bad_file = directory / "broken.avadance"
    bad_file.write_bytes(b"invalid archive")
    assert (await session.call_tool("ava_command", {"id": "core.Open", "paths": [str(bad_file)]})).isError
    assert (await call("state"))["projectPath"] == str(path)
    print("Passed avatar import, state sections and project save/new/reopen", flush=True)

    audio = directory / "beat.wav"
    with wave.open(str(audio), "wb") as sound:
        sound.setparams((1, 2, 44100, 0, "NONE", "not compressed"))
        sound.writeframes(bytes(44100 * 2))
    await call("command", id="core.LoadWave", paths=[str(audio)])

    async def audio_loaded():
        return (await call("state", section="audio"))["samples"] == 44100

    await until(audio_loaded, "WAV not loaded")
    assert (await call("transport", action="play"))["playing"]
    assert not (await call("transport", action="stop"))["playing"]
    for cmd, suffix in [("SaveMotion", ".avamotion"), ("ExportImage", ".png")]:
        output = directory / ("frame" + suffix)
        await call("command", id="core." + cmd, paths=[str(output)])
        assert output.stat().st_size > 0
    movie = directory / "clip.mp4"
    await call("command", id="core.ExportMp4", paths=[str(movie)])
    for field in (await call("ui", query="TextField"))["items"]:
        if field["type"] == "TextField":
            await call("control", id=field["id"], action="set", value="0")
    await click("OK", "OK")

    async def exported():
        result = await call("state")
        assert not result["exportError"], result
        return not result["exporting"] and movie.is_file()

    await until(exported, "MP4 did not finish")
    assert movie.stat().st_size > 0
    print("Passed WAV, playback, AvaAvaDance motion/PNG export and MP4 rendering", flush=True)

    await call("command", id=ext)

    async def window():
        return next((w for w in (await call("native_ui"))["windows"] if w["class"] == "AvaDanceToolWindow"), None)

    hwnd = (await until(window, "No native extension window"))["window"]
    controls = (await call("native_ui", window=hwnd, limit=128))["items"]
    cancel = next(e for e in controls if e["name"] == "キャンセル" and e["enabled"] and "click" in e["actions"])
    await call("native_input", window=hwnd, control=cancel["id"], kind="keyDown", virtual_key=9)
    await call("native_input", window=hwnd, control=cancel["id"], kind="keyUp", virtual_key=9)
    assert (await call("native_image", window=hwnd)).startswith(b"\x89PNG")
    await call("native_control", window=hwnd, control=cancel["id"], action="click")

    async def closed():
        return not any(w["window"] == hwnd for w in (await call("native_ui"))["windows"])

    await until(closed, "Native extension window did not close")
    await call("command", id="core.Quit")
    print("Passed native extension control, screenshot and quit", flush=True)


async def main():
    exe = ROOT / "Builds" / "Windows" / "AvaAvaDance.exe"
    with tempfile.TemporaryDirectory(prefix="ava-mcp-") as tmp:
        directory = Path(tmp)
        player = subprocess.Popen([str(exe), "-avaSmokeOutput", tmp, "-logFile", str(directory / "player.log")])
        try:
            script = exe.parent / "Mcp" / "ava_mcp.py"
            params = StdioServerParameters(command="uv", args=["run", "--script", str(script)])
            async with stdio_client(params) as (reader, writer), ClientSession(reader, writer) as session:
                await exercise(session, player.pid, directory)
            assert await asyncio.to_thread(player.wait, 15) == 0
        finally:
            if player.poll() is None:
                player.terminate()
                player.wait(timeout=10)


if __name__ == "__main__":
    asyncio.run(main())
