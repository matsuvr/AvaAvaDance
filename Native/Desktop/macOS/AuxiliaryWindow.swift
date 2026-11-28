import AppKit
import SwiftUI

@MainActor
final class AuxiliaryWindow: NSObject, NSWindowDelegate {
    let id: Int
    let surface: WorkspaceSurface
    private let window: NSWindow

    init(id: Int, name: String, description: Packet) {
        self.id = id
        let frame = NSRect(x: 0, y: 0, width: description.scalar("width") ?? 640,
                           height: description.scalar("height") ?? 400)
        surface = WorkspaceSurface(frame: frame)
        surface.windowID = id
        surface.windowName = name
        window = NSWindow(contentRect: frame, styleMask: [.titled, .closable, .resizable, .miniaturizable],
                          backing: .buffered, defer: false)
        super.init()
        surface.apply(description)
        window.isReleasedWhenClosed = false
        window.appearance = NSAppearance(named: .darkAqua)
        window.delegate = self
        window.contentView = NSHostingView(rootView: WorkspaceView(surface: surface).frame(maxWidth: .infinity, maxHeight: .infinity))
        window.setFrameAutosaveName("AvaWorkspace." + name)
        window.acceptsMouseMovedEvents = true
        window.makeKeyAndOrderFront(nil)
    }
    func update(_ node: Packet) {
        window.title = node["title"] as? String ?? "AvaAvaDance"
        window.level = node["top"] as? Bool == true ? .floating : .normal
        surface.apply(node)
    }
    func windowShouldClose(_ sender: NSWindow) -> Bool {
        surface.commitPendingEdits()
        surface.send(id, ["action": "window.close"])
        return false
    }
    func dismiss() {
        window.delegate = nil
        window.contentView = nil
        window.close()
    }
}
