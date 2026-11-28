import AppKit

extension Dictionary where Key == String, Value == Any {
    func scalar(_ key: String) -> CGFloat? {
        (self[key] as? NSNumber).map { CGFloat($0.doubleValue) }
    }
    func rect(_ key: String) -> CGRect? {
        guard let values = self[key] as? [NSNumber], values.count == 4 else { return nil }
        let a = values.map { CGFloat($0.doubleValue) }
        guard a.allSatisfy({ $0.isFinite }) else { return nil }
        return CGRect(x: a[0], y: a[1], width: max(0, a[2]), height: max(0, a[3]))
    }
    func ink(_ key: String) -> NSColor? {
        guard let hex = self[key] as? String, hex.count == 9,
              let v = UInt32(hex.dropFirst(), radix: 16) else { return nil }
        return NSColor(srgbRed: CGFloat((v >> 16) & 255) / 255, green: CGFloat((v >> 8) & 255) / 255,
                       blue: CGFloat(v & 255) / 255, alpha: CGFloat(v >> 24) / 255)
    }
}

final class WorkspaceSurface: NSView {
    var windowName: String?
    var windowID = 0
    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }
    private var views: [Int: WorkspaceNode] = [:]
    private var paintOrder: [Int] = []
    private var viewport: Int?
    private var image: NSImage?
    private var lastSize = CGSize.zero
    private var lastScale: CGFloat = 0
    private var monitor: Any?
    private var deactivateObserver: NSObjectProtocol?
    private(set) var document = 0
    private(set) var layoutEpoch = -1
    private var errorLabel: NSTextField?

    func report(_ message: String) {
        let label = errorLabel ?? NSTextField(wrappingLabelWithString: "")
        errorLabel = label
        label.stringValue = message
        label.textColor = .white
        label.drawsBackground = true
        label.backgroundColor = .systemRed
        label.frame = CGRect(x: 0, y: 0, width: bounds.width, height: 48)
        addSubview(label, positioned: .above, relativeTo: nil)
    }

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        registerForDraggedTypes([.fileURL])
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is unavailable") }
    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation { dropTarget(sender) == nil ? [] : .copy }
    override func draggingUpdated(_ sender: NSDraggingInfo) -> NSDragOperation { draggingEntered(sender) }
    private func dropTarget(_ sender: NSDraggingInfo) -> Int? {
        guard let viewport, let view = views[viewport], view.canDrop,
              view.frame.contains(convert(sender.draggingLocation, from: nil)) else { return nil }
        return viewport
    }
    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        guard let target = dropTarget(sender),
              let urls = sender.draggingPasteboard.readObjects(forClasses: [NSURL.self],
                options: [.urlReadingFileURLsOnly: true]) as? [URL], !urls.isEmpty else { return false }
        send(target, ["action": "drop", "paths": urls.map(\.path)])
        return true
    }

    override func draw(_ dirtyRect: NSRect) {
        NSColor(calibratedWhite: 0.16, alpha: 1).setFill()
        dirtyRect.fill()
    }
    override func layout() {
        super.layout()
        sendSize(bounds.size)
    }
    override func viewDidChangeBackingProperties() {
        super.viewDidChangeBackingProperties()
        sendSize(bounds.size)
    }
    func sendSize(_ size: CGSize) {
        let size = CGSize(width: size.width.rounded(), height: size.height.rounded())
        let scale = window?.backingScaleFactor ?? 1
        guard size.width > 0 && size.height > 0 && (size != lastSize || scale != lastScale) else { return }
        lastSize = size
        lastScale = scale
        if windowName != nil {
            send(windowID, ["action": "window.resize", "width": Int(size.width), "height": Int(size.height)])
        } else {
            DesktopBridge.shared.send(["type": "resize", "width": Int(size.width), "height": Int(size.height), "scale": scale])
        }
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        if let monitor { NSEvent.removeMonitor(monitor); self.monitor = nil }
        if let deactivateObserver { NotificationCenter.default.removeObserver(deactivateObserver); self.deactivateObserver = nil }
        guard let window else { return }
        deactivateObserver = NotificationCenter.default.addObserver(forName: NSWindow.didResignKeyNotification,
            object: window, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated {
                    self?.commitPendingEdits()
                    self?.send(0, ["action": "blur"])
                }
            }
        monitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .keyUp, .mouseMoved, .scrollWheel, .rightMouseDown,
            .rightMouseUp, .leftMouseDown, .leftMouseUp, .leftMouseDragged, .rightMouseDragged]) { [weak self] event in
            guard let self, event.window === self.window, NSApp.modalWindow == nil else { return event }
            if event.type == .keyDown || event.type == .keyUp {
                // Let the field editor and the macOS menu handle text composition,
                // clipboard, Tab traversal and application-level shortcuts.
                guard !(self.window?.firstResponder is NSTextView),
                      event.keyCode != 48, let key = Self.keyName(event) else { return event }
                let node = self.window?.firstResponder as? WorkspaceNode
                if node?.capturesKeys != true {
                    if event.modifierFlags.contains(.command) && ["Q", "W", "H", "M"].contains(key) { return event }
                    if self.window?.firstResponder is NSControl && ["Return", "Space", "LeftArrow", "RightArrow", "UpArrow", "DownArrow"].contains(key) {
                        return event
                    }
                }
                var packet = Self.modifiers(event)
                packet["action"] = "input"
                packet["kind"] = event.type == .keyDown ? "keyDown" : "keyUp"
                packet["key"] = key
                self.send(node?.capturesKeys == true ? node!.id : 0, packet)
                return nil
            }
            // Native controls consume wheel and modifier-click events before
            // NSResponder can pass them to the logical control under the pointer.
            let p = self.convert(event.locationInWindow, from: nil)
            guard let node = self.node(at: p) else { return event }
            if event.type == .mouseMoved { node.pointer(event, "move"); return event }
            if event.type == .scrollWheel {
                if node.kind == "multiline" { return event }
                node.pointer(event, "wheel")
                return nil
            }
            if node.isField && (event.type == .rightMouseDown || event.type == .rightMouseUp ||
                event.type == .rightMouseDragged || !event.modifierFlags.intersection([.control, .shift, .command]).isEmpty) {
                let kind = [.leftMouseDown, .rightMouseDown].contains(event.type) ? "down" :
                    ([.leftMouseUp, .rightMouseUp].contains(event.type) ? "up" : "drag")
                node.pointer(event, kind)
                return nil
            }
            return event
        }
    }

    private static func keyName(_ event: NSEvent) -> String? {
        let special: [UInt16: String] = [36:"Return", 51:"Backspace", 53:"Escape", 117:"Delete",
            123:"LeftArrow", 124:"RightArrow", 125:"DownArrow", 126:"UpArrow", 49:"Space", 115:"Home",
            119:"End", 116:"PageUp", 121:"PageDown", 122:"F1", 120:"F2", 99:"F3", 118:"F4",
            96:"F5", 97:"F6", 98:"F7", 100:"F8", 101:"F9", 109:"F10", 103:"F11", 111:"F12",
            82:"Keypad0", 83:"Keypad1", 84:"Keypad2", 85:"Keypad3", 86:"Keypad4", 87:"Keypad5",
            88:"Keypad6", 89:"Keypad7", 91:"Keypad8", 92:"Keypad9", 76:"KeypadEnter"]
        if let key = special[event.keyCode] { return key }
        guard let text = event.charactersIgnoringModifiers?.uppercased(), text.count == 1 else { return nil }
        if let scalar = text.unicodeScalars.first, (65...90).contains(scalar.value) { return text }
        if Int(text) != nil { return "Alpha" + text }
        return [",":"Comma", ".":"Period", "/":"Slash", ";":"Semicolon", "-":"Minus", "=":"Equals",
                "[":"LeftBracket", "]":"RightBracket", "'":"Quote", "\\":"Backslash", "`":"BackQuote"][text]
    }
    static func modifiers(_ event: NSEvent) -> Packet {
        ["ctrl": !event.modifierFlags.intersection([.control, .command]).isEmpty,
         "shift": event.modifierFlags.contains(.shift), "alt": event.modifierFlags.contains(.option)]
    }
    func send(_ id: Int, _ message: Packet) {
        var packet = message
        packet["id"] = id
        packet["document"] = document
        if let windowName {
            packet["window"] = windowName
            packet["windowId"] = windowID
        }
        DesktopBridge.shared.send(packet)
    }
    func commitPendingEdits() {
        for node in views.values { node.commitPendingEdit() }
    }
    private func node(at point: NSPoint) -> WorkspaceNode? {
        for id in paintOrder.reversed() {
            if let item = views[id], item.interactive, item.frame.contains(point) { return item }
        }
        return nil
    }

    func apply(_ packet: Packet) {
        guard let nodes = packet["nodes"] as? [Packet], let generation = packet["document"] as? Int else { return }
        if let epoch = packet["layout"] as? Int, layoutEpoch != epoch {
            layoutEpoch = epoch
            image = nil
        }
        errorLabel?.removeFromSuperview()
        errorLabel = nil
        if generation != document {
            // Do not commit an edit that started in the previous project.
            views.values.forEach { $0.active = false }
            views.values.forEach { $0.removeFromSuperview() }
            views.removeAll()
            paintOrder.removeAll()
            image = nil
            document = generation
        }
        var order: [Int] = []
        for node in nodes {
            guard let id = node["id"] as? Int, let kind = node["kind"] as? String,
                  let geometry = node.rect("bounds") else { continue }
            let visible = geometry.intersection(node.rect("clip") ?? geometry)
            guard !visible.isEmpty else { continue }
            let view: WorkspaceNode
            if let old = views[id], old.kind == kind { view = old }
            else {
                views[id]?.active = false
                views[id]?.removeFromSuperview()
                view = WorkspaceNode(id: id, kind: kind, surface: self)
                views[id] = view
                addSubview(view)
            }
            view.update(node, geometry: geometry, visible: visible)
            if kind == "viewport" { viewport = id; view.image = image }
            order.append(id)
        }
        let live = Set(order)
        for id in views.keys.filter({ !live.contains($0) }) {
            let view = views.removeValue(forKey: id)
            view?.active = false
            view?.removeFromSuperview()
        }
        if order != paintOrder {
            subviews = order.compactMap { views[$0] }
            paintOrder = order
        }
    }
    func show(_ frame: CGImage) {
        guard let viewport, let view = views[viewport] else { return }
        image = NSImage(cgImage: frame, size: NSSize(width: CGFloat(frame.width), height: CGFloat(frame.height)))
        view.image = image
    }
}
