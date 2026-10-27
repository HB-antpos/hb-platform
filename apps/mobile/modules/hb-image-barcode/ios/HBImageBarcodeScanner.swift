import Foundation
import ImageIO
import Vision

/// 一条识别结果；type 沿用 expo-camera 的条码类型命名，JS 侧可与相机扫码走同一套归一化。
struct HBImageBarcodeHit: Equatable {
  let type: String
  let data: String
}

enum HBImageBarcodeScannerError: Error {
  case invalidUri
  case unreadableImage
}

/// 只依赖 Vision / ImageIO，不依赖 ExpoModulesCore，方便单独编译和验证。
enum HBImageBarcodeScanner {
  /// Vision 条码类型 -> expo-camera 类型名。
  /// 注意：Vision 把 UPC-A 当成「前面补 0 的 EAN-13」返回 13 位，和 JS 侧 restoreCameraBarcodeData 的 12 位补 0 规则兼容。
  static func expoType(for symbology: VNBarcodeSymbology) -> String {
    switch symbology {
    case .ean13: return "ean13"
    case .ean8: return "ean8"
    case .upce: return "upc_e"
    case .code128: return "code128"
    case .code39, .code39Checksum, .code39FullASCII, .code39FullASCIIChecksum: return "code39"
    case .code93, .code93i: return "code93"
    case .itf14, .i2of5, .i2of5Checksum: return "itf14"
    case .qr: return "qr"
    case .pdf417: return "pdf417"
    case .dataMatrix: return "datamatrix"
    case .aztec: return "aztec"
    default:
      if #available(iOS 15.0, *), symbology == .codabar {
        return "codabar"
      }
      return symbology.rawValue
    }
  }

  static func fileUrl(from uri: String) throws -> URL {
    // 图片选择器返回 file:// URI；也兼容直接传本地路径。
    if uri.hasPrefix("/") {
      return URL(fileURLWithPath: uri)
    }
    guard let url = URL(string: uri), url.isFileURL else {
      throw HBImageBarcodeScannerError.invalidUri
    }
    return url
  }

  /// 读取 EXIF 方向，避免竖拍照片按横向喂给 Vision 后识别率下降。
  static func orientation(of url: URL) -> CGImagePropertyOrientation {
    guard
      let source = CGImageSourceCreateWithURL(url as CFURL, nil),
      let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
      let raw = properties[kCGImagePropertyOrientation] as? UInt32,
      let orientation = CGImagePropertyOrientation(rawValue: raw)
    else {
      return .up
    }
    return orientation
  }

  static func scan(uri: String) throws -> [HBImageBarcodeHit] {
    let url = try fileUrl(from: uri)
    guard FileManager.default.isReadableFile(atPath: url.path) else {
      throw HBImageBarcodeScannerError.unreadableImage
    }

    let request = VNDetectBarcodesRequest()
    let handler = VNImageRequestHandler(url: url, orientation: orientation(of: url), options: [:])
    do {
      try handler.perform([request])
    } catch {
      throw HBImageBarcodeScannerError.unreadableImage
    }

    var seen = Set<String>()
    var hits: [HBImageBarcodeHit] = []
    for observation in request.results ?? [] {
      // 二进制 QR 等没有文本载荷的结果对业务无意义，直接忽略。
      guard let payload = observation.payloadStringValue, !payload.isEmpty else { continue }
      let hit = HBImageBarcodeHit(type: expoType(for: observation.symbology), data: payload)
      if seen.insert("\(hit.type)|\(hit.data)").inserted {
        hits.append(hit)
      }
    }
    return hits
  }
}
