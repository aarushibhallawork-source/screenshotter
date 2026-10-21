// Draws the 1024×1024 app icon: a Y2K pink tile with a tilted polaroid and a heart.
// Usage: swift scripts/make-icon.swift out.png
import Cocoa

let S: CGFloat = 1024
let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(S), pixelsHigh: Int(S), bitsPerSample: 8,
                           samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                           bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
let ctx = NSGraphicsContext.current!.cgContext

func hex(_ v: UInt32) -> NSColor {
    NSColor(srgbRed: CGFloat((v >> 16) & 255) / 255, green: CGFloat((v >> 8) & 255) / 255, blue: CGFloat(v & 255) / 255, alpha: 1)
}
let ink = hex(0x25202B)

func heart(_ c: CGPoint, _ s: CGFloat) -> NSBezierPath {
    let p = NSBezierPath()
    p.move(to: CGPoint(x: c.x, y: c.y - s * 0.42))
    p.curve(to: CGPoint(x: c.x - s * 0.5, y: c.y + s * 0.12),
            controlPoint1: CGPoint(x: c.x - s * 0.18, y: c.y - s * 0.2), controlPoint2: CGPoint(x: c.x - s * 0.5, y: c.y - s * 0.12))
    p.curve(to: CGPoint(x: c.x, y: c.y + s * 0.28),
            controlPoint1: CGPoint(x: c.x - s * 0.5, y: c.y + s * 0.46), controlPoint2: CGPoint(x: c.x - s * 0.08, y: c.y + s * 0.5))
    p.curve(to: CGPoint(x: c.x + s * 0.5, y: c.y + s * 0.12),
            controlPoint1: CGPoint(x: c.x + s * 0.08, y: c.y + s * 0.5), controlPoint2: CGPoint(x: c.x + s * 0.5, y: c.y + s * 0.46))
    p.curve(to: CGPoint(x: c.x, y: c.y - s * 0.42),
            controlPoint1: CGPoint(x: c.x + s * 0.5, y: c.y - s * 0.12), controlPoint2: CGPoint(x: c.x + s * 0.18, y: c.y - s * 0.2))
    p.close()
    return p
}

func sparkle(_ c: CGPoint, _ r: CGFloat) -> NSBezierPath {
    let p = NSBezierPath()
    for i in 0..<8 {
        let a = CGFloat(i) * .pi / 4
        let rr = i % 2 == 0 ? r : r * 0.28
        let pt = CGPoint(x: c.x + cos(a) * rr, y: c.y + sin(a) * rr)
        i == 0 ? p.move(to: pt) : p.line(to: pt)
    }
    p.close()
    return p
}

// Tile (macOS icon grid: 824pt squircle-ish tile, 100pt margin) with a hard shadow.
let tile = NSRect(x: 100, y: 100, width: 824, height: 824)
let shadow = NSBezierPath(roundedRect: tile.offsetBy(dx: 18, dy: -22), xRadius: 190, yRadius: 190)
ink.setFill(); shadow.fill()
let tilePath = NSBezierPath(roundedRect: tile, xRadius: 190, yRadius: 190)
NSGradient(colors: [hex(0xFFE0F1), hex(0xFFA3D4), hex(0xFF5FB4)])!.draw(in: tilePath, angle: -90)
ink.setStroke(); tilePath.lineWidth = 20; tilePath.stroke()

// Dot grid on the tile.
ctx.saveGState()
tilePath.addClip()
NSColor.white.withAlphaComponent(0.35).setFill()
for x in stride(from: 130, to: 924, by: 46) { for y in stride(from: 130, to: 924, by: 46) {
    NSBezierPath(ovalIn: NSRect(x: x, y: y, width: 7, height: 7)).fill()
}}
ctx.restoreGState()

// Polaroid, tilted.
ctx.saveGState()
ctx.translateBy(x: 512, y: 500)
ctx.rotate(by: -0.14)
let card = NSRect(x: -215, y: -250, width: 430, height: 500)
ink.setFill(); NSBezierPath(rect: card.offsetBy(dx: 16, dy: -18)).fill()
let cardPath = NSBezierPath(rect: card)
NSColor.white.setFill(); cardPath.fill()
ink.setStroke(); cardPath.lineWidth = 16; cardPath.stroke()
let photo = NSRect(x: -175, y: -110, width: 350, height: 320)
let photoPath = NSBezierPath(rect: photo)
NSGradient(colors: [hex(0xC9B6FF), hex(0x9FC2FF)])!.draw(in: photoPath, angle: -90)
ink.setStroke(); photoPath.lineWidth = 12; photoPath.stroke()
let h = heart(CGPoint(x: 0, y: 40), 250)
hex(0xFF5FB4).setFill(); h.fill()
ink.setStroke(); h.lineWidth = 14; h.stroke()
NSColor.white.withAlphaComponent(0.85).setFill()
NSBezierPath(ovalIn: NSRect(x: -78, y: 70, width: 40, height: 26)).fill()
// Caption line.
ink.withAlphaComponent(0.25).setFill()
NSBezierPath(roundedRect: NSRect(x: -150, y: -195, width: 200, height: 22), xRadius: 11, yRadius: 11).fill()
ctx.restoreGState()

// Sparkles.
for (c, r) in [(CGPoint(x: 790, y: 800), CGFloat(70)), (CGPoint(x: 230, y: 260), CGFloat(46))] {
    let s = sparkle(c, r)
    NSColor.white.setFill(); s.fill()
    ink.setStroke(); s.lineWidth = 10; s.stroke()
}

NSGraphicsContext.current = nil
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: CommandLine.arguments[1]))
