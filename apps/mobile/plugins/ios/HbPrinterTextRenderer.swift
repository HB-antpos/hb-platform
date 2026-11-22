import CoreGraphics
import CoreText
import Foundation

/// 标签打印的通用文字渲染器：只负责「按字体、字号把一段文字量出尺寸 / 画成 1bit 位图」，
/// 坐标、字号、文案等排版全部由 JS 决定（可走 OTA）。接口约定见 docs/plans/2026-11-23-mobile-label-js-layout.md，
/// 必须与 Android 的 HbPrinterTextRenderer.kt 保持同一口径。
final class HbPrinterTextRenderer {
  struct RenderError: Error {
    let code: String
    let message: String
  }

  struct TextMetrics {
    let advance: CGFloat
    let ascent: CGFloat
    let descent: CGFloat
    let inkLeft: CGFloat
    let inkTop: CGFloat
    let inkRight: CGFloat
    let inkBottom: CGFloat
  }

  struct TextBitmap {
    let width: Int
    let height: Int
    let originX: Int
    let originY: Int
    let hasInk: Bool
    let inkLeft: Int
    let inkTop: Int
    let inkRight: Int
    let inkBottom: Int
    let bitsBase64: String
  }

  static let renderVersion = 1
  static let maxBatch = 64
  static let maxBitmapWidth = 2048
  static let maxBitmapHeight = 1024
  static let maxTextLength = 512
  static let maxFontSize: CGFloat = 600
  static let maxPadding = 64

  /// 内置字体键 → App 包里的字体文件名（不含扩展名）；与 Android 端、JS 端的键保持一致，顺序也一致。
  static let bundledFonts: [(key: String, file: String)] = [
    ("roboto-regular", "HbPrinter-Roboto-Regular"),
    ("roboto-light", "HbPrinter-Roboto-Light"),
    ("roboto-medium", "HbPrinter-Roboto-Medium"),
    ("roboto-bold", "HbPrinter-Roboto-Bold"),
    ("roboto-black", "HbPrinter-Roboto-Black"),
  ]

  private let lock = NSLock()
  private var descriptors: [String: CTFontDescriptor] = [:]

  /// 按字体键取字体：内置键读 App 包资源，file:// 读本地字体文件（以后 OTA 下发新字体用）；
  /// 直接从文件建字体描述符，不注册到系统，也不需要 Info.plist 的 UIAppFonts。
  func font(_ key: String, size: CGFloat) throws -> CTFont {
    guard size.isFinite, size > 0, size <= Self.maxFontSize else {
      throw RenderError(code: "PRINTER_RENDER_INVALID", message: "Font size is out of range: \(size)")
    }
    return CTFontCreateWithFontDescriptor(try descriptor(key), size, nil)
  }

  private func descriptor(_ key: String) throws -> CTFontDescriptor {
    lock.lock()
    defer { lock.unlock() }
    if let cached = descriptors[key] {
      return cached
    }

    var url: URL?
    if let file = Self.bundledFonts.first(where: { $0.key == key })?.file {
      url = Bundle.main.url(forResource: file, withExtension: "ttf")
    } else if key.hasPrefix("file://") {
      // Expo 文件 URI 可能带百分号转义；解析失败时按原样路径兜底。
      url = URL(string: key) ?? URL(fileURLWithPath: String(key.dropFirst("file://".count)))
    }
    guard let fontURL = url,
          FileManager.default.fileExists(atPath: fontURL.path),
          let created = CTFontManagerCreateFontDescriptorsFromURL(fontURL as CFURL) as? [CTFontDescriptor],
          let first = created.first else {
      throw RenderError(code: "PRINTER_FONT_NOT_FOUND", message: "Printer font is not available: \(key)")
    }
    descriptors[key] = first
    return first
  }

  func measure(text: String, font key: String, size: CGFloat) throws -> TextMetrics {
    try checkText(text)
    let font = try font(key, size: size)
    let line = makeLine(text, font: font)
    let ink = inkBounds(line, text: text)
    return TextMetrics(
      advance: CGFloat(CTLineGetTypographicBounds(line, nil, nil, nil)),
      // 行高口径取主字体的 ascent / descent（与 Android Paint.fontMetrics 一致），不受中文回退字体影响。
      ascent: CTFontGetAscent(font),
      descent: CTFontGetDescent(font),
      inkLeft: ink.left,
      inkTop: ink.top,
      inkRight: ink.right,
      inkBottom: ink.bottom
    )
  }

  func render(text: String, font key: String, size: CGFloat, inverse: Bool, padding: Int) throws -> TextBitmap {
    try checkText(text)
    guard padding >= 0, padding <= Self.maxPadding else {
      throw RenderError(code: "PRINTER_RENDER_INVALID", message: "Padding is out of range: \(padding)")
    }
    let font = try font(key, size: size)
    let line = makeLine(text, font: font)
    let ink = inkBounds(line, text: text)
    let advance = CGFloat(CTLineGetTypographicBounds(line, nil, nil, nil))
    let ascent = CTFontGetAscent(font)
    let descent = CTFontGetDescent(font)

    // 画布 = 行框（ascent..descent、0..advance）与墨迹框的并集，四周再加 padding；基线起点取整到像素。
    let left = Int(floor(min(0, ink.left)))
    let right = Int(ceil(max(advance, ink.right)))
    let top = Int(floor(min(-ascent, ink.top)))
    let bottom = Int(ceil(max(descent, ink.bottom)))
    let width = max(1, right - left + padding * 2)
    let height = max(1, bottom - top + padding * 2)
    guard width <= Self.maxBitmapWidth, height <= Self.maxBitmapHeight else {
      throw RenderError(code: "PRINTER_RENDER_TOO_LARGE", message: "Rendered text is too large: \(width)x\(height)")
    }
    let originX = padding - left
    let originY = padding - top

    guard let context = CGContext(
      data: nil,
      width: width,
      height: height,
      bitsPerComponent: 8,
      bytesPerRow: width,
      space: CGColorSpaceCreateDeviceGray(),
      bitmapInfo: CGImageAlphaInfo.none.rawValue
    ) else {
      throw RenderError(code: "PRINTER_RENDER_ERROR", message: "Unable to create bitmap context.")
    }
    context.setFillColor(gray: inverse ? 0 : 1, alpha: 1)
    context.fill(CGRect(x: 0, y: 0, width: width, height: height))
    context.setShouldAntialias(true)
    context.setAllowsAntialiasing(true)
    context.setShouldSmoothFonts(false)
    context.setFillColor(gray: inverse ? 1 : 0, alpha: 1)
    // CoreGraphics 原点在左下：基线 y 要从底部换算。
    context.textPosition = CGPoint(x: CGFloat(originX), y: CGFloat(height - originY))
    CTLineDraw(line, context)

    guard let data = context.data else {
      throw RenderError(code: "PRINTER_RENDER_ERROR", message: "Bitmap context has no data.")
    }
    // 位图上下文内存第 0 行就是图像最上面一行。
    let gray = data.bindMemory(to: UInt8.self, capacity: width * height)
    let rowBytes = (width + 7) / 8
    var bits = [UInt8](repeating: 0, count: rowBytes * height)
    var inkMinX = Int.max
    var inkMinY = Int.max
    var inkMaxX = -1
    var inkMaxY = -1
    for y in 0..<height {
      for x in 0..<width {
        // 阈值与现有标签位图一致：灰度 < 200 记为黑。
        let black = gray[y * width + x] < 200
        if black {
          bits[y * rowBytes + (x >> 3)] |= UInt8(0x80 >> (x & 7))
        }
        // 墨迹 = 与底色不同的像素（正常是黑字，反白是白字），两端同一口径。
        if black != inverse {
          inkMinX = min(inkMinX, x)
          inkMaxX = max(inkMaxX, x)
          inkMinY = min(inkMinY, y)
          inkMaxY = max(inkMaxY, y)
        }
      }
    }
    let hasInk = inkMaxX >= 0
    return TextBitmap(
      width: width,
      height: height,
      originX: originX,
      originY: originY,
      hasInk: hasInk,
      inkLeft: hasInk ? inkMinX : 0,
      inkTop: hasInk ? inkMinY : 0,
      inkRight: hasInk ? inkMaxX + 1 : 0,
      inkBottom: hasInk ? inkMaxY + 1 : 0,
      bitsBase64: Data(bits).base64EncodedString()
    )
  }

  private func makeLine(_ text: String, font: CTFont) -> CTLine {
    let attributes: [NSAttributedString.Key: Any] = [
      NSAttributedString.Key(kCTFontAttributeName as String): font,
      // 字色跟随上下文填充色，正常 / 反白共用同一条 CTLine。
      NSAttributedString.Key(kCTForegroundColorFromContextAttributeName as String): true,
    ]
    return CTLineCreateWithAttributedString(NSAttributedString(string: text, attributes: attributes))
  }

  /// 字形轮廓的墨迹框，换成「基线为原点、y 向下」；空白文字返回全 0（与 Android 一致）。
  private func inkBounds(_ line: CTLine, text: String) -> (left: CGFloat, top: CGFloat, right: CGFloat, bottom: CGFloat) {
    if text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
      return (0, 0, 0, 0)
    }
    let bounds = CTLineGetBoundsWithOptions(line, .useGlyphPathBounds)
    if bounds.isNull || bounds.isEmpty {
      return (0, 0, 0, 0)
    }
    return (bounds.minX, -bounds.maxY, bounds.maxX, -bounds.minY)
  }

  private func checkText(_ text: String) throws {
    // 与 Android String.length 同口径：按 UTF-16 单元计数。
    if text.utf16.count > Self.maxTextLength {
      throw RenderError(code: "PRINTER_RENDER_INVALID", message: "Text is too long: \(text.utf16.count)")
    }
  }
}
