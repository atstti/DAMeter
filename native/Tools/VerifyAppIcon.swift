import AppKit
import Foundation
import Darwin
// Usage: xcrun swift VerifyAppIcon.swift DAMeter.app [decoded.iconset]
// This checks bundle resources and native icon resolution only. It does not
// register the app, change its icon, or touch privacy/permission databases.
private struct IconCheckError: Error, CustomStringConvertible {
    let description: String
}
private func fail(_ message: String) throws -> Never {
    throw IconCheckError(description: message)
}
private struct IconColors {
    // Green, yellow, red, dark background, and other opaque colors.
    var counts = [Int](repeating: 0, count: 5)
    var total: Int { counts.reduce(0, +) }
    var description: String {
        "green=\(counts[0]), yellow=\(counts[1]), red=\(counts[2]), dark=\(counts[3]), opaque=\(total)"
    }
    func requireMeterColors(_ label: String) throws {
        guard total >= 1_200, counts[0] >= 60, counts[1] >= 40, counts[2] >= 15 else {
            try fail("\(label) does not show DAMeter's green/yellow/red meter icon (\(description)).")
        }
    }
    func difference(from other: IconColors) -> Double {
        guard total > 0, other.total > 0 else { return 1 }
        // Coarse histograms tolerate antialiasing, shadows, and icon positioning.
        return zip(counts, other.counts).reduce(0.0) { result, pair in
            result + abs(Double(pair.0) / Double(total) - Double(pair.1) / Double(other.total))
        } / 2
    }
}
private func renderColors(_ image: NSImage, label: String) throws -> IconColors {
    guard image.isValid else { try fail("\(label) is not a valid NSImage.") }
    guard let pixels = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: 64, pixelsHigh: 64,
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0
    ), let context = NSGraphicsContext(bitmapImageRep: pixels) else {
        try fail("Cannot create the bitmap context for \(label).")
    }
    pixels.size = NSSize(width: 64, height: 64)
    NSGraphicsContext.saveGraphicsState()
    defer { NSGraphicsContext.restoreGraphicsState() }
    NSGraphicsContext.current = context
    context.imageInterpolation = .high
    let rectangle = NSRect(x: 0, y: 0, width: 64, height: 64)
    context.cgContext.clear(rectangle)
    image.draw(in: rectangle, from: .zero, operation: .sourceOver, fraction: 1)
    var colors = IconColors()
    for y in 0..<64 {
        for x in 0..<64 {
            guard let color = pixels.colorAt(x: x, y: y)?.usingColorSpace(.sRGB) else {
                try fail("Cannot read the rendered pixels for \(label).")
            }
            if color.alphaComponent < 0.6 { continue }
            let red = color.redComponent
            let green = color.greenComponent
            let blue = color.blueComponent
            let category: Int
            if green > 0.5 && green > red * 1.4 && green > blue * 1.1 {
                category = 0
            } else if red > 0.65 && green > 0.5 && blue < 0.5 && abs(red - green) < 0.4 {
                category = 1
            } else if red > 0.7 && green < 0.6 && blue < 0.65 && red > green * 1.4 {
                category = 2
            } else if max(red, max(green, blue)) < 0.35 {
                category = 3
            } else {
                category = 4
            }
            colors.counts[category] += 1
        }
    }
    return colors
}
private func verifyDecodedIconset(_ directory: URL) throws -> String {
    let files = try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)
        .filter { $0.pathExtension.lowercased() == "png" }
    guard !files.isEmpty else { try fail("The decoded iconset contains no PNG representations: \(directory.path).") }
    let namePattern = try NSRegularExpression(pattern: "^icon_([0-9]+)x([0-9]+)(@2x)?\\.png$")
    var sizes = Set<Int>()
    for file in files {
        let name = file.lastPathComponent as NSString
        guard let match = namePattern.firstMatch(in: name as String, range: NSRange(location: 0, length: name.length)),
              let width = Int(name.substring(with: match.range(at: 1))),
              let height = Int(name.substring(with: match.range(at: 2))), width == height, width > 0 else {
            try fail("Unexpected decoded icon representation name: \(name).")
        }
        let scale = match.range(at: 3).location == NSNotFound ? 1 : 2
        let expected = width * scale
        guard let bitmap = NSBitmapImageRep(data: try Data(contentsOf: file)) else {
            try fail("Cannot decode icon representation \(file.lastPathComponent).")
        }
        guard bitmap.pixelsWide == expected, bitmap.pixelsHigh == expected else {
            try fail("\(file.lastPathComponent) has \(bitmap.pixelsWide)x\(bitmap.pixelsHigh) pixels; expected \(expected)x\(expected).")
        }
        sizes.insert(expected)
    }
    let required: Set<Int> = [16, 32, 64, 128, 256, 512, 1024]
    let missing = required.subtracting(sizes).sorted()
    guard missing.isEmpty else { try fail("The ICNS is missing pixel sizes: \(missing).") }
    // iconutil may coalesce entries: require pixel sizes, not ten distinct files.
    return sizes.sorted().map(String.init).joined(separator: ",")
}
private func verify() throws {
    guard (2...3).contains(CommandLine.arguments.count) else {
        try fail("Usage: VerifyAppIcon.swift DAMeter.app [decoded.iconset]")
    }
    let app = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true).standardizedFileURL
    guard let bundle = Bundle(url: app),
          bundle.object(forInfoDictionaryKey: "CFBundlePackageType") as? String == "APPL",
          bundle.bundleIdentifier == "org.atstti.DAMeter" else {
        try fail("The target is not the DAMeter application bundle: \(app.path).")
    }
    guard let iconName = bundle.object(forInfoDictionaryKey: "CFBundleIconFile") as? String,
          !iconName.isEmpty, let resources = bundle.resourceURL else {
        try fail("Info.plist does not name a CFBundleIconFile resource.")
    }
    let filename = iconName.lowercased().hasSuffix(".icns") ? iconName : iconName + ".icns"
    let iconURL = resources.appendingPathComponent(filename).standardizedFileURL
    guard iconURL.path.hasPrefix(resources.standardizedFileURL.path + "/"),
          FileManager.default.fileExists(atPath: iconURL.path),
          let direct = NSImage(contentsOf: iconURL) else {
        try fail("CFBundleIconFile does not resolve to a readable ICNS resource: \(iconURL.path).")
    }
    let directColors = try renderColors(direct, label: "The ICNS resource")
    try directColors.requireMeterColors("The ICNS resource")
    let native = NSWorkspace.shared.icon(forFile: app.path)
    let nativeColors = try renderColors(native, label: "The NSWorkspace bundle icon")
    try nativeColors.requireMeterColors("The NSWorkspace bundle icon")
    let difference = nativeColors.difference(from: directColors)
    guard difference <= 0.2 else {
        try fail("NSWorkspace resolves a different icon (histogram difference=\(difference); resource: \(directColors.description); native: \(nativeColors.description)).")
    }
    var sizes = "not checked (no decoded iconset supplied)"
    if CommandLine.arguments.count == 3 {
        sizes = try verifyDecodedIconset(URL(fileURLWithPath: CommandLine.arguments[2], isDirectory: true))
    }
    print("Verified bundle icon \(filename): pixel sizes=\(sizes); native \(nativeColors.description); histogram difference=\(String(format: "%.4f", difference)).")
}
do {
    try verify()
} catch {
    FileHandle.standardError.write(Data("App icon verification failed: \(error)\n".utf8))
    exit(1)
}
