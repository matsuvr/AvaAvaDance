# /// script
# requires-python = ">=3.10,<3.15"
# dependencies = ["mcp==1.30.0", "pywinauto==0.6.9; sys_platform == 'win32'", "pillow>=11,<13; sys_platform == 'win32'"]
# ///
"""AvaAvaDance MCP server. Start with uv run --script ava_mcp.py."""

import asyncio
import base64
import json
import os
import socket
import struct
import subprocess
import time
from pathlib import Path
from typing import Annotated, Any, Literal

from mcp.server.fastmcp import FastMCP, Image
from pydantic import BaseModel, ConfigDict, Field

server = FastMCP("AvaAvaDance", instructions=(
    "Use ava_state for current status, ava_commands for menus, ava_ui for controls. "
    "File operations accept absolute paths on the app's machine. Read the returned status and dialog, "
    "and poll after imports/exports. Use ava_native_ui for separate extension windows."
))
selected = None
Offset = Annotated[int, Field(ge=0)]
Limit = Annotated[int, Field(ge=1, le=256)]
Frame = Annotated[int, Field(ge=0, le=600000)]


def read_exact(stream, size):
    data = stream.read(size)
    if len(data) != size:
        raise ConnectionError("AvaAvaDance closed the connection.")
    return data


def rpc(instance, tool, args, timeout=55):
    data = json.dumps({"tool": tool, "args": args}, ensure_ascii=False, allow_nan=False).encode("utf-8")
    if len(data) > 4 * 1024**2:
        raise ValueError("Request exceeds 4 MiB. Split the edit into smaller batches.")
    with socket.create_connection(("127.0.0.1", instance["port"]), timeout=min(timeout, 2)) as sock:
        sock.settimeout(timeout)
        sock.sendall(instance["token"].encode("ascii") + struct.pack("<I", len(data)) + data)
        with sock.makefile("rb") as stream:
            size, = struct.unpack("<I", read_exact(stream, 4))
            if size > 16 * 1024**2:
                raise ValueError("Reply exceeds the bridge limit.")
            reply = json.loads(read_exact(stream, size))
    if "error" in reply:
        raise ValueError(reply["error"])
    return reply["result"]


def instances():
    directory = Path(os.environ["LOCALAPPDATA"]) / "AvaAvaDance" / "Mcp"
    live = []
    for path in directory.glob("*.json"):
        try:
            item = json.loads(path.read_text(encoding="utf-8-sig"))
            if item["protocol"] == 1 and rpc(item, "identify", {}, 0.6)["pid"] == item["pid"]:
                live.append(item)
        except (OSError, KeyError, ValueError, TypeError):
            continue
    return live


def instance():
    global selected
    if selected is None:
        live = instances()
        if not live:
            raise ValueError("Start AvaAvaDance with ava_launch, or enter Play Mode in Unity.")
        if len(live) != 1:
            raise ValueError("Several instances are open. Use ava_instances and ava_connect(pid).")
        selected = live[0]
    return selected


def call(tool, **args):
    try:
        return rpc(instance(), tool, args)
    except OSError as err:
        raise ConnectionError(f"Connection interrupted. Check ava_instances, then ava_connect. "
                              f"Inspect state before repeating an edit. {err}") from err


def public(item):
    return {key: item[key] for key in ("pid", "editor", "appPath")}


@server.tool()
def ava_instances() -> dict:
    """List running players and Unity Play Mode instances on this Windows account."""
    return {"instances": [public(item) for item in instances()]}


@server.tool()
def ava_connect(pid: int) -> dict:
    """Select a live instance for this MCP client, including after a player restart."""
    global selected
    for item in instances():
        if item["pid"] == pid:
            selected = item
            return public(item)
    raise ValueError("No live AvaAvaDance instance with that PID.")


@server.tool()
async def ava_launch() -> dict:
    """Open the bundled Windows player and connect to its new session."""
    global selected
    root = Path(__file__).resolve().parent.parent
    exe = root / "AvaAvaDance.exe"
    if not exe.is_file():
        exe = root / "Builds" / "Windows" / "AvaAvaDance.exe"
    if not exe.is_file():
        raise FileNotFoundError("Build the Windows player first, or enter Play Mode in Unity.")
    player = subprocess.Popen([str(exe)], cwd=exe.parent, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                              stderr=subprocess.DEVNULL, creationflags=subprocess.DETACHED_PROCESS)
    deadline = time.monotonic() + 45
    while time.monotonic() < deadline:
        for item in await asyncio.to_thread(instances):
            if item["pid"] == player.pid:
                selected = item
                return public(item)
        if player.poll() is not None:
            raise RuntimeError(f"Player exited with code {player.returncode}.")
        await asyncio.sleep(0.25)
    raise TimeoutError(f"Player {player.pid} has not published its endpoint. Check ava_instances before launching again.")


@server.tool()
def ava_state(section: Literal["summary", "document", "scene", "timeline", "audio", "render", "physics",
                               "extensions", "materials", "rig", "project"] = "summary",
              owner: str | None = None, offset: Offset = 0, limit: Limit = 64, pointer: str | None = None) -> dict:
    """Read live state. document includes unsaved changes, scene lists owner IDs, rig needs a model owner,
    materials is paginated, project is the saved-format snapshot without binary assets. Use a JSON
    Pointer to return a subtree, e.g. /sceneMotion/tracks in project. Selected arrays are paginated."""
    result = call("state", section=section, owner=owner, offset=offset, limit=limit, pointer=pointer)
    if isinstance(result, dict):
        return result
    return {"items": result} if isinstance(result, list) else {"value": result}


@server.tool()
def ava_commands(offset: Offset = 0, limit: Limit = 128) -> dict:
    """Discover registered core and extension menu command IDs, Japanese/English labels, enabled and checked states."""
    return call("commands", offset=offset, limit=limit)


@server.tool()
def ava_command(id: str, paths: list[str] | None = None) -> dict:
    """Run an enabled menu command. Supply ordered absolute paths for open/save dialogs requested by this step.
    Inspect dialog/status and unusedPaths. A subsequent confirmation may request paths on its own click."""
    return call("command", id=id, paths=paths)


@server.tool()
def ava_ui(query: str = "", offset: Offset = 0, limit: Limit = 64, all_elements: bool = False) -> dict:
    """Find visible UI Toolkit controls by label, type, name, tooltip, or window. Lists IDs, values,
    choices, enabled state and bounds in panel points. Set all_elements for unlabelled drawing surfaces
    and internal children. Modal dialogs are listed while open."""
    return call("ui", query=query, offset=offset, limit=limit, all_elements=all_elements)


@server.tool()
def ava_control(id: int, action: Literal["activate", "set", "focus", "reveal"],
                value: str | int | float | bool | None = None, paths: list[str] | None = None) -> dict:
    """Click a button, change a field/toggle/choice, focus, or scroll to a control by its live ava_ui ID.
    Supply paths when this button opens a file picker. Refresh IDs after switching windows/dialogs."""
    return call("control", id=id, action=action, value=value, paths=paths)


@server.tool()
def ava_input(id: int, kind: Literal["down", "up", "move", "drag", "wheel", "keyDown", "keyUp"],
              x: float = 0, y: float = 0, dx: float = 0, dy: float = 0,
              button: Literal[0, 1, 2] = 0, clicks: Literal[1, 2] = 1, key: str | None = None,
              shift: bool = False, ctrl: bool = False, alt: bool = False, paths: list[str] | None = None) -> dict:
    """Send pointer, wheel or Unity KeyCode events to a live control. x/y are relative to the control's
    top left, dx/dy are drag/wheel deltas. Pair down/up for clicks and keyDown/keyUp for shortcuts."""
    return call("input", **locals())


@server.tool()
def ava_transport(action: Literal["seek", "play", "stop", "undo", "redo"], frame: Frame | None = None) -> dict:
    """Control the playhead, playback or undo history. Seek requires a frame in 0..600000."""
    if action == "seek" and frame is None:
        raise ValueError("Seek requires frame.")
    return call("transport", action=action, frame=frame)


@server.tool()
def ava_select(owner: str, track: str | None = None) -> dict:
    """Select scene, model or accessory by owner ID and optionally a track (see ava_tracks)."""
    return call("select", owner=owner, track=track)


@server.tool()
def ava_order(document: int, owners: list[str], physics: bool = False) -> dict:
    """Set model draw order, or simulation order when physics=true. Pass all owner IDs from ava_state(scene)."""
    return call("order", document=document, owners=owners, physics=physics)


@server.tool()
def ava_tracks(owner: str | None = None, offset: Offset = 0, limit: Limit = 64) -> dict:
    """List motion tracks, channels, revisions and key counts. Use owner and id to query or edit keys."""
    return call("tracks", owner=owner, offset=offset, limit=limit)


@server.tool()
def ava_keys(owner: str, track: str, offset: Offset = 0, limit: Limit = 32) -> dict:
    """Read current pose, document/revision and a page of complete keyframes, including interpolation."""
    return call("keys", owner=owner, track=track, offset=offset, limit=limit)


@server.tool()
def ava_sample(owner: str, track: str, frame: Annotated[float, Field(ge=0, le=600000)]) -> dict:
    """Sample the evaluated channel at an integer or fractional frame without moving the playhead."""
    return call("sample", owner=owner, track=track, frame=frame)


class KeyEdit(BaseModel):
    model_config = ConfigDict(extra="forbid")
    Owner: str
    Track: str
    Revision: int
    Frame: Frame
    Value: dict[str, Any] | None
    Curves: Literal["Preserve", "Replace", "Clear"] = "Preserve"
    SourceMetadata: Literal["Preserve", "Replace", "Clear"] = "Preserve"


@server.tool()
def ava_edit_keys(document: int, edits: list[KeyEdit]) -> dict:
    """Apply an undoable batch. Copy a complete Value from ava_keys/ava_sample and modify it, or set
    Value=null to delete. Use the document and track Revision from ava_keys. Frame zero cannot be deleted."""
    return call("edit_keys", document=document, edits=[edit.model_dump() for edit in edits])


@server.tool()
def ava_preview(size: Annotated[int, Field(ge=64, le=1600)] = 800,
                surface: Literal["camera", "workspace", "operations", "timeline", "audio"] = "camera") -> Image:
    """Return a PNG of the camera, full workspace including controls, or an open detached UI panel.
    size is the maximum pixel length of the longer side."""
    return Image(data=base64.b64decode(call("preview", size=size, surface=surface), validate=True), format="png")


def native_target():
    item = instance()
    rpc(item, "identify", {}, 2)  # Works even during a blocking Win32 dialog.
    return item["pid"], item["editor"]


@server.tool()
def ava_native_ui(window: int | None = None, offset: Offset = 0, limit: Limit = 64) -> dict:
    """List this instance's native windows. Supply a window handle to inspect its Windows UI Automation
    controls, IDs, bounds and supported actions (extension windows, file pickers and detached panels)."""
    from ava_mcp_windows import inspect
    return inspect(*native_target(), window, offset, limit)


@server.tool()
def ava_native_control(window: int, control: str, action: Literal["invoke", "set", "select", "toggle", "expand", "collapse",
                                                                  "click", "doubleClick", "close"],
                       value: str = "", x: int | None = None, y: int | None = None) -> dict:
    """Operate a native control discovered by ava_native_ui. Click coordinates are local pixels within
    that control (default center). close applies to the window root."""
    from ava_mcp_windows import operate
    return operate(*native_target(), window, control, action, value, x, y)


@server.tool()
def ava_native_input(window: int, control: str, kind: Literal["up", "down", "move", "drag", "wheel", "keyDown", "keyUp", "text"],
                     x: int = 0, y: int = 0, button: Literal["left", "right", "middle"] = "left",
                     wheel: Annotated[int, Field(ge=-32768, le=32767)] = 120, virtual_key: int = 0, text: str = "",
                     ctrl: bool = False, shift: bool = False) -> dict:
    """Send window-local mouse/drag/wheel, virtual-key or Unicode text messages to a native control.
    Coordinates are pixels within the inspected control. ctrl and shift modify mouse events."""
    from ava_mcp_windows import send_input
    return send_input(*native_target(), window, control, kind, x, y, button, wheel, virtual_key, text, ctrl, shift)


@server.tool()
def ava_native_image(window: int) -> Image:
    """Capture a selected native app window into a PNG (for native extension layouts)."""
    from ava_mcp_windows import picture
    return Image(data=picture(*native_target(), window), format="png")


if __name__ == "__main__":
    server.run(transport="stdio")
