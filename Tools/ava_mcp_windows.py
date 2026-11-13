"""Windows-only operations on HWNDs owned by the authenticated AvaAvaDance instance."""

import ctypes
from io import BytesIO
from itertools import islice

import win32api
import win32con
import win32gui
import win32process
import win32ui
from comtypes import COMError
from PIL import Image
from pywinauto import Desktop
from pywinauto.uia_defines import NoPatternInterfaceError


def windows(pid, editor):
    result = {}

    def collect(hwnd, _):
        if win32process.GetWindowThreadProcessId(hwnd)[1] != pid or not win32gui.IsWindowVisible(hwnd):
            return
        cls = win32gui.GetClassName(hwnd)
        if editor:
            # Unity Editor windows belong to the same process. Only expose app extension windows
            # and the dialogs that they own while in Play Mode.
            owner = hwnd
            while owner and win32gui.GetClassName(owner) != "AvaDanceToolWindow":
                owner = win32gui.GetWindow(owner, win32con.GW_OWNER)
            if not owner:
                return
        result[hwnd] = {"window": hwnd, "title": win32gui.GetWindowText(hwnd), "class": cls}

    win32gui.EnumWindows(collect, None)
    return result


def require_window(pid, editor, hwnd):
    if hwnd not in windows(pid, editor):
        raise ValueError("Window is closed or does not belong to this AvaAvaDance instance.")


def elements(pid, hwnd):
    root = Desktop(backend="uia").window(handle=hwnd).wrapper_object()
    yield "root", root
    for child in root.descendants(process=pid):
        runtime = child.element_info.runtime_id
        if runtime:
            yield ".".join(map(str, runtime)), child


def pattern(control, name):
    try:
        return getattr(control, "iface_" + name)
    except (NoPatternInterfaceError, COMError, AttributeError):
        return None


def describe(key, control):
    info = control.element_info
    rect = info.rectangle
    actions = ["click", "doubleClick"]
    if key == "root":
        actions.append("close")
    value = None
    for name, action in (("invoke", "invoke"), ("selection_item", "select"), ("toggle", "toggle"),
                         ("expand_collapse", "expand"), ("value", "set"), ("range_value", "set")):
        interface = pattern(control, name)
        if interface is None:
            continue
        if name in ("value", "range_value"):
            value = interface.CurrentValue
            if interface.CurrentIsReadOnly:
                continue
        elif name == "toggle":
            value = interface.CurrentToggleState
        elif name == "selection_item":
            value = bool(interface.CurrentIsSelected)
        elif name == "expand_collapse":
            actions.append("collapse")
        actions.append(action)
    return {"id": key, "name": info.name, "automationId": info.automation_id, "type": info.control_type,
            "enabled": info.enabled, "value": value, "actions": actions,
            "bounds": [rect.left, rect.top, rect.width(), rect.height()]}


def inspect(pid, editor, window, offset, limit):
    if window is None:
        return {"windows": list(windows(pid, editor).values())}
    require_window(pid, editor, window)
    page = list(islice(elements(pid, window), offset, offset + limit + 1))
    return {"items": [describe(key, control) for key, control in page[:limit]],
            "nextOffset": offset + limit if len(page) > limit else None}


def click(pid, window, control, double, x, y):
    rect = control.rectangle()
    x = rect.width() // 2 if x is None else x
    y = rect.height() // 2 if y is None else y
    if not (0 <= x < rect.width() and 0 <= y < rect.height()):
        raise ValueError("Point outside control.")
    point = (rect.left + x, rect.top + y)
    # Descend within this window, even if another app covers it. Never send global mouse input.
    hwnd = window
    while True:
        pos = win32gui.ScreenToClient(hwnd, point)
        child = win32gui.ChildWindowFromPointEx(hwnd, pos, win32con.CWP_SKIPINVISIBLE | win32con.CWP_SKIPDISABLED)
        if not child or child == hwnd:
            break
        if win32process.GetWindowThreadProcessId(child)[1] != pid:
            raise ValueError("Point intersects another process.")
        hwnd = child
    position = win32api.MAKELONG(pos[0] & 65535, pos[1] & 65535)
    for msg in ([win32con.WM_LBUTTONDOWN, win32con.WM_LBUTTONUP, win32con.WM_LBUTTONDBLCLK, win32con.WM_LBUTTONUP]
                if double else [win32con.WM_LBUTTONDOWN, win32con.WM_LBUTTONUP]):
        win32gui.PostMessage(hwnd, msg, 0 if msg == win32con.WM_LBUTTONUP else win32con.MK_LBUTTON, position)


def operate(pid, editor, window, key, action, value, x, y):
    require_window(pid, editor, window)
    control = next((item for k, item in elements(pid, window) if k == key), None)
    if control is None or not control.is_enabled():
        raise ValueError("Native control is stale or disabled. Inspect the window again.")
    if action in ("click", "doubleClick"):
        click(pid, window, control, action == "doubleClick", x, y)
    elif action == "close" and key == "root":
        win32gui.PostMessage(window, win32con.WM_CLOSE, 0, 0)
    elif action == "set":
        setter = pattern(control, "value")
        if setter is not None:
            setter.SetValue(value)
        else:
            control.iface_range_value.SetValue(float(value))
    elif action == "invoke":
        control.iface_invoke.Invoke()
    elif action == "select":
        control.iface_selection_item.Select()
    elif action == "toggle":
        control.iface_toggle.Toggle()
    elif action == "expand":
        control.iface_expand_collapse.Expand()
    elif action == "collapse":
        control.iface_expand_collapse.Collapse()
    else:
        raise ValueError("Unsupported action for this control.")
    return {"window": window, "control": key, "action": action}


def send_input(pid, editor, window, key, kind, x, y, button, wheel, virtual_key, text, ctrl, shift):
    require_window(pid, editor, window)
    element = next((item for k, item in elements(pid, window) if k == key), None)
    if element is None or not element.is_enabled():
        raise ValueError("Native control is stale or disabled.")
    rect = element.rectangle()
    # UI Automation list items may share the HWND of their containing control.
    target = element
    while not target.handle:
        target = target.parent()
    hwnd = target.handle
    if (hwnd != window and not win32gui.IsChild(window, hwnd)) or win32process.GetWindowThreadProcessId(hwnd)[1] != pid:
        raise ValueError("Target is outside the selected window.")
    if kind in ("keyDown", "keyUp"):
        if not 1 <= virtual_key <= 254:
            raise ValueError("Supply a Windows virtual-key code in 1..254.")
        scan = ctypes.windll.user32.MapVirtualKeyW(virtual_key, 0)
        flags = 1 | ((scan & 255) << 16)
        if kind == "keyUp":
            flags |= 3 << 30
        win32gui.PostMessage(hwnd, win32con.WM_KEYDOWN if kind == "keyDown" else win32con.WM_KEYUP, virtual_key, flags)
    elif kind == "text":
        units = text.encode("utf-16-le")
        for i in range(0, len(units), 2):
            win32gui.PostMessage(hwnd, win32con.WM_CHAR, int.from_bytes(units[i:i + 2], "little"), 1)
    else:
        if not (0 <= x < rect.width() and 0 <= y < rect.height()):
            raise ValueError("Point outside control.")
        screen_point = (rect.left + x, rect.top + y)
        point = screen_point if kind == "wheel" else win32gui.ScreenToClient(hwnd, screen_point)
        position = win32api.MAKELONG(point[0] & 65535, point[1] & 65535)
        down, up, flag = {
            "left": (win32con.WM_LBUTTONDOWN, win32con.WM_LBUTTONUP, win32con.MK_LBUTTON),
            "right": (win32con.WM_RBUTTONDOWN, win32con.WM_RBUTTONUP, win32con.MK_RBUTTON),
            "middle": (win32con.WM_MBUTTONDOWN, win32con.WM_MBUTTONUP, win32con.MK_MBUTTON),
        }[button]
        message = {"down": down, "up": up, "move": win32con.WM_MOUSEMOVE,
                   "drag": win32con.WM_MOUSEMOVE, "wheel": win32con.WM_MOUSEWHEEL}[kind]
        flags = (win32con.MK_CONTROL if ctrl else 0) | (win32con.MK_SHIFT if shift else 0)
        if kind in ("down", "drag"):
            flags |= flag
        if kind == "wheel":
            flags = win32api.MAKELONG(flags, wheel & 65535)
        win32gui.PostMessage(hwnd, message, flags, position)
    return {"window": window, "control": key, "kind": kind}


def picture(pid, editor, hwnd):
    require_window(pid, editor, hwnd)
    left, top, right, bottom = win32gui.GetWindowRect(hwnd)
    width, height = right - left, bottom - top
    if width <= 0 or height <= 0 or width * height > 16_777_216:
        raise ValueError("Window is minimized or too large to capture.")
    handle = win32gui.GetWindowDC(hwnd)
    source = win32ui.CreateDCFromHandle(handle)
    target = source.CreateCompatibleDC()
    bitmap = win32ui.CreateBitmap()
    try:
        bitmap.CreateCompatibleBitmap(source, width, height)
        target.SelectObject(bitmap)
        if not ctypes.windll.user32.PrintWindow(ctypes.c_void_p(hwnd), ctypes.c_void_p(target.GetSafeHdc()), 2):
            raise ValueError("Window does not support PrintWindow.")
        image = Image.frombytes("RGB", (width, height), bitmap.GetBitmapBits(True), "raw", "BGRX")
        image.thumbnail((1600, 1600))
        output = BytesIO()
        image.save(output, "PNG")
        return output.getvalue()
    finally:
        target.DeleteDC()
        source.DeleteDC()
        win32gui.ReleaseDC(hwnd, handle)
        win32gui.DeleteObject(bitmap.GetHandle())
