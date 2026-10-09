import ExpoModulesCore
import Foundation

private let maximumSafeJavaScriptInteger = 9_007_199_254_740_991

// 客显购物车一屏可见行数上限（与 WPF 客显 72 行高 / 768 设计高对齐）。
// models 校验 visibleItemStart 与 view controller 的窗口切片共用这一个常量，
// 并且必须与 TS 契约 CUSTOMER_DISPLAY_VISIBLE_ITEM_LIMIT 保持一致。
let hbExternalDisplayVisibleItemLimit = 6

// 本地文件 URI 校验：广告与商品缩略图共用。
// 只接受 file URL（host 为空或 localhost）且长度不超过 2048，客显层永不联网取资源。
private func validatedLocalFileURL(
  _ uri: String,
  field: String
) throws -> URL {
  guard
    uri.count <= 2_048,
    let url = URL(string: uri),
    url.isFileURL,
    !url.path.isEmpty,
    url.host == nil || url.host == "" || url.host == "localhost"
  else {
    throw HBExternalDisplayValidationError.invalid(field)
  }
  return url
}

enum HBExternalDisplayMode: String {
  case idle
  case cart
  case payment
  case change
  case success
}

enum HBExternalDisplayAdvertKind: String {
  case image
  case video
}

struct HBExternalDisplayMoney {
  let cents: Int

  var dictionary: [String: Any] {
    [
      "currency": "AUD",
      "cents": cents,
    ]
  }
}

struct HBExternalDisplayItem {
  let name: String
  let quantity: String
  let unitPrice: HBExternalDisplayMoney?
  let amount: HBExternalDisplayMoney
  // 以下为对齐 WPF 客显追加的可选字段，缺省时按无该信息渲染。
  let itemNumber: String?
  let lookupCode: String?
  let grossAmount: HBExternalDisplayMoney?
  let discountRate: String?
  let imageUri: String?
  // 已通过校验的本地缩略图 file URL，仅供兜底层 UIImage(contentsOfFile:) 读取。
  let imageUrl: URL?

  // React 层只靠 onSnapshotChanged 拿快照，这里漏字段就会丢字段。
  var dictionary: [String: Any] {
    var payload: [String: Any] = [
      "name": name,
      "quantity": quantity,
      "amount": amount.dictionary,
    ]
    if let unitPrice {
      payload["unitPrice"] = unitPrice.dictionary
    }
    if let itemNumber {
      payload["itemNumber"] = itemNumber
    }
    if let lookupCode {
      payload["lookupCode"] = lookupCode
    }
    if let grossAmount {
      payload["grossAmount"] = grossAmount.dictionary
    }
    if let discountRate {
      payload["discountRate"] = discountRate
    }
    if let imageUri {
      payload["imageUri"] = imageUri
    }
    return payload
  }
}

struct HBExternalDisplaySummary {
  let itemQuantity: String
  let skuCount: Int
  let subtotal: HBExternalDisplayMoney

  var dictionary: [String: Any] {
    [
      "itemQuantity": itemQuantity,
      "skuCount": skuCount,
      "subtotal": subtotal.dictionary,
    ]
  }
}

struct HBExternalDisplayAdvert {
  let kind: HBExternalDisplayAdvertKind
  let localUri: String
  let url: URL

  var dictionary: [String: Any] {
    [
      "kind": kind.rawValue,
      "localUri": localUri,
    ]
  }
}

struct HBExternalDisplaySnapshot {
  let revision: Int
  let mode: HBExternalDisplayMode
  let items: [HBExternalDisplayItem]
  let visibleItemStart: Int?
  let summary: HBExternalDisplaySummary?
  let gst: HBExternalDisplayMoney
  let discount: HBExternalDisplayMoney
  let total: HBExternalDisplayMoney
  let change: HBExternalDisplayMoney
  let advert: HBExternalDisplayAdvert?

  var dictionary: [String: Any] {
    var payload: [String: Any] = [
      "revision": revision,
      "mode": mode.rawValue,
      "items": items.map(\.dictionary),
      "gst": gst.dictionary,
      "discount": discount.dictionary,
      "total": total.dictionary,
      "change": change.dictionary,
      "advert": advert?.dictionary ?? NSNull(),
    ]
    if let summary {
      payload["summary"] = summary.dictionary
    }
    if let visibleItemStart {
      payload["visibleItemStart"] = visibleItemStart
    }
    return payload
  }
}

struct HBExternalDisplayMoneyRecord: Record {
  @Field
  var currency = ""

  @Field
  var cents = 0

  func validated(field: String) throws -> HBExternalDisplayMoney {
    guard currency == "AUD" else {
      throw HBExternalDisplayValidationError.invalid("\(field).currency")
    }
    guard
      cents >= -maximumSafeJavaScriptInteger,
      cents <= maximumSafeJavaScriptInteger
    else {
      throw HBExternalDisplayValidationError.invalid("\(field).cents")
    }
    return HBExternalDisplayMoney(cents: cents)
  }
}

struct HBExternalDisplayItemRecord: Record {
  @Field
  var name = ""

  @Field
  var quantity = ""

  @Field
  var unitPrice: HBExternalDisplayMoneyRecord?

  @Field
  var amount = HBExternalDisplayMoneyRecord()

  @Field
  var itemNumber: String?

  @Field
  var lookupCode: String?

  @Field
  var grossAmount: HBExternalDisplayMoneyRecord?

  @Field
  var discountRate: String?

  @Field
  var imageUri: String?

  func validated(index: Int) throws -> HBExternalDisplayItem {
    guard (1...160).contains(name.count) else {
      throw HBExternalDisplayValidationError.invalid("items[\(index)].name")
    }
    guard
      quantity.range(
        of: #"^-?\d+(?:\.\d{1,3})?$"#,
        options: .regularExpression
      ) != nil
    else {
      throw HBExternalDisplayValidationError.invalid("items[\(index)].quantity")
    }
    if let itemNumber {
      guard (1...64).contains(itemNumber.count) else {
        throw HBExternalDisplayValidationError.invalid("items[\(index)].itemNumber")
      }
    }
    if let lookupCode {
      guard (1...64).contains(lookupCode.count) else {
        throw HBExternalDisplayValidationError.invalid("items[\(index)].lookupCode")
      }
    }
    if let discountRate {
      guard
        discountRate.range(
          of: #"^\d{1,3}(?:\.\d{1,2})?$"#,
          options: .regularExpression
        ) != nil
      else {
        throw HBExternalDisplayValidationError.invalid("items[\(index)].discountRate")
      }
    }
    // 折前金额与折扣率只在该行有折扣时成对出现，缺一即视为非法快照。
    guard (grossAmount == nil) == (discountRate == nil) else {
      throw HBExternalDisplayValidationError.invalid("items[\(index)].discountRate")
    }
    let validatedImageUrl: URL? = try imageUri.map {
      try validatedLocalFileURL($0, field: "items[\(index)].imageUri")
    }

    return try HBExternalDisplayItem(
      name: name,
      quantity: quantity,
      unitPrice: unitPrice == nil
        ? nil
        : try unitPrice?.validated(field: "items[\(index)].unitPrice"),
      amount: amount.validated(field: "items[\(index)].amount"),
      itemNumber: itemNumber,
      lookupCode: lookupCode,
      grossAmount: grossAmount == nil
        ? nil
        : try grossAmount?.validated(field: "items[\(index)].grossAmount"),
      discountRate: discountRate,
      imageUri: imageUri,
      imageUrl: validatedImageUrl
    )
  }
}

struct HBExternalDisplaySummaryRecord: Record {
  @Field
  var itemQuantity = ""

  @Field
  var skuCount = 0

  @Field
  var subtotal = HBExternalDisplayMoneyRecord()

  func validated() throws -> HBExternalDisplaySummary {
    guard
      itemQuantity.range(
        of: #"^-?\d+(?:\.\d{1,3})?$"#,
        options: .regularExpression
      ) != nil
    else {
      throw HBExternalDisplayValidationError.invalid("summary.itemQuantity")
    }
    guard
      skuCount >= 0,
      skuCount <= maximumSafeJavaScriptInteger
    else {
      throw HBExternalDisplayValidationError.invalid("summary.skuCount")
    }

    return HBExternalDisplaySummary(
      itemQuantity: itemQuantity,
      skuCount: skuCount,
      subtotal: try subtotal.validated(field: "summary.subtotal")
    )
  }
}

struct HBExternalDisplayAdvertRecord: Record {
  @Field
  var kind = ""

  @Field
  var localUri = ""

  func validated() throws -> HBExternalDisplayAdvert {
    guard let parsedKind = HBExternalDisplayAdvertKind(rawValue: kind) else {
      throw HBExternalDisplayValidationError.invalid("advert.kind")
    }
    let url = try validatedLocalFileURL(localUri, field: "advert.localUri")

    return HBExternalDisplayAdvert(
      kind: parsedKind,
      localUri: localUri,
      url: url
    )
  }
}

struct HBExternalDisplaySnapshotRecord: Record {
  @Field
  var revision = -1

  @Field
  var mode = ""

  @Field
  var items: [HBExternalDisplayItemRecord] = []

  @Field
  var visibleItemStart: Int?

  @Field
  var summary: HBExternalDisplaySummaryRecord?

  @Field
  var gst = HBExternalDisplayMoneyRecord()

  @Field
  var discount = HBExternalDisplayMoneyRecord()

  @Field
  var total = HBExternalDisplayMoneyRecord()

  @Field
  var change = HBExternalDisplayMoneyRecord()

  @Field
  var advert: HBExternalDisplayAdvertRecord?

  func validated() throws -> HBExternalDisplaySnapshot {
    guard revision >= 0 else {
      throw HBExternalDisplayValidationError.invalid("revision")
    }
    guard let parsedMode = HBExternalDisplayMode(rawValue: mode) else {
      throw HBExternalDisplayValidationError.invalid("mode")
    }
    guard items.count <= 100 else {
      throw HBExternalDisplayValidationError.invalid("items")
    }
    let maximumVisibleItemStart = max(0, items.count - hbExternalDisplayVisibleItemLimit)
    if let visibleItemStart {
      guard
        visibleItemStart >= 0,
        visibleItemStart <= maximumVisibleItemStart
      else {
        throw HBExternalDisplayValidationError.invalid("visibleItemStart")
      }
    }

    return try HBExternalDisplaySnapshot(
      revision: revision,
      mode: parsedMode,
      items: items.enumerated().map { index, item in
        try item.validated(index: index)
      },
      visibleItemStart: visibleItemStart,
      summary: summary == nil ? nil : try summary?.validated(),
      gst: gst.validated(field: "gst"),
      discount: discount.validated(field: "discount"),
      total: total.validated(field: "total"),
      change: change.validated(field: "change"),
      advert: advert == nil ? nil : try advert?.validated()
    )
  }
}

enum HBExternalDisplayValidationError: LocalizedError {
  case invalid(String)

  var errorDescription: String? {
    switch self {
    case .invalid(let field):
      return "Invalid external display field: \(field)"
    }
  }
}
