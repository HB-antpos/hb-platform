import CoreImage
import CoreImage.CIFilterBuiltins
import Foundation
import UIKit

final class HBAttendanceQrCodec {
  private let keychain: HBAttendanceSecurityKeychain
  private let ciContext = CIContext(options: [.useSoftwareRenderer: false])

  init(keychain: HBAttendanceSecurityKeychain) {
    self.keychain = keychain
  }

  func issue(_ input: HBAttendanceQrInput) throws -> String {
    var key = try keychain.readKey(handle: input.keyHandle)
    defer {
      key.resetBytes(in: 0..<key.count)
    }

    let token: String
    do {
      token = try HBAttendanceTokenCodec.encrypt(input, key: key)
    } catch {
      throw HBAttendanceSecurityException(
        .tokenGenerationFailed,
        "无法生成考勤二维码内容。"
      )
    }

    do {
      return try renderQrDataUri(token)
    } catch let exception as HBAttendanceSecurityException {
      throw exception
    } catch {
      throw HBAttendanceSecurityException(
        .qrRenderFailed,
        "无法生成考勤二维码图像。"
      )
    }
  }

  private func renderQrDataUri(_ token: String) throws -> String {
    let filter = CIFilter.qrCodeGenerator()
    filter.message = Data(token.utf8)
    // Q 级纠错（约 25%）：收银页的码正中压了一个倒计时圆标，遮住约 4% 面积，
    // M 级（约 15%）余量偏紧；码会从 57 × 57 模块变成 65 × 65，显示尺寸足够。
    filter.correctionLevel = "Q"
    guard let output = filter.outputImage else {
      throw HBAttendanceSecurityException(
        .qrRenderFailed,
        "考勤二维码滤镜没有输出。"
      )
    }
    let scaled = output.transformed(
      by: CGAffineTransform(scaleX: 10, y: 10)
    )
    guard
      let cgImage = ciContext.createCGImage(
        scaled,
        from: scaled.extent
      ),
      let png = UIImage(cgImage: cgImage).pngData()
    else {
      throw HBAttendanceSecurityException(
        .qrRenderFailed,
        "考勤二维码图像编码失败。"
      )
    }
    return "data:image/png;base64,\(png.base64EncodedString())"
  }

}
