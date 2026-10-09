import AVFoundation
import UIKit

// WPF 客显配色（PosTheme.xaml 的 PosCustomerDisplay*）。
// React 面板与 UIKit 兜底层必须使用同一套颜色。
private enum HBExternalDisplayPalette {
  static let background = UIColor(red: 9 / 255, green: 17 / 255, blue: 31 / 255, alpha: 1)
  static let surface = UIColor(red: 16 / 255, green: 27 / 255, blue: 45 / 255, alpha: 1)
  static let text = UIColor.white
  static let mutedText = UIColor.white.withAlphaComponent(0.64)
  static let headerText = UIColor.white.withAlphaComponent(0.90)
  static let accent = UIColor(red: 105 / 255, green: 227 / 255, blue: 194 / 255, alpha: 1)
  static let amount = UIColor(red: 1, green: 199 / 255, blue: 61 / 255, alpha: 1)
  static let divider = UIColor.white.withAlphaComponent(0.12)
  static let accentSurface = UIColor(
    red: 105 / 255,
    green: 227 / 255,
    blue: 194 / 255,
    alpha: 0.12
  )
}

// 客显几何：React 层透明广告窗口与 UIKit 兜底层必须使用同一套公式。
// s = 屏高 / 768（WPF 设计画布高），所有 WPF 像素值乘 s；
// 不做 WPF 的宽屏两侧留白，直接铺满整个屏幕宽度。
struct HBExternalDisplayLayoutMetrics {
  let scale: CGFloat
  let cartFrame: CGRect
  let advertFrame: CGRect
  let summaryFrame: CGRect

  init(size: CGSize) {
    guard size.width > 0, size.height > 0 else {
      scale = 1
      cartFrame = .zero
      advertFrame = .zero
      summaryFrame = .zero
      return
    }

    let width = size.width
    let height = size.height
    let s = height / 768
    // 画布宽夹在 1024...1366 之间；越窄购物车占比越大，最多 0.68（对应 WPF ResolveCartColumnShare）。
    let canvasWidth = min(max(768 * width / height, 1024), 1366)
    let cartShare = 0.60 + 0.08 * ((1366 - canvasWidth) / (1366 - 1024))
    let margin = 18 * s
    let summaryHeight = 152 * s
    let gap = 20 * s
    let contentWidth = width - 2 * margin
    let contentHeight = height - 2 * margin - summaryHeight - gap
    let cartWidth = contentWidth * cartShare
    // 购物车与广告之间固定留 18s。
    let advertWidth = contentWidth * (1 - cartShare) - margin

    scale = s
    cartFrame = CGRect(x: margin, y: margin, width: cartWidth, height: contentHeight)
    advertFrame = CGRect(
      x: margin + cartWidth + margin,
      y: margin,
      width: advertWidth,
      height: contentHeight
    )
    summaryFrame = CGRect(
      x: margin,
      y: margin + contentHeight + gap,
      width: contentWidth,
      height: summaryHeight
    )
  }
}

final class HBExternalDisplayViewController: UIViewController {
  private typealias Palette = HBExternalDisplayPalette

  private struct AdvertIdentity: Hashable {
    let kind: String
    let localUri: String
  }

  private struct HBExternalDisplayItemWindow {
    let items: [HBExternalDisplayItem]
    let hiddenAbove: Int
    let hiddenBelow: Int
  }

  // 左侧购物车面板：frame 布局，内部内容按当前缩放重建。
  private let orderPanel = UIView()
  private var itemStack = UIStackView()
  private var moreItemsLabel = UILabel()

  // 右侧广告媒体区。
  private let advertContainer = UIView()
  private let advertImageView = UIImageView()

  // 底部全宽汇总区，三列：弹性 / 220s / 220s。
  private let summaryPanel = UIView()
  private var itemQuantityValueLabel = UILabel()
  private var skuCountValueLabel = UILabel()
  private var subtotalValueLabel = UILabel()
  private var gstValueLabel = UILabel()
  private var savingsMetricView = UIView()
  private var savingsValueLabel = UILabel()
  private var totalValueLabel = UILabel()
  private var statusTitleLabel = UILabel()
  private var statusSubtitleLabel = UILabel()

  private var videoPlayer: AVQueuePlayer?
  private var videoLooper: AVPlayerLooper?
  private var videoLayer: AVPlayerLayer?
  private var videoPlayerItem: AVPlayerItem?
  private var videoStatusObservation: NSKeyValueObservation?
  private var videoFailureObserver: NSObjectProtocol?
  private var videoStalledObserver: NSObjectProtocol?
  private var videoStartupTimeoutWorkItem: DispatchWorkItem?
  private var videoStartupTimeoutToken: UUID?
  private var videoRetryWorkItem: DispatchWorkItem?
  private var videoRetryToken: UUID?
  private var reactSurface: UIView?
  private var currentAdvertIdentity: AdvertIdentity?
  private var pendingVideoIdentity: AdvertIdentity?
  private var lastRequestedAdvertIdentity: AdvertIdentity?
  private var videoFailureCounts: [AdvertIdentity: Int] = [:]
  private var isHandlingVideoFailure = false
  private var isShowingFullScreenAdvert = false
  // 当前内容使用的缩放系数；屏幕尺寸/旋转变化导致 s 变化时整体重建内容。
  private var contentScale: CGFloat = 1
  // 最近一次渲染的快照；nil 表示等待态。缩放变化重建内容后据此重绘。
  private var lastSnapshot: HBExternalDisplaySnapshot?
  private let maximumVideoFailureCount = 2
  private let videoRetryDelay: TimeInterval = 0.75
  private let videoStartupTimeout: TimeInterval = 5

  var hasReactSurface: Bool {
    reactSurface != nil
  }

  deinit {
    videoStartupTimeoutWorkItem?.cancel()
    videoRetryWorkItem?.cancel()
    videoStatusObservation?.invalidate()
    if let videoFailureObserver {
      NotificationCenter.default.removeObserver(videoFailureObserver)
    }
    if let videoStalledObserver {
      NotificationCenter.default.removeObserver(videoStalledObserver)
    }
  }

  override func loadView() {
    let rootView = UIView()
    rootView.backgroundColor = Palette.background
    rootView.isUserInteractionEnabled = false
    view = rootView

    configureLayout()
    showWaitingState()
  }

  override func viewDidLayoutSubviews() {
    super.viewDidLayoutSubviews()
    applyFrames()
    videoLayer?.frame = advertContainer.bounds
  }

  func showWaitingState() {
    resetVideoRetryState()
    updateFallbackLayout(fullScreenAdvert: false)
    lastSnapshot = nil
    renderTransaction()
    clearAdvert()
  }

  @discardableResult
  func render(snapshot: HBExternalDisplaySnapshot) -> String? {
    updateFallbackLayout(
      fullScreenAdvert:
        snapshot.mode == .idle
        && snapshot.items.isEmpty
        && snapshot.advert != nil
    )
    lastSnapshot = snapshot
    renderTransaction()

    return render(advert: snapshot.advert)
  }

  func stopMedia() {
    cancelVideoStartupTimeout()
    cancelVideoRetry()
    videoStatusObservation?.invalidate()
    videoStatusObservation = nil
    if let videoFailureObserver {
      NotificationCenter.default.removeObserver(videoFailureObserver)
    }
    videoFailureObserver = nil
    if let videoStalledObserver {
      NotificationCenter.default.removeObserver(videoStalledObserver)
    }
    videoStalledObserver = nil
    videoPlayerItem = nil
    pendingVideoIdentity = nil
    videoPlayer?.pause()
    videoLayer?.removeFromSuperlayer()
    videoLayer = nil
    videoLooper = nil
    videoPlayer = nil
    currentAdvertIdentity = nil
  }

  func installReactSurface(
    initialProperties: [AnyHashable: Any]
  ) -> String? {
    guard reactSurface == nil else { return nil }

    do {
      let surface = try HBExternalDisplayReactSurfaceFactory.makeSurface(
        initialProperties: initialProperties
      )
      surface.translatesAutoresizingMaskIntoConstraints = false
      surface.isUserInteractionEnabled = false
      surface.isOpaque = false
      surface.backgroundColor = .clear
      view.addSubview(surface)
      NSLayoutConstraint.activate([
        surface.leadingAnchor.constraint(equalTo: view.leadingAnchor),
        surface.trailingAnchor.constraint(equalTo: view.trailingAnchor),
        surface.topAnchor.constraint(equalTo: view.topAnchor),
        surface.bottomAnchor.constraint(equalTo: view.bottomAnchor),
      ])
      reactSurface = surface
      return nil
    } catch {
      return error.localizedDescription
    }
  }

  func removeReactSurface() {
    reactSurface?.removeFromSuperview()
    reactSurface = nil
  }

  // MARK: - 布局

  // 三块顶层面板都用 frame 布局，frame 与 React 层同一套公式
  // （HBExternalDisplayLayoutMetrics），旋转或分辨率变化时在 viewDidLayoutSubviews 重新计算。
  private func configureLayout() {
    orderPanel.backgroundColor = Palette.background
    orderPanel.layer.borderWidth = 1
    orderPanel.layer.borderColor = Palette.divider.cgColor
    orderPanel.layer.masksToBounds = true
    view.addSubview(orderPanel)

    advertContainer.layer.borderColor = Palette.divider.cgColor
    advertContainer.layer.masksToBounds = true
    advertImageView.contentMode = .scaleAspectFit
    advertImageView.translatesAutoresizingMaskIntoConstraints = false
    advertContainer.addSubview(advertImageView)
    NSLayoutConstraint.activate([
      advertImageView.leadingAnchor.constraint(equalTo: advertContainer.leadingAnchor),
      advertImageView.trailingAnchor.constraint(equalTo: advertContainer.trailingAnchor),
      advertImageView.topAnchor.constraint(equalTo: advertContainer.topAnchor),
      advertImageView.bottomAnchor.constraint(equalTo: advertContainer.bottomAnchor),
    ])
    view.addSubview(advertContainer)

    summaryPanel.backgroundColor = Palette.surface
    summaryPanel.layer.borderWidth = 1
    summaryPanel.layer.borderColor = Palette.divider.cgColor
    summaryPanel.layer.masksToBounds = true
    view.addSubview(summaryPanel)

    rebuildContent()
    applyFrames()
  }

  private func applyFrames() {
    let metrics = HBExternalDisplayLayoutMetrics(size: view.bounds.size)
    let s = metrics.scale

    orderPanel.frame = metrics.cartFrame
    orderPanel.layer.cornerRadius = 12 * s
    summaryPanel.frame = metrics.summaryFrame
    summaryPanel.layer.cornerRadius = 12 * s

    if isShowingFullScreenAdvert {
      // 空闲全屏广告：铺满整个屏幕，无边距、无圆角、无边框。
      advertContainer.frame = view.bounds
      advertContainer.backgroundColor = .clear
      advertContainer.layer.cornerRadius = 0
      advertContainer.layer.borderWidth = 0
    } else {
      advertContainer.frame = metrics.advertFrame
      advertContainer.backgroundColor = Palette.surface
      advertContainer.layer.cornerRadius = 18 * s
      advertContainer.layer.borderWidth = 1
    }

    // 缩放系数变化（旋转/分辨率切换）时按新的 s 重建内容并重绘当前快照。
    if abs(s - contentScale) > 0.0005 {
      contentScale = s
      rebuildContent()
      renderTransaction()
    }
  }

  private func updateFallbackLayout(fullScreenAdvert: Bool) {
    guard fullScreenAdvert != isShowingFullScreenAdvert else { return }
    isShowingFullScreenAdvert = fullScreenAdvert

    UIView.performWithoutAnimation {
      orderPanel.isHidden = fullScreenAdvert
      summaryPanel.isHidden = fullScreenAdvert
      view.setNeedsLayout()
      view.layoutIfNeeded()
    }
  }

  // 所有 WPF 像素值乘以当前缩放系数。
  private func px(_ value: CGFloat) -> CGFloat {
    value * contentScale
  }

  private func rebuildContent() {
    buildCartPanel()
    buildSummaryPanel()
  }

  // MARK: - 购物车面板

  private func buildCartPanel() {
    orderPanel.subviews.forEach { $0.removeFromSuperview() }
    let padding = px(20)

    let headerRow = makeTableRow(
      height: px(48),
      background: Palette.surface,
      item: makeHeaderCell(title: "Item Description", alignment: .left),
      quantity: makeHeaderCell(title: "Qty", alignment: .center),
      price: makeHeaderCell(title: "Price", alignment: .right),
      total: makeHeaderCell(title: "Total", alignment: .right)
    )
    headerRow.translatesAutoresizingMaskIntoConstraints = false
    orderPanel.addSubview(headerRow)

    itemStack = UIStackView()
    itemStack.axis = .vertical
    itemStack.alignment = .fill
    itemStack.distribution = .fill
    itemStack.spacing = 0
    itemStack.translatesAutoresizingMaskIntoConstraints = false
    orderPanel.addSubview(itemStack)

    moreItemsLabel = makeLabel(
      size: 12,
      weight: .medium,
      color: Palette.mutedText,
      alignment: .right
    )
    moreItemsLabel.isHidden = true
    moreItemsLabel.translatesAutoresizingMaskIntoConstraints = false
    orderPanel.addSubview(moreItemsLabel)

    NSLayoutConstraint.activate([
      headerRow.leadingAnchor.constraint(equalTo: orderPanel.leadingAnchor, constant: padding),
      headerRow.trailingAnchor.constraint(equalTo: orderPanel.trailingAnchor, constant: -padding),
      headerRow.topAnchor.constraint(equalTo: orderPanel.topAnchor, constant: padding),
      itemStack.leadingAnchor.constraint(equalTo: headerRow.leadingAnchor),
      itemStack.trailingAnchor.constraint(equalTo: headerRow.trailingAnchor),
      itemStack.topAnchor.constraint(equalTo: headerRow.bottomAnchor),
      itemStack.bottomAnchor.constraint(
        lessThanOrEqualTo: orderPanel.bottomAnchor,
        constant: -padding
      ),
      moreItemsLabel.trailingAnchor.constraint(
        equalTo: orderPanel.trailingAnchor,
        constant: -padding
      ),
      moreItemsLabel.bottomAnchor.constraint(
        equalTo: orderPanel.bottomAnchor,
        constant: -px(6)
      ),
    ])
  }

  // 表格行骨架：商品列（弹性）/ Qty 96s / Price 116s / Total 126s，行底 1 分隔线。
  private func makeTableRow(
    height: CGFloat,
    background: UIColor?,
    item: UIView,
    quantity: UIView,
    price: UIView,
    total: UIView
  ) -> UIView {
    let row = UIView()
    row.backgroundColor = background
    row.heightAnchor.constraint(equalToConstant: height).isActive = true

    quantity.widthAnchor.constraint(equalToConstant: px(96)).isActive = true
    price.widthAnchor.constraint(equalToConstant: px(116)).isActive = true
    total.widthAnchor.constraint(equalToConstant: px(126)).isActive = true
    item.setContentHuggingPriority(.defaultLow, for: .horizontal)
    item.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)

    let stack = UIStackView(arrangedSubviews: [item, quantity, price, total])
    stack.axis = .horizontal
    stack.alignment = .fill
    stack.distribution = .fill
    stack.spacing = 0
    stack.translatesAutoresizingMaskIntoConstraints = false

    let divider = UIView()
    divider.backgroundColor = Palette.divider
    divider.translatesAutoresizingMaskIntoConstraints = false

    row.addSubview(stack)
    row.addSubview(divider)
    NSLayoutConstraint.activate([
      stack.leadingAnchor.constraint(equalTo: row.leadingAnchor),
      stack.trailingAnchor.constraint(equalTo: row.trailingAnchor),
      stack.topAnchor.constraint(equalTo: row.topAnchor),
      stack.bottomAnchor.constraint(equalTo: row.bottomAnchor),
      divider.leadingAnchor.constraint(equalTo: row.leadingAnchor),
      divider.trailingAnchor.constraint(equalTo: row.trailingAnchor),
      divider.bottomAnchor.constraint(equalTo: row.bottomAnchor),
      divider.heightAnchor.constraint(equalToConstant: 1),
    ])
    return row
  }

  private func makeHeaderCell(title: String, alignment: NSTextAlignment) -> UIView {
    let cell = UIView()
    let label = makeLabel(
      size: 17,
      weight: .bold,
      color: Palette.headerText,
      alignment: alignment
    )
    label.text = title
    label.translatesAutoresizingMaskIntoConstraints = false
    cell.addSubview(label)
    // 列头内边距 12s。
    NSLayoutConstraint.activate([
      label.leadingAnchor.constraint(equalTo: cell.leadingAnchor, constant: px(12)),
      label.trailingAnchor.constraint(equalTo: cell.trailingAnchor, constant: -px(12)),
      label.centerYAnchor.constraint(equalTo: cell.centerYAnchor),
    ])
    return cell
  }

  private func makeLabel(
    size: CGFloat,
    weight: UIFont.Weight,
    color: UIColor,
    alignment: NSTextAlignment = .left,
    monospacedDigits: Bool = false
  ) -> UILabel {
    let label = UILabel()
    label.font =
      monospacedDigits
      ? .monospacedDigitSystemFont(ofSize: px(size), weight: weight)
      : .systemFont(ofSize: px(size), weight: weight)
    label.textColor = color
    label.textAlignment = alignment
    label.numberOfLines = 1
    return label
  }

  private func replaceItemRows(with items: [HBExternalDisplayItem]) {
    itemStack.arrangedSubviews.forEach { row in
      itemStack.removeArrangedSubview(row)
      row.removeFromSuperview()
    }

    // 空购物车只显示列头与空表，不再显示任何空态文案。
    for (index, item) in items.enumerated() {
      // 与 WPF AlternatingRowBackground 一致：第 1、3、5…（从 0 计）行用表面色。
      let row = makeTableRow(
        height: px(72),
        background: index % 2 == 1 ? Palette.surface : Palette.background,
        item: makeItemCell(for: item),
        quantity: makeQuantityCell(for: item),
        price: makePriceCell(for: item),
        total: makeTotalCell(for: item)
      )
      itemStack.addArrangedSubview(row)
    }

    // 剩余空间由底部弹性占位吸收，行从顶部排列。
    let flexibleSpacer = UIView()
    flexibleSpacer.setContentHuggingPriority(.defaultLow, for: .vertical)
    flexibleSpacer.setContentCompressionResistancePriority(.defaultLow, for: .vertical)
    itemStack.addArrangedSubview(flexibleSpacer)
  }

  private func makeItemCell(for item: HBExternalDisplayItem) -> UIView {
    let cell = UIView()
    let thumbnail = makeThumbnail(for: item)
    thumbnail.translatesAutoresizingMaskIntoConstraints = false

    let nameLabel = makeLabel(size: 19, weight: .semibold, color: Palette.text)
    nameLabel.text = item.name
    nameLabel.lineBreakMode = .byTruncatingTail
    nameLabel.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)

    let textStack = UIStackView(arrangedSubviews: [nameLabel])
    textStack.axis = .vertical
    textStack.alignment = .fill
    textStack.distribution = .fill
    textStack.spacing = px(4)
    textStack.translatesAutoresizingMaskIntoConstraints = false
    // 货号与查询码都没有时不占行。
    if let metaRow = makeMetaRow(for: item) {
      textStack.addArrangedSubview(metaRow)
    }

    cell.addSubview(thumbnail)
    cell.addSubview(textStack)
    NSLayoutConstraint.activate([
      thumbnail.leadingAnchor.constraint(equalTo: cell.leadingAnchor, constant: px(12)),
      thumbnail.centerYAnchor.constraint(equalTo: cell.centerYAnchor),
      thumbnail.widthAnchor.constraint(equalToConstant: px(52)),
      thumbnail.heightAnchor.constraint(equalToConstant: px(52)),
      textStack.leadingAnchor.constraint(equalTo: thumbnail.trailingAnchor, constant: px(12)),
      textStack.trailingAnchor.constraint(equalTo: cell.trailingAnchor, constant: -px(8)),
      textStack.centerYAnchor.constraint(equalTo: cell.centerYAnchor),
    ])
    return cell
  }

  // 52s 缩略图盒：图来自已校验的本地 file URL，读失败回退购物袋占位。
  // 这里只用 UIImage(contentsOfFile:)，客显层永不联网取图。
  private func makeThumbnail(for item: HBExternalDisplayItem) -> UIView {
    let box = UIView()
    box.backgroundColor = Palette.accentSurface
    box.layer.cornerRadius = px(6)
    box.layer.borderWidth = 1
    box.layer.borderColor = Palette.divider.cgColor
    box.layer.masksToBounds = true

    let imageView = UIImageView()
    imageView.translatesAutoresizingMaskIntoConstraints = false
    if let url = item.imageUrl, let image = UIImage(contentsOfFile: url.path) {
      imageView.image = image
      imageView.contentMode = .scaleAspectFit
    } else {
      let configuration = UIImage.SymbolConfiguration(pointSize: px(22), weight: .regular)
      imageView.image = UIImage(systemName: "bag.fill", withConfiguration: configuration)
      imageView.tintColor = Palette.accent
      imageView.contentMode = .center
    }
    box.addSubview(imageView)
    let inset = px(2)
    NSLayoutConstraint.activate([
      imageView.leadingAnchor.constraint(equalTo: box.leadingAnchor, constant: inset),
      imageView.trailingAnchor.constraint(equalTo: box.trailingAnchor, constant: -inset),
      imageView.topAnchor.constraint(equalTo: box.topAnchor, constant: inset),
      imageView.bottomAnchor.constraint(equalTo: box.bottomAnchor, constant: -inset),
    ])
    return box
  }

  // "Item No. xxx" | lookupCode，两者之间插 1×12s 竖线（左右各 10s）。
  private func makeMetaRow(for item: HBExternalDisplayItem) -> UIView? {
    var arranged: [UIView] = []
    if let itemNumber = item.itemNumber {
      let label = makeLabel(size: 12, weight: .regular, color: Palette.mutedText)
      label.text = "Item No. \(itemNumber)"
      label.lineBreakMode = .byTruncatingTail
      arranged.append(label)
    }
    if item.itemNumber != nil, item.lookupCode != nil {
      let separator = UIView()
      separator.translatesAutoresizingMaskIntoConstraints = false
      let line = UIView()
      line.backgroundColor = Palette.divider
      line.translatesAutoresizingMaskIntoConstraints = false
      separator.addSubview(line)
      NSLayoutConstraint.activate([
        separator.widthAnchor.constraint(equalToConstant: px(21)),
        line.widthAnchor.constraint(equalToConstant: 1),
        line.heightAnchor.constraint(equalToConstant: px(12)),
        line.centerXAnchor.constraint(equalTo: separator.centerXAnchor),
        line.centerYAnchor.constraint(equalTo: separator.centerYAnchor),
      ])
      separator.setContentHuggingPriority(.required, for: .horizontal)
      separator.setContentCompressionResistancePriority(.required, for: .horizontal)
      arranged.append(separator)
    }
    if let lookupCode = item.lookupCode {
      let label = makeLabel(size: 12, weight: .regular, color: Palette.mutedText)
      label.text = lookupCode
      label.lineBreakMode = .byTruncatingTail
      arranged.append(label)
    }
    guard !arranged.isEmpty else { return nil }

    // 货号标签不被拉伸，保证竖线紧跟其后；多余宽度由最后一个标签吸收。
    arranged.first?.setContentHuggingPriority(.required, for: .horizontal)
    arranged.last?.setContentHuggingPriority(.defaultLow, for: .horizontal)
    let stack = UIStackView(arrangedSubviews: arranged)
    stack.axis = .horizontal
    stack.alignment = .center
    stack.distribution = .fill
    stack.spacing = 0
    return stack
  }

  // 数量胶囊：内边距 10s×4s，强调表面背景，1 强调色边框，圆角 12s。
  private func makeQuantityCell(for item: HBExternalDisplayItem) -> UIView {
    let cell = UIView()
    let pill = UIView()
    pill.backgroundColor = Palette.accentSurface
    pill.layer.cornerRadius = px(12)
    pill.layer.borderWidth = 1
    pill.layer.borderColor = Palette.accent.cgColor
    pill.translatesAutoresizingMaskIntoConstraints = false

    let label = makeLabel(
      size: 16,
      weight: .bold,
      color: Palette.accent,
      alignment: .center,
      monospacedDigits: true
    )
    label.text = item.quantity
    label.translatesAutoresizingMaskIntoConstraints = false
    pill.addSubview(label)
    cell.addSubview(pill)
    NSLayoutConstraint.activate([
      label.leadingAnchor.constraint(equalTo: pill.leadingAnchor, constant: px(10)),
      label.trailingAnchor.constraint(equalTo: pill.trailingAnchor, constant: -px(10)),
      label.topAnchor.constraint(equalTo: pill.topAnchor, constant: px(4)),
      label.bottomAnchor.constraint(equalTo: pill.bottomAnchor, constant: -px(4)),
      pill.centerXAnchor.constraint(equalTo: cell.centerXAnchor),
      pill.centerYAnchor.constraint(equalTo: cell.centerYAnchor),
      pill.leadingAnchor.constraint(greaterThanOrEqualTo: cell.leadingAnchor, constant: px(2)),
      pill.trailingAnchor.constraint(lessThanOrEqualTo: cell.trailingAnchor, constant: -px(2)),
    ])
    return cell
  }

  // 单价 17s 白色右对齐；有折扣率时其下 2s 显示 "-{rate}%"（12s 粗体强调色）。
  private func makePriceCell(for item: HBExternalDisplayItem) -> UIView {
    let priceLabel = makeLabel(
      size: 17,
      weight: .regular,
      color: Palette.text,
      alignment: .right,
      monospacedDigits: true
    )
    priceLabel.text = unitPriceText(for: item)
    var arranged: [UIView] = [priceLabel]
    if let discountRate = item.discountRate {
      let rateLabel = makeLabel(
        size: 12,
        weight: .bold,
        color: Palette.accent,
        alignment: .right
      )
      rateLabel.text = "-\(discountRate)%"
      arranged.append(rateLabel)
    }
    return wrapTrailing(arranged, spacing: px(2))
  }

  // 有 grossAmount 时先显示删除线原价（13s 次要色），其下是实收金额 19s 粗体；
  // 有折扣时实收用强调色，否则白色。
  private func makeTotalCell(for item: HBExternalDisplayItem) -> UIView {
    var arranged: [UIView] = []
    if let grossAmount = item.grossAmount {
      let grossLabel = makeLabel(
        size: 13,
        weight: .regular,
        color: Palette.mutedText,
        alignment: .right,
        monospacedDigits: true
      )
      grossLabel.attributedText = NSAttributedString(
        string: format(grossAmount),
        attributes: [
          .strikethroughStyle: NSUnderlineStyle.single.rawValue,
          .strikethroughColor: Palette.mutedText,
          .font: grossLabel.font as Any,
          .foregroundColor: Palette.mutedText,
        ]
      )
      arranged.append(grossLabel)
    }
    let amountLabel = makeLabel(
      size: 19,
      weight: .bold,
      color: item.grossAmount != nil ? Palette.accent : Palette.text,
      alignment: .right,
      monospacedDigits: true
    )
    amountLabel.text = format(item.amount)
    arranged.append(amountLabel)
    return wrapTrailing(arranged, spacing: px(2))
  }

  // 价格/合计列：内容右对齐、右内缩 12s、垂直居中。
  private func wrapTrailing(_ views: [UIView], spacing: CGFloat) -> UIView {
    let cell = UIView()
    let stack = UIStackView(arrangedSubviews: views)
    stack.axis = .vertical
    stack.alignment = .trailing
    stack.distribution = .fill
    stack.spacing = spacing
    stack.translatesAutoresizingMaskIntoConstraints = false
    cell.addSubview(stack)
    NSLayoutConstraint.activate([
      stack.trailingAnchor.constraint(equalTo: cell.trailingAnchor, constant: -px(12)),
      stack.leadingAnchor.constraint(greaterThanOrEqualTo: cell.leadingAnchor),
      stack.centerYAnchor.constraint(equalTo: cell.centerYAnchor),
    ])
    return cell
  }

  // MARK: - 汇总区

  private func buildSummaryPanel() {
    summaryPanel.subviews.forEach { $0.removeFromSuperview() }

    itemQuantityValueLabel = makeLabel(size: 15, weight: .regular, color: Palette.mutedText)
    skuCountValueLabel = makeLabel(size: 15, weight: .regular, color: Palette.mutedText)
    subtotalValueLabel = makeAmountValueLabel()
    gstValueLabel = makeAmountValueLabel()
    savingsValueLabel = makeAmountValueLabel()
    totalValueLabel = makeLabel(
      size: 62,
      weight: .black,
      color: Palette.amount,
      alignment: .right,
      monospacedDigits: true
    )
    totalValueLabel.adjustsFontSizeToFitWidth = true
    totalValueLabel.minimumScaleFactor = 0.3

    // 左列：数量行 + 三栏金额（Subtotal / GST / Savings）。
    let countsRow = UIStackView(arrangedSubviews: [
      makeCountCaption("Item Quantity"),
      itemQuantityValueLabel,
      makeCountCaption("SKU Count"),
      skuCountValueLabel,
      UIView(),
    ])
    countsRow.axis = .horizontal
    countsRow.alignment = .center
    countsRow.distribution = .fill
    countsRow.spacing = px(8)
    countsRow.setCustomSpacing(px(32), after: itemQuantityValueLabel)

    let subtotalMetric = makeMetric(title: "Subtotal", valueLabel: subtotalValueLabel)
    let gstMetric = makeMetric(title: "GST", valueLabel: gstValueLabel)
    savingsMetricView = makeMetric(title: "Savings", valueLabel: savingsValueLabel)
    let metricsRow = UIStackView(arrangedSubviews: [subtotalMetric, gstMetric, savingsMetricView])
    metricsRow.axis = .horizontal
    metricsRow.alignment = .top
    metricsRow.distribution = .fillEqually
    metricsRow.spacing = px(12)

    let leftStack = UIStackView(arrangedSubviews: [countsRow, metricsRow])
    leftStack.axis = .vertical
    leftStack.alignment = .fill
    leftStack.distribution = .fill
    leftStack.spacing = px(8)
    leftStack.translatesAutoresizingMaskIntoConstraints = false
    let leftColumn = UIView()
    leftColumn.addSubview(leftStack)
    NSLayoutConstraint.activate([
      leftStack.leadingAnchor.constraint(equalTo: leftColumn.leadingAnchor),
      leftStack.trailingAnchor.constraint(equalTo: leftColumn.trailingAnchor, constant: -px(12)),
      leftStack.centerYAnchor.constraint(equalTo: leftColumn.centerYAnchor),
    ])

    // 中列：Total To Pay，右对齐，金额宽 ≤200s、高 ≤68s，超出缩小。
    let totalCaption = makeLabel(
      size: 16,
      weight: .black,
      color: Palette.text,
      alignment: .right
    )
    totalCaption.text = "Total To Pay"
    let totalStack = UIStackView(arrangedSubviews: [totalCaption, totalValueLabel])
    totalStack.axis = .vertical
    totalStack.alignment = .fill
    totalStack.distribution = .fill
    totalStack.spacing = 0
    totalStack.translatesAutoresizingMaskIntoConstraints = false
    let middleColumn = UIView()
    middleColumn.addSubview(totalStack)
    NSLayoutConstraint.activate([
      totalStack.widthAnchor.constraint(equalToConstant: px(200)),
      totalStack.trailingAnchor.constraint(equalTo: middleColumn.trailingAnchor, constant: -px(20)),
      totalStack.centerYAnchor.constraint(equalTo: middleColumn.centerYAnchor),
      totalValueLabel.heightAnchor.constraint(equalToConstant: px(68)),
    ])

    // 右列：状态卡（强调表面、1 强调色边框、圆角 12s、内边距 16s、垂直居中）。
    statusTitleLabel = makeLabel(size: 18, weight: .black, color: Palette.accent)
    statusTitleLabel.numberOfLines = 2
    statusSubtitleLabel = makeLabel(size: 15, weight: .regular, color: Palette.text)
    statusSubtitleLabel.numberOfLines = 2
    let statusStack = UIStackView(arrangedSubviews: [statusTitleLabel, statusSubtitleLabel])
    statusStack.axis = .vertical
    statusStack.alignment = .fill
    statusStack.distribution = .fill
    statusStack.spacing = px(4)
    statusStack.translatesAutoresizingMaskIntoConstraints = false
    let statusCard = UIView()
    statusCard.backgroundColor = Palette.accentSurface
    statusCard.layer.cornerRadius = px(12)
    statusCard.layer.borderWidth = 1
    statusCard.layer.borderColor = Palette.accent.cgColor
    statusCard.translatesAutoresizingMaskIntoConstraints = false
    statusCard.addSubview(statusStack)
    let rightColumn = UIView()
    rightColumn.addSubview(statusCard)
    NSLayoutConstraint.activate([
      statusStack.leadingAnchor.constraint(equalTo: statusCard.leadingAnchor, constant: px(16)),
      statusStack.trailingAnchor.constraint(equalTo: statusCard.trailingAnchor, constant: -px(16)),
      statusStack.topAnchor.constraint(equalTo: statusCard.topAnchor, constant: px(16)),
      statusStack.bottomAnchor.constraint(equalTo: statusCard.bottomAnchor, constant: -px(16)),
      statusCard.leadingAnchor.constraint(equalTo: rightColumn.leadingAnchor),
      statusCard.trailingAnchor.constraint(equalTo: rightColumn.trailingAnchor),
      statusCard.centerYAnchor.constraint(equalTo: rightColumn.centerYAnchor),
    ])

    middleColumn.widthAnchor.constraint(equalToConstant: px(220)).isActive = true
    rightColumn.widthAnchor.constraint(equalToConstant: px(220)).isActive = true
    let sections = UIStackView(arrangedSubviews: [leftColumn, middleColumn, rightColumn])
    sections.axis = .horizontal
    sections.alignment = .fill
    sections.distribution = .fill
    sections.spacing = 0
    sections.translatesAutoresizingMaskIntoConstraints = false
    summaryPanel.addSubview(sections)
    // 汇总区内边距 16s（上下）× 24s（左右）。
    NSLayoutConstraint.activate([
      sections.leadingAnchor.constraint(equalTo: summaryPanel.leadingAnchor, constant: px(24)),
      sections.trailingAnchor.constraint(equalTo: summaryPanel.trailingAnchor, constant: -px(24)),
      sections.topAnchor.constraint(equalTo: summaryPanel.topAnchor, constant: px(16)),
      sections.bottomAnchor.constraint(equalTo: summaryPanel.bottomAnchor, constant: -px(16)),
    ])
  }

  private func makeCountCaption(_ title: String) -> UILabel {
    let label = makeLabel(size: 15, weight: .regular, color: Palette.mutedText)
    label.text = title
    label.setContentHuggingPriority(.required, for: .horizontal)
    return label
  }

  private func makeAmountValueLabel() -> UILabel {
    let label = makeLabel(
      size: 28,
      weight: .black,
      color: Palette.text,
      monospacedDigits: true
    )
    // 宽度不足时缩小，最大高度 38s。
    label.adjustsFontSizeToFitWidth = true
    label.minimumScaleFactor = 0.5
    label.heightAnchor.constraint(lessThanOrEqualToConstant: px(38)).isActive = true
    return label
  }

  private func makeMetric(title: String, valueLabel: UILabel) -> UIView {
    let titleLabel = makeLabel(size: 14, weight: .bold, color: Palette.mutedText)
    titleLabel.text = title
    let stack = UIStackView(arrangedSubviews: [titleLabel, valueLabel])
    stack.axis = .vertical
    stack.alignment = .fill
    stack.distribution = .fill
    stack.spacing = 0
    return stack
  }

  // MARK: - 渲染当前状态

  // 按 lastSnapshot 刷新购物车、汇总与状态卡；lastSnapshot 为 nil 即等待态。
  private func renderTransaction() {
    guard let snapshot = lastSnapshot else {
      replaceItemRows(with: [])
      moreItemsLabel.text = nil
      moreItemsLabel.isHidden = true
      itemQuantityValueLabel.text = "0"
      skuCountValueLabel.text = "0"
      subtotalValueLabel.text = "$0.00"
      gstValueLabel.text = "$0.00"
      savingsMetricView.alpha = 0
      totalValueLabel.text = "$0.00"
      renderStatusCard(mode: nil, change: HBExternalDisplayMoney(cents: 0))
      return
    }

    renderStatusCard(mode: snapshot.mode, change: snapshot.change)
    let window = visibleItemWindow(for: snapshot)
    replaceItemRows(with: window.items)
    moreItemsLabel.text = moreItemsText(
      hiddenAbove: window.hiddenAbove,
      hiddenBelow: window.hiddenBelow
    )
    moreItemsLabel.isHidden =
      window.hiddenAbove == 0 && window.hiddenBelow == 0
    let summary = resolvedSummary(for: snapshot)
    itemQuantityValueLabel.text = summary.itemQuantity
    skuCountValueLabel.text = String(summary.skuCount)
    subtotalValueLabel.text = format(summary.subtotal)
    gstValueLabel.text = format(snapshot.gst)
    // 无优惠时整栏隐藏（用 alpha 保留列位，与 WPF 折叠不改变列宽一致）。
    let hasSavings = snapshot.discount.cents != 0
    savingsMetricView.alpha = hasSavings ? 1 : 0
    savingsValueLabel.text = hasSavings ? formatSavings(snapshot.discount) : nil
    totalValueLabel.text = format(snapshot.total)
  }

  private func visibleItemWindow(
    for snapshot: HBExternalDisplaySnapshot
  ) -> HBExternalDisplayItemWindow {
    let itemCount = snapshot.items.count
    let limit = hbExternalDisplayVisibleItemLimit
    let start: Int
    if let visibleItemStart = snapshot.visibleItemStart {
      start = min(max(visibleItemStart, 0), max(itemCount - limit, 0))
    } else {
      start = max(itemCount - limit, 0)
    }
    let end = min(start + limit, itemCount)
    return HBExternalDisplayItemWindow(
      items: Array(snapshot.items[start..<end]),
      hiddenAbove: start,
      hiddenBelow: max(itemCount - end, 0)
    )
  }

  private func moreItemsText(hiddenAbove: Int, hiddenBelow: Int) -> String? {
    if hiddenAbove > 0 && hiddenBelow > 0 {
      return localizedText(
        english: "\(hiddenAbove) earlier · \(hiddenBelow) later",
        chinese: "上方 \(hiddenAbove) 件 · 下方 \(hiddenBelow) 件"
      )
    }
    if hiddenAbove > 0 {
      return localizedText(
        english: "\(hiddenAbove) earlier",
        chinese: "前面还有 \(hiddenAbove) 件"
      )
    }
    if hiddenBelow > 0 {
      return localizedText(
        english: "\(hiddenBelow) later",
        chinese: "后面还有 \(hiddenBelow) 件"
      )
    }
    return nil
  }

  private func resolvedSummary(
    for snapshot: HBExternalDisplaySnapshot
  ) -> HBExternalDisplaySummary {
    if let summary = snapshot.summary {
      return summary
    }

    let quantityTotal = snapshot.items.reduce(Decimal.zero) { total, item in
      total + (Decimal(string: item.quantity) ?? 0)
    }
    return HBExternalDisplaySummary(
      itemQuantity: NSDecimalNumber(decimal: quantityTotal).stringValue,
      skuCount: snapshot.items.count,
      subtotal: HBExternalDisplayMoney(
        cents: snapshot.total.cents + snapshot.discount.cents
      )
    )
  }

  private func unitPriceText(for item: HBExternalDisplayItem) -> String {
    if let unitPrice = item.unitPrice {
      return format(unitPrice)
    }
    return "—"
  }

  // mode 为 nil（等待态）以及 idle/cart/payment 都显示 WPF 的 Ready for Payment；
  // change/success 保留 iPad 原有的找零/完成文案。
  private func renderStatusCard(
    mode: HBExternalDisplayMode?,
    change: HBExternalDisplayMoney
  ) {
    switch mode {
    case .none, .some(.idle), .some(.cart), .some(.payment):
      statusTitleLabel.text = localizedText(english: "Ready for Payment", chinese: "准备付款")
      statusSubtitleLabel.text = localizedText(english: "Insert or tap card", chinese: "请插卡或挥卡")
    case .some(.change):
      statusTitleLabel.text = localizedText(english: "Your change", chinese: "找零")
      statusSubtitleLabel.text = format(change)
    case .some(.success):
      statusTitleLabel.text = localizedText(english: "Payment complete", chinese: "付款完成")
      statusSubtitleLabel.text = change.cents != 0
        ? localizedText(
          english: "Change \(format(change))",
          chinese: "找零 \(format(change))"
        )
        : localizedText(
          english: "Thank you for shopping with us",
          chinese: "谢谢惠顾"
        )
    }
  }

  // UIKit 启动兜底也固定使用英文，避免 React surface 就绪前短暂显示中文。
  private func localizedText(english: String, chinese _: String) -> String {
    english
  }

  private func format(_ money: HBExternalDisplayMoney) -> String {
    let absoluteCents = abs(money.cents)
    let sign = money.cents < 0 ? "-" : ""
    return String(
      format: "%@$%d.%02d",
      sign,
      absoluteCents / 100,
      absoluteCents % 100
    )
  }

  // snapshot 保存的是折扣绝对金额；Savings 栏统一以 "-" 前缀呈现为减项。
  private func formatSavings(_ money: HBExternalDisplayMoney) -> String {
    let absoluteCents = abs(money.cents)
    return String(
      format: "-$%d.%02d",
      absoluteCents / 100,
      absoluteCents % 100
    )
  }

  private func render(advert: HBExternalDisplayAdvert?) -> String? {
    guard let advert else {
      resetVideoRetryState()
      clearAdvert()
      return nil
    }

    let identity = AdvertIdentity(
      kind: advert.kind.rawValue,
      localUri: advert.localUri
    )
    prepareVideoRetryState(for: identity)
    guard currentAdvertIdentity != identity else { return nil }
    guard pendingVideoIdentity != identity else { return nil }

    stopMedia()
    advertImageView.image = nil

    let url = advert.url
    guard url.isFileURL, FileManager.default.fileExists(atPath: url.path) else {
      clearAdvert()
      return "advert-file-unavailable"
    }

    switch advert.kind {
    case .image:
      guard let image = UIImage(contentsOfFile: url.path) else {
        clearAdvert()
        return "advert-image-unavailable"
      }
      advertImageView.image = image
      advertImageView.isHidden = false
      currentAdvertIdentity = identity

    case .video:
      guard
        videoFailureCounts[identity, default: 0]
          < maximumVideoFailureCount
      else {
        clearAdvert()
        return "advert-video-retry-exhausted"
      }
      let asset = AVURLAsset(url: url)
      guard asset.isPlayable else {
        recordVideoFailure(for: identity)
        clearAdvert()
        return "advert-video-unavailable"
      }
      advertImageView.isHidden = true
      let player = AVQueuePlayer()
      let templateItem = AVPlayerItem(asset: asset)
      videoLooper = AVPlayerLooper(player: player, templateItem: templateItem)
      guard let playbackItem = player.currentItem else {
        recordVideoFailure(for: identity)
        clearAdvert()
        return "advert-video-unavailable"
      }
      let layer = AVPlayerLayer(player: player)
      layer.videoGravity = .resizeAspect
      advertContainer.layer.insertSublayer(layer, at: 0)
      videoPlayer = player
      videoLayer = layer
      observeVideoPlayback(
        item: playbackItem,
        advert: advert,
        identity: identity
      )
      player.play()
      view.setNeedsLayout()
    }

    return nil
  }

  private func prepareVideoRetryState(for identity: AdvertIdentity) {
    guard lastRequestedAdvertIdentity != identity else { return }
    lastRequestedAdvertIdentity = identity
    videoFailureCounts.removeAll()
  }

  private func resetVideoRetryState() {
    lastRequestedAdvertIdentity = nil
    videoFailureCounts.removeAll()
  }

  private func recordVideoFailure(for identity: AdvertIdentity) {
    videoFailureCounts[identity, default: 0] += 1
  }

  private func observeVideoPlayback(
    item: AVPlayerItem,
    advert: HBExternalDisplayAdvert,
    identity: AdvertIdentity
  ) {
    videoPlayerItem = item
    pendingVideoIdentity = identity
    videoStatusObservation = item.observe(
      \.status,
      options: [.initial, .new]
    ) { [weak self, weak item] _, _ in
      DispatchQueue.main.async {
        guard let self, let item else { return }
        self.handleVideoStatusChange(
          item: item,
          advert: advert,
          identity: identity
        )
      }
    }
    videoFailureObserver = NotificationCenter.default.addObserver(
      forName: .AVPlayerItemFailedToPlayToEndTime,
      object: nil,
      queue: .main
    ) { [weak self] notification in
      guard let item = notification.object as? AVPlayerItem else { return }
      self?.handleVideoPlaybackFailure(
        item: item,
        advert: advert,
        identity: identity
      )
    }
    videoStalledObserver = NotificationCenter.default.addObserver(
      forName: .AVPlayerItemPlaybackStalled,
      object: nil,
      queue: .main
    ) { [weak self] notification in
      guard let item = notification.object as? AVPlayerItem else { return }
      self?.handleVideoPlaybackFailure(
        item: item,
        advert: advert,
        identity: identity
      )
    }
    scheduleVideoStartupTimeout(
      item: item,
      advert: advert,
      identity: identity
    )
  }

  private func handleVideoStatusChange(
    item: AVPlayerItem,
    advert: HBExternalDisplayAdvert,
    identity: AdvertIdentity
  ) {
    precondition(Thread.isMainThread)
    guard videoPlayerItem === item else { return }

    switch item.status {
    case .readyToPlay:
      cancelVideoStartupTimeout()
      pendingVideoIdentity = nil
      currentAdvertIdentity = identity
    case .failed:
      handleVideoPlaybackFailure(
        item: item,
        advert: advert,
        identity: identity
      )
    case .unknown:
      break
    @unknown default:
      break
    }
  }

  private func handleVideoPlaybackFailure(
    item: AVPlayerItem,
    advert: HBExternalDisplayAdvert,
    identity: AdvertIdentity
  ) {
    precondition(Thread.isMainThread)
    guard
      !isHandlingVideoFailure,
      pendingVideoIdentity == identity || currentAdvertIdentity == identity,
      videoPlayerItem === item || videoPlayer?.currentItem === item
    else {
      return
    }

    isHandlingVideoFailure = true
    defer { isHandlingVideoFailure = false }
    recordVideoFailure(for: identity)
    // 先撤销媒体再上报，避免播放器 teardown 再次触发失败通知。
    clearAdvert()
    HBExternalDisplayCoordinator.shared.reportFailure("advert-video-playback-failed")
    scheduleVideoRetry(advert: advert, identity: identity)
  }

  private func scheduleVideoStartupTimeout(
    item: AVPlayerItem,
    advert: HBExternalDisplayAdvert,
    identity: AdvertIdentity
  ) {
    cancelVideoStartupTimeout()
    let token = UUID()
    videoStartupTimeoutToken = token
    let workItem = DispatchWorkItem { [weak self, weak item] in
      guard
        let self,
        let item,
        self.videoStartupTimeoutToken == token,
        self.pendingVideoIdentity == identity,
        self.lastRequestedAdvertIdentity == identity
      else {
        return
      }

      self.videoStartupTimeoutWorkItem = nil
      self.videoStartupTimeoutToken = nil
      self.handleVideoPlaybackFailure(
        item: item,
        advert: advert,
        identity: identity
      )
    }
    videoStartupTimeoutWorkItem = workItem
    DispatchQueue.main.asyncAfter(
      deadline: .now() + videoStartupTimeout,
      execute: workItem
    )
  }

  private func cancelVideoStartupTimeout() {
    videoStartupTimeoutWorkItem?.cancel()
    videoStartupTimeoutWorkItem = nil
    videoStartupTimeoutToken = nil
  }

  private func scheduleVideoRetry(
    advert: HBExternalDisplayAdvert,
    identity: AdvertIdentity
  ) {
    guard
      videoFailureCounts[identity, default: 0] < maximumVideoFailureCount,
      lastRequestedAdvertIdentity == identity
    else {
      return
    }

    cancelVideoRetry()
    let token = UUID()
    videoRetryToken = token
    let workItem = DispatchWorkItem { [weak self] in
      guard
        let self,
        self.videoRetryToken == token,
        self.lastRequestedAdvertIdentity == identity
      else {
        return
      }

      self.videoRetryWorkItem = nil
      self.videoRetryToken = nil
      if let failure = self.render(advert: advert) {
        HBExternalDisplayCoordinator.shared.reportFailure(failure)
      }
    }
    videoRetryWorkItem = workItem
    DispatchQueue.main.asyncAfter(
      deadline: .now() + videoRetryDelay,
      execute: workItem
    )
  }

  private func cancelVideoRetry() {
    videoRetryWorkItem?.cancel()
    videoRetryWorkItem = nil
    videoRetryToken = nil
  }

  private func clearAdvert() {
    stopMedia()
    advertImageView.image = nil
    advertImageView.isHidden = true
  }
}
