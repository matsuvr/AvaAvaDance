import AppKit

private final class FormTextView: NSTextView {
    weak var node: WorkspaceNode?
    var limit = 0

    override func didChangeText() {
        super.didChangeText()
        if !hasMarkedText() { node?.sendText(string, submit: false) }
    }
    override func shouldChangeText(in affectedCharRange: NSRange, replacementString: String?) -> Bool {
        if let replacementString, limit > 0 && (string as NSString).length - affectedCharRange.length +
            (replacementString as NSString).length > limit { return false }
        return super.shouldChangeText(in: affectedCharRange, replacementString: replacementString)
    }
    override func keyDown(with event: NSEvent) {
        if (event.keyCode == 36 || event.keyCode == 76) && !hasMarkedText() {
            if event.modifierFlags.contains(.shift) {
                insertText("\n", replacementRange: selectedRange())
            } else { node?.sendText(string, submit: true) }
            return
        }
        super.keyDown(with: event)
    }
    override func becomeFirstResponder() -> Bool {
        let focused = super.becomeFirstResponder()
        if focused, let node { node.surface?.send(node.id, ["action": "focus"]) }
        return focused
    }
}

private final class GestureSlider: NSSlider {
    weak var node: WorkspaceNode?
    override func mouseDown(with event: NSEvent) {
        guard let node, node.active, isEnabled else { return }
        node.surface?.send(node.id, ["action": "begin"])
        super.mouseDown(with: event)
        if node.active { node.surface?.send(node.id, ["action": "end"]) }
    }
}

final class WorkspaceNode: NSView, NSTextFieldDelegate {
    let id: Int
    let kind: String
    weak var surface: WorkspaceSurface?
    var active = true
    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { interactive }
    var interactive = false
    var isField: Bool { ["text", "integer", "number"].contains(kind) }
    var capturesKeys: Bool { model["keyCapture"] as? Bool == true }
    var image: NSImage? { didSet { if oldValue !== image { needsDisplay = true } } }
    private var control: NSControl?
    private var editor: FormTextView?
    private var editorScroll: NSScrollView?
    private var model: Packet = [:]
    private var interior = CGRect.zero
    private var enabled = true
    var canDrop: Bool { enabled && kind == "viewport" }
    private var lastSubmitted: String?

    init(id: Int, kind: String, surface: WorkspaceSurface) {
        self.id = id
        self.kind = kind
        self.surface = surface
        super.init(frame: .zero)
        wantsLayer = true
        layer?.masksToBounds = true
        switch kind {
        case "button":
            let b = NSButton(title: "", target: nil, action: nil)
            b.bezelStyle = .smallSquare
            control = b
        case "toggle":
            control = NSButton(checkboxWithTitle: "", target: nil, action: nil)
        case "choice":
            control = NSPopUpButton(frame: .zero, pullsDown: false)
        case "slider":
            let slider = GestureSlider(frame: .zero)
            slider.node = self
            slider.isContinuous = true
            control = slider
        case "text", "integer", "number":
            let field = NSTextField(frame: .zero)
            field.delegate = self
            field.isSelectable = true
            field.usesSingleLineMode = true
            field.lineBreakMode = .byTruncatingTail
            control = field
        case "multiline":
            let scroll = NSScrollView()
            let text = FormTextView(frame: .zero)
            text.node = self
            text.isRichText = false
            text.isVerticallyResizable = true
            text.isHorizontallyResizable = false
            text.autoresizingMask = [.width]
            text.textContainer?.widthTracksTextView = true
            scroll.hasVerticalScroller = true
            scroll.autohidesScrollers = true
            scroll.borderType = .bezelBorder
            scroll.documentView = text
            editor = text
            editorScroll = scroll
            addSubview(scroll)
        case "label":
            let field = NSTextField(labelWithString: "")
            field.maximumNumberOfLines = 1
            control = field
        default: break
        }
        if let control {
            control.controlSize = .small
            control.target = self
            control.action = #selector(activate)
            addSubview(control)
        }
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is unavailable") }

    override func hitTest(_ point: NSPoint) -> NSView? {
        guard interactive else { return nil }
        if kind == "label" || capturesKeys { return frame.contains(point) ? self : nil }
        return super.hitTest(point)
    }

    func update(_ node: Packet, geometry: CGRect, visible: CGRect) {
        if (node as NSDictionary).isEqual(to: model) { return }
        model = node
        enabled = node["enabled"] as? Bool ?? true
        interactive = node["hit"] as? Bool ?? (kind != "label")
        frame = visible
        interior = geometry.offsetBy(dx: -visible.minX, dy: -visible.minY)
        control?.frame = interior
        control?.isEnabled = enabled
        if let editor, let scroll = editorScroll {
            scroll.frame = interior
            editor.isEditable = enabled
            editor.minSize = scroll.contentSize
            editor.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
            editor.font = .systemFont(ofSize: node.scalar("fontSize") ?? 12)
            editor.limit = node["maxLength"] as? Int ?? 0
            editor.setAccessibilityIdentifier(node["name"] as? String ?? String(id))
            let value = node["value"] as? String ?? ""
            if editor.window?.firstResponder !== editor && editor.string != value { editor.string = value }
        }
        toolTip = (node["tip"] as? String).flatMap { $0.isEmpty ? nil : $0 }
        let title = node["text"] as? String ?? ""
        if let control {
            let name = node["name"] as? String ?? ""
            control.setAccessibilityIdentifier(name.isEmpty ? String(id) : name)
            control.setAccessibilityLabel(title.isEmpty ? toolTip ?? name : title)
            control.font = .systemFont(ofSize: max(7, node.scalar("fontSize") ?? 11))
        }
        if let menu = control as? NSPopUpButton {
            let choices = node["choices"] as? [String] ?? []
            let labels = node["choiceLabels"] as? [String] ?? choices
            if labels != menu.itemTitles {
                menu.removeAllItems()
                for title in labels { menu.menu?.addItem(NSMenuItem(title: title, action: nil, keyEquivalent: "")) }
            }
            if let selected = node["value"] as? String, let index = choices.firstIndex(of: selected), index < menu.numberOfItems {
                menu.selectItem(at: index)
                if let caption = node["selectedLabel"] as? String { menu.cell?.title = caption }
            }
        } else if let button = control as? NSButton {
            button.title = title
            if kind == "toggle" { button.state = node["value"] as? Bool == true ? .on : .off }
        } else if let slider = control as? NSSlider {
            slider.minValue = node["min"] as? Double ?? 0
            slider.maxValue = node["max"] as? Double ?? 1
            slider.isVertical = node["vertical"] as? Bool ?? false
            slider.doubleValue = node["value"] as? Double ?? 0
        } else if let field = control as? NSTextField {
            if kind == "label" {
                field.textColor = node.ink("color") ?? .labelColor
                field.maximumNumberOfLines = node["wrap"] as? Bool == true ? 0 : 1
                field.lineBreakMode = field.maximumNumberOfLines == 0 ? .byWordWrapping : .byClipping
                let align = node["align"] as? String
                field.alignment = align == "center" ? .center : align == "right" ? .right : .left
                field.stringValue = title
            } else {
                field.isEditable = enabled && node["readOnly"] as? Bool != true
                if field.currentEditor() == nil {
                    restoreText(field)
                }
            }
        }
        needsDisplay = true
    }

    @objc private func activate() {
        guard active, enabled, let control else { return }
        switch kind {
        case "button":
            surface?.commitPendingEdits()
            surface?.send(id, ["action": "activate"])
        case "toggle": surface?.send(id, ["action": "set", "value": (control as! NSButton).state == .on])
        case "choice":
            let index = (control as! NSPopUpButton).indexOfSelectedItem
            if let choices = model["choices"] as? [String], choices.indices.contains(index) {
                surface?.send(id, ["action": "set", "value": choices[index]])
            }
        case "slider":
            let value: Any = model["integer"] as? Bool == true ? Int(control.doubleValue.rounded()) as Any : control.doubleValue
            surface?.send(id, ["action": "set", "value": value])
        case "text", "integer", "number": submit(control.stringValue)
        default: break
        }
    }
    private func submit(_ text: String) {
        guard active, enabled, model["readOnly"] as? Bool != true, lastSubmitted != text else { return }
        let value: Any
        switch kind {
        case "number":
            guard let number = Double(text), number.isFinite else { return }
            value = number
        case "integer":
            guard let number = Int32(text) else { return }
            value = Int(number)
        default: value = text
        }
        lastSubmitted = text
        surface?.send(id, ["action": "set", "value": value])
    }
    private func restoreText(_ field: NSTextField) {
        let value = model["value"]
        let text = value as? String ?? (value as? NSNumber)?.stringValue ?? ""
        lastSubmitted = text
        if field.stringValue != text { field.stringValue = text }
    }
    func commitPendingEdit() {
        guard isField, let field = control as? NSTextField,
              let editing = field.currentEditor() as? NSTextView, !editing.hasMarkedText() else { return }
        submit(editing.string)
    }
    func sendText(_ value: String, submit: Bool) {
        guard active, enabled else { return }
        surface?.send(id, ["action": submit ? "submit" : "set", "value": value])
    }
    func controlTextDidChange(_ notification: Notification) {
        if model["delayed"] as? Bool != true, let field = notification.object as? NSTextField,
           (field.currentEditor() as? NSTextView)?.hasMarkedText() != true { submit(field.stringValue) }
    }
    func controlTextDidBeginEditing(_ notification: Notification) {
        if active { surface?.send(id, ["action": "focus"]) }
    }
    func controlTextDidEndEditing(_ notification: Notification) {
        guard active, enabled, let field = notification.object as? NSTextField else { return }
        let text = field.stringValue
        if lastSubmitted == text || (kind == "integer" && Int32(text) == nil) ||
            (kind == "number" && Double(text)?.isFinite != true) { restoreText(field) }
        else { submit(text) }
        surface?.send(id, ["action": "unfocus"])
    }

    override func draw(_ dirtyRect: NSRect) {
        if let background = model.ink("background") { background.setFill(); dirtyRect.fill() }
        if kind == "viewport" {
            image?.draw(in: interior, from: .zero, operation: .copy, fraction: 1, respectFlipped: true, hints: nil)
        }
        guard let paths = model["paths"] as? [Packet] else { return }
        for spec in paths {
            guard let commands = spec["commands"] as? [[Any]], let ink = spec.ink("color") else { continue }
            let path = NSBezierPath()
            func point(_ items: [Any], _ i: Int) -> NSPoint? {
                guard i + 1 < items.count, let x = items[i] as? NSNumber, let y = items[i + 1] as? NSNumber else { return nil }
                return .init(x: CGFloat(x.doubleValue) + interior.minX, y: CGFloat(y.doubleValue) + interior.minY)
            }
            for items in commands {
                switch items.first as? String {
                case "M": if let p = point(items, 1) { path.move(to: p) }
                case "L": if let p = point(items, 1) { path.line(to: p) }
                case "C":
                    if let c1 = point(items, 1), let c2 = point(items, 3), let p = point(items, 5) {
                        path.curve(to: p, controlPoint1: c1, controlPoint2: c2)
                    }
                case "Z": path.close()
                default: break
                }
            }
            if spec["fill"] as? Bool == true { ink.setFill(); path.fill() }
            else { ink.setStroke(); path.lineWidth = spec.scalar("width") ?? 1; path.stroke() }
        }
    }

    func pointer(_ event: NSEvent, _ kind: String) {
        guard active && enabled else { return }
        let p = convert(event.locationInWindow, from: nil)
        var packet = WorkspaceSurface.modifiers(event)
        packet["action"] = "input"
        packet["kind"] = kind
        packet["x"] = p.x - interior.minX
        packet["y"] = p.y - interior.minY
        let wheel = kind == "wheel"
        packet["dx"] = wheel ? -event.scrollingDeltaX : event.deltaX
        packet["dy"] = wheel ? -event.scrollingDeltaY : event.deltaY
        packet["clicks"] = (wheel || kind == "move") ? 0 : event.clickCount
        packet["button"] = wheel ? 0 : event.buttonNumber
        surface?.send(id, packet)
    }
    override func mouseDown(with event: NSEvent) {
        window?.makeFirstResponder(self)
        if capturesKeys { surface?.send(id, ["action": "focus"]) }
        else { pointer(event, "down") }
    }
    override func mouseDragged(with event: NSEvent) { pointer(event, "drag") }
    override func mouseUp(with event: NSEvent) { pointer(event, "up") }
    override func otherMouseDown(with event: NSEvent) { pointer(event, "down") }
    override func otherMouseDragged(with event: NSEvent) { pointer(event, "drag") }
    override func otherMouseUp(with event: NSEvent) { pointer(event, "up") }
    override func rightMouseDown(with event: NSEvent) { pointer(event, "down") }
    override func rightMouseDragged(with event: NSEvent) { pointer(event, "drag") }
    override func rightMouseUp(with event: NSEvent) { pointer(event, "up") }
}
