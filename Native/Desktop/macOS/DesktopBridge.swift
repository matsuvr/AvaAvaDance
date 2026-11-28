@preconcurrency import AppKit
@preconcurrency import SwiftUI
import UniformTypeIdentifiers

typealias Packet = [String: Any]

private typealias Poll = @convention(c) (UnsafeMutableRawPointer?, Int32) -> Int32
private typealias Publish = @convention(c) (UnsafeRawPointer?, Int32) -> Void
private typealias Frame = @convention(c) (UnsafeRawPointer?, Int32, Int32, Int64, Int64) -> Void
private typealias Pick = @convention(c) (UnsafePointer<CChar>?, UnsafeMutableRawPointer?, Int32) -> Int32
private typealias Capture = @convention(c) (UnsafeMutableRawPointer?, Int32, Int32) -> Int32
private typealias PrepareQuit = @convention(c) () -> Void

// PlayerMain owns NSApplication.run(). Its callbacks can arrive on the Unity
// render thread, while all layout and AppKit work stays on the main thread.
final class DesktopBridge {
    static let shared = DesktopBridge()
    private let lock = NSLock()
    private var events: [Data] = []
    private var eventIndex = 0
    private var snapshot: Data?
    private var pixels: (Data, Int, Int, Int64, Int64)?
    private var scheduled = false
    private var needsAttach = true
    private var surface: WorkspaceSurface?
    private var windows: [String: AuxiliaryWindow] = [:]
    private var wantsFullScreen = false

    @MainActor func prepareQuit() {
        surface?.commitPendingEdits()
        for panel in windows.values { panel.surface.commitPendingEdits() }
        send(["type": "quitAfterEdits"])
    }

    @MainActor func capture(_ maximum: Int) -> Data? {
        guard maximum > 0, let surface, !surface.bounds.isEmpty,
              let bitmap = surface.bitmapImageRepForCachingDisplay(in: surface.bounds) else { return nil }
        surface.cacheDisplay(in: surface.bounds, to: bitmap)
        guard let source = bitmap.cgImage else { return nil }
        let scale = min(1, Double(maximum) / Double(max(source.width, source.height)))
        let width = max(1, Int((Double(source.width) * scale).rounded()))
        let height = max(1, Int((Double(source.height) * scale).rounded()))
        guard let canvas = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8,
                  bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(),
                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        canvas.interpolationQuality = .high
        canvas.draw(source, in: CGRect(x: 0, y: 0, width: CGFloat(width), height: CGFloat(height)))
        guard let image = canvas.makeImage() else { return nil }
        return NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:])
    }

    func send(_ packet: Packet) {
        guard let data = try? JSONSerialization.data(withJSONObject: packet) else { return }
        lock.lock()
        events.append(data)
        lock.unlock()
    }

    func poll(_ buffer: UnsafeMutableRawPointer?, _ capacity: Int32) -> Int32 {
        lock.lock()
        defer { lock.unlock() }
        if needsAttach { scheduleLocked() }
        guard eventIndex < events.count else { return 0 }
        let data = events[eventIndex]
        let length = Int32(data.count)
        guard let buffer, capacity >= length else { return -length }
        data.copyBytes(to: buffer.assumingMemoryBound(to: UInt8.self), count: data.count)
        eventIndex += 1
        if eventIndex == events.count {
            events.removeAll(keepingCapacity: true)
            eventIndex = 0
        }
        return length
    }

    func publish(_ data: Data) {
        lock.lock()
        snapshot = data
        scheduleLocked()
        lock.unlock()
    }

    func frame(_ data: Data, _ width: Int, _ height: Int, _ document: Int64, _ layout: Int64) {
        lock.lock()
        pixels = (data, width, height, document, layout)
        scheduleLocked()
        lock.unlock()
    }

    private func scheduleLocked() {
        if scheduled { return }
        scheduled = true
        DispatchQueue.main.async { self.present() }
    }

    @MainActor func attach() {
        guard surface == nil else { return }
        let windows = [NSApp?.mainWindow, NSApp?.keyWindow].compactMap { $0 } + (NSApp?.windows ?? [])
        guard let window = windows.first(where: {
            $0.isVisible && $0.canBecomeMain && !($0 is NSPanel) && $0.contentView != nil
        }), let content = window.contentView else { return }
        let workspace = WorkspaceSurface(frame: content.bounds)
        let host = NSHostingView(rootView: WorkspaceView(surface: workspace).frame(maxWidth: .infinity, maxHeight: .infinity))
        host.appearance = NSAppearance(named: .darkAqua)
        host.frame = content.bounds
        host.autoresizingMask = [.width, .height]
        content.addSubview(host, positioned: .above, relativeTo: nil)
        surface = workspace
        lock.lock()
        needsAttach = false
        lock.unlock()
        window.acceptsMouseMovedEvents = true
        window.makeFirstResponder(workspace)
        workspace.sendSize(content.bounds.size)
        // Retain Unity's window delegate. Closing the window and Command-Q still
        // reach Application.wantsToQuit, which owns unsaved-project confirmation.
    }

    @MainActor private func present() {
        attach()
        lock.lock()
        scheduled = false
        guard let surface else { lock.unlock(); return }
        let message = snapshot
        let image = pixels
        snapshot = nil
        pixels = nil
        lock.unlock()
        if let message,
           let json = try? JSONSerialization.jsonObject(with: message) as? Packet {
            if let error = json["error"] as? String {
                NSLog("AvaAvaDance: %@", error)
                surface.report(error)
            } else {
                surface.apply(json)
                updateWindows(json)
                if let full = json["fullscreen"] as? Bool, full != wantsFullScreen {
                    wantsFullScreen = full
                    if let window = surface.window, window.styleMask.contains(.fullScreen) != full {
                        window.toggleFullScreen(nil)
                    }
                }
            }
        }
        if let (bytes, width, height, document, layout) = image,
           document == surface.document, layout == surface.layoutEpoch,
           let provider = CGDataProvider(data: bytes as CFData),
           let frame = CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32,
                bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.noneSkipLast.rawValue)
                    .union(.byteOrder32Big), provider: provider, decode: nil,
                shouldInterpolate: true, intent: .defaultIntent) {
            surface.show(frame)
            for aux in windows.values { aux.surface.show(frame) }
        }
    }

    @MainActor private func updateWindows(_ snapshot: Packet) {
        guard let descriptions = snapshot["windows"] as? [Packet], let document = snapshot["document"] as? Int else { return }
        var live = Set<String>()
        for var entry in descriptions {
            guard let name = entry["name"] as? String, let id = entry["id"] as? Int else { continue }
            live.insert(name)
            entry["document"] = document
            entry["layout"] = snapshot["layout"]
            if windows[name]?.id != id {
                windows.removeValue(forKey: name)?.dismiss()
                windows[name] = AuxiliaryWindow(id: id, name: name, description: entry)
            }
            windows[name]?.update(entry)
        }
        for name in windows.keys.filter({ !live.contains($0) }) { windows.removeValue(forKey: name)?.dismiss() }
    }
}

struct WorkspaceView: NSViewRepresentable {
    let surface: WorkspaceSurface
    func makeNSView(context: Context) -> WorkspaceSurface { surface }
    func updateNSView(_ view: WorkspaceSurface, context: Context) {}
}

@MainActor private func fileChoice(_ request: Packet) -> URL? {
    let save = request["save"] as? Bool == true
    let panel: NSSavePanel
    if save {
        panel = NSSavePanel()
    } else {
        let open = NSOpenPanel()
        open.canChooseDirectories = false
        open.allowsMultipleSelection = false
        panel = open
    }
    let extensions = request["extensions"] as? [String] ?? []
    let types = extensions.compactMap { UTType(filenameExtension: $0) }
    if !types.isEmpty { panel.allowedContentTypes = types }
    if save, let ext = extensions.first { panel.nameFieldStringValue = "Untitled." + ext }
    panel.title = request["title"] as? String ?? (save ? "保存" : "ファイルを選択")
    if let dir = request["directory"] as? String, !dir.isEmpty {
        panel.directoryURL = URL(fileURLWithPath: dir, isDirectory: true)
    }
    return panel.runModal() == .OK ? panel.url : nil
}

private let poll: Poll = { buffer, capacity in DesktopBridge.shared.poll(buffer, capacity) }
private let publish: Publish = { buffer, count in
    guard let buffer, count > 0 else { return }
    DesktopBridge.shared.publish(Data(bytes: buffer, count: Int(count)))
}
private let frame: Frame = { buffer, width, height, document, layout in
    guard let buffer, width > 0, height > 0, width <= 16384, height <= 16384 else { return }
    DesktopBridge.shared.frame(Data(bytes: buffer, count: Int(width) * Int(height) * 4), Int(width), Int(height), document, layout)
}
private let pick: Pick = { json, buffer, capacity in
    guard let json, let buffer, capacity > 0, let text = String(validatingUTF8: json),
          let request = try? JSONSerialization.jsonObject(with: Data(text.utf8)) as? Packet else { return -1 }
    let url = Thread.isMainThread ? MainActor.assumeIsolated { fileChoice(request) } : DispatchQueue.main.sync { fileChoice(request) }
    guard let url else { return 0 }
    let data = Data(url.path.utf8)
    guard data.count <= Int(capacity) else { return -1 }
    data.copyBytes(to: buffer.assumingMemoryBound(to: UInt8.self), count: data.count)
    return Int32(data.count)
}
private let capture: Capture = { destination, capacity, maximum in
    guard let destination, capacity > 0, maximum > 0 else { return -1 }
    let png = Thread.isMainThread
        ? MainActor.assumeIsolated { DesktopBridge.shared.capture(Int(maximum)) }
        : DispatchQueue.main.sync { DesktopBridge.shared.capture(Int(maximum)) }
    guard let png, png.count <= Int(capacity) else { return -1 }
    png.copyBytes(to: destination.assumingMemoryBound(to: UInt8.self), count: png.count)
    return Int32(png.count)
}

@_cdecl("AvaInstallDesktopHost")
public func installDesktopHost() {
    // Command-line rendering and verification use the Player without a workspace.
    if CommandLine.arguments.contains(where: { $0.lowercased() == "-batchmode" }) { return }
    func exportPointer<T>(_ name: String, _ function: T) {
        let address = unsafeBitCast(function, to: UInt.self)
        setenv(name, String(address, radix: 16), 1)
    }
    exportPointer("AVA_DESKTOP_POLL", poll)
    exportPointer("AVA_DESKTOP_PUBLISH", publish)
    exportPointer("AVA_DESKTOP_FRAME", frame)
    exportPointer("AVA_DESKTOP_PICK", pick)
    exportPointer("AVA_DESKTOP_CAPTURE", capture)
    let prepareQuit: PrepareQuit = {
        DispatchQueue.main.async { DesktopBridge.shared.prepareQuit() }
    }
    exportPointer("AVA_DESKTOP_PREPARE_QUIT", prepareQuit)
    setenv("AVA_DESKTOP_PID", String(getpid()), 1)
}
