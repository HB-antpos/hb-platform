import ExpoModulesCore

/// 本地图片条码识别：JS 传入相册图片的 file:// URI，返回图片里所有识别到的条码。
/// 图片只在本机用 Vision 解析，不上传、不落盘、不写日志。
public class HBImageBarcodeModule: Module {
  public func definition() -> ModuleDefinition {
    Name("HBImageBarcode")

    AsyncFunction("scanImage") { (uri: String) -> [[String: String]] in
      do {
        return try HBImageBarcodeScanner.scan(uri: uri).map { ["type": $0.type, "data": $0.data] }
      } catch HBImageBarcodeScannerError.invalidUri {
        throw Exception(name: "ERR_HB_IMAGE_BARCODE_URI", description: "Image URI must be a local file URI")
      } catch {
        throw Exception(name: "ERR_HB_IMAGE_BARCODE_READ", description: "Unable to read the image")
      }
    }
    .runOnQueue(DispatchQueue.global(qos: .userInitiated))
  }
}
