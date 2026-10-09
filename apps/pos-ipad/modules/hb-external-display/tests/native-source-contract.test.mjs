import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const moduleRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
);

async function read(relativePath) {
  return readFile(path.join(moduleRoot, relativePath), "utf8");
}

test("scene delegate uses the iPadOS noninteractive scene lifecycle", async () => {
  const source = await read("ios/HBExternalDisplaySceneDelegate.swift");

  assert.match(
    source,
    /session\.role\s*==\s*\.windowExternalDisplayNonInteractive/,
  );
  assert.match(source, /UIWindow\(windowScene:\s*windowScene\)/);
  assert.match(source, /isUserInteractionEnabled\s*=\s*false/);
  assert.match(source, /windowScene\(\s*_\s+windowScene:[\s\S]*didUpdate/);
  assert.doesNotMatch(source, /UIScreen\.screens/);
});

test("scene delegate selects the highest external screen mode before creating its window", async () => {
  const source = await read("ios/HBExternalDisplaySceneDelegate.swift");

  assert.match(source, /windowScene\.screen\.availableModes/);
  assert.match(
    source,
    /\$0\.size\.width\s*\*\s*\$0\.size\.height/,
  );
  assert.match(source, /windowScene\.screen\.currentMode\s*=\s*displayMode/);
  assert.match(
    source,
    /currentMode\s*=\s*displayMode[\s\S]*UIWindow\(windowScene:\s*windowScene\)/,
  );
});

test("app delegate subscriber selects the highest mode as an external screen connects", async () => {
  const [source, config] = await Promise.all([
    read("ios/HBExternalDisplayAppDelegateSubscriber.swift"),
    read("expo-module.config.json"),
  ]);

  assert.match(
    config,
    /"appDelegateSubscribers"\s*:\s*\["HBExternalDisplayAppDelegateSubscriber"\]/,
  );
  assert.match(source, /UIScreen\.didConnectNotification/);
  assert.match(source, /notification\.object\s+as\?\s+UIScreen/);
  assert.match(source, /screen\.availableModes\.max/);
  assert.match(source, /screen\.currentMode\s*=\s*displayMode/);
});

test("primary scene delegate adopts the AppDelegate main window without restarting the Expo root", async () => {
  const source = await read("ios/HBPrimarySceneDelegate.swift");

  assert.match(source, /session\.role\s*==\s*\.windowApplication/);
  assert.match(
    source,
    /protocol HBPrimaryWindowAppDelegate[\s\S]*var window: UIWindow\?/,
  );
  assert.match(
    source,
    /guard\s+let appDelegate\s*=\s*UIApplication\.shared\.delegate\s+as\?\s+HBPrimaryWindowAppDelegate,\s*let appWindow\s*=\s*appDelegate\.window/,
  );
  assert.match(
    source,
    /appWindow\.windowScene\s*=\s*windowScene/,
  );
  assert.match(source, /window\s*=\s*appWindow/);
  assert.match(source, /appWindow\.makeKeyAndVisible\(\)/);
  assert.match(
    source,
    /existingWindowScene\.activationState\s*!=\s*\.unattached/,
  );
  assert.match(source, /existingWindowScene\s*!==\s*windowScene/);
  assert.match(source, /openURLContexts/);
  assert.match(source, /continue userActivity/);
  assert.match(source, /UIApplication\.shared\.delegate\?\.application\?/);
  assert.doesNotMatch(source, /\bUIWindow\s*\(/);
  assert.doesNotMatch(source, /startReactNative/);
  assert.doesNotMatch(source, /didStartReactNative/);
  assert.doesNotMatch(source, /remainingAttempts/);
  assert.doesNotMatch(source, /factory\.startReactNative/);
  assert.doesNotMatch(source, /appDelegate\.window\s*=\s*nil/);
});

test("coordinator owns a monotonic revision gate and required status events", async () => {
  const source = await read("ios/HBExternalDisplayCoordinator.swift");

  assert.match(source, /snapshot\.revision\s*>\s*latestRevision/);
  for (const event of [
    "connected",
    "disconnected",
    "resolutionChanged",
    "ready",
    "failed",
    "enabledChanged",
  ]) {
    assert.match(source, new RegExp(`"${event}"`));
  }
});

test("native revision gate is scoped to a JS producer session and replays after render", async () => {
  const [coordinator, swiftModule] = await Promise.all([
    read("ios/HBExternalDisplayCoordinator.swift"),
    read("ios/HBExternalDisplayModule.swift"),
  ]);

  assert.match(swiftModule, /producerSessionID\s*=\s*UUID\(\)\.uuidString/);
  assert.match(swiftModule, /beginProducerSession/);
  assert.match(swiftModule, /endProducerSession/);
  assert.match(coordinator, /activeProducerSessionID/);
  assert.match(
    coordinator,
    /guard\s+producerSessionID\s*==\s*activeProducerSessionID/,
  );
  assert.match(
    coordinator,
    /beginProducerSession[\s\S]*latestRevision\s*=\s*-1/,
  );
  assert.match(
    coordinator,
    /markReactSurfaceRendered[\s\S]*snapshotEventSink\?\(latestSnapshot\.dictionary\)/,
  );
});

test("producer epoch reset clears transaction state and returns every external endpoint to waiting", async () => {
  const coordinator = await read("ios/HBExternalDisplayCoordinator.swift");

  assert.match(
    coordinator,
    /private func resetProducerEpoch\(\)[\s\S]*latestRevision\s*=\s*-1[\s\S]*latestSnapshot\s*=\s*nil[\s\S]*reactSurfaceReady\s*=\s*false/,
  );
  assert.match(
    coordinator,
    /private func resetProducerEpoch\(\)[\s\S]*controller\?\.stopMedia\(\)[\s\S]*controller\?\.removeReactSurface\(\)[\s\S]*controller\?\.showWaitingState\(\)/,
  );
  assert.match(
    coordinator,
    /if isNewProducerSession \{[\s\S]*resetProducerEpoch\(\)[\s\S]*\}/,
  );
  assert.match(
    coordinator,
    /func endProducerSession[\s\S]*guard\s+producerSessionID\s*==\s*activeProducerSessionID[\s\S]*resetProducerEpoch\(\)/,
  );
});

test("session invalidation can force native waiting state without relying on the RN snapshot bridge", async () => {
  const [coordinator, swiftModule] = await Promise.all([
    read("ios/HBExternalDisplayCoordinator.swift"),
    read("ios/HBExternalDisplayModule.swift"),
  ]);

  assert.match(swiftModule, /AsyncFunction\("forceBlank"\)/);
  assert.match(
    coordinator,
    /func forceBlank[\s\S]*guard\s+producerSessionID\s*==\s*activeProducerSessionID/,
  );
  assert.match(
    coordinator,
    /func forceBlank[\s\S]*latestSnapshot\s*=\s*nil[\s\S]*controller\?\.stopMedia\(\)[\s\S]*controller\?\.removeReactSurface\(\)[\s\S]*controller\?\.showWaitingState\(\)/,
  );
  assert.match(
    coordinator,
    /func forceBlank[\s\S]*reason:\s*"sensitive-content-reset"/,
  );
});

test("native snapshot is allowlisted and advertisements are local files only", async () => {
  const [models, viewController] = await Promise.all([
    read("ios/HBExternalDisplayModels.swift"),
    read("ios/HBExternalDisplayViewController.swift"),
  ]);
  const combined = `${models}\n${viewController}`;

  assert.match(models, /advert\.localUri/);
  assert.match(viewController, /url\.isFileURL/);
  assert.doesNotMatch(
    combined,
    /deviceAuthorization|accessToken|refreshToken|cardReference|customerId/,
  );
});

test("native snapshot accepts optional unit price and summary without breaking legacy fields", async () => {
  const source = await read("ios/HBExternalDisplayModels.swift");

  assert.match(source, /var unitPrice: HBExternalDisplayMoneyRecord\?/);
  assert.match(source, /let unitPrice: HBExternalDisplayMoney\?/);
  assert.match(source, /payload\["unitPrice"\]\s*=\s*unitPrice\.dictionary/);
  assert.match(source, /var summary: HBExternalDisplaySummaryRecord\?/);
  assert.match(source, /let summary: HBExternalDisplaySummary\?/);
  assert.match(source, /payload\["summary"\]\s*=\s*summary\.dictionary/);
  assert.match(source, /struct HBExternalDisplaySummary/);
  assert.match(source, /let itemQuantity: String/);
  assert.match(source, /let skuCount: Int/);
  assert.match(source, /let subtotal: HBExternalDisplayMoney/);
  assert.match(source, /"itemQuantity": itemQuantity/);
  assert.match(source, /"skuCount": skuCount/);
  assert.match(source, /"subtotal": subtotal\.dictionary/);
  assert.match(
    source,
    /unitPrice == nil[\s\S]*\? nil[\s\S]*: try unitPrice\?\.validated/,
  );
  assert.match(
    source,
    /summary == nil \? nil : try summary\?\.validated/,
  );
  assert.match(source, /var visibleItemStart: Int\?/);
  assert.match(source, /let visibleItemStart: Int\?/);
  assert.match(source, /payload\["visibleItemStart"\]\s*=\s*visibleItemStart/);
  assert.match(
    source,
    /let maximumVisibleItemStart = max\(0, items\.count - hbExternalDisplayVisibleItemLimit\)/,
  );
  assert.match(source, /visibleItemStart\s*<=\s*maximumVisibleItemStart/);
  assert.match(
    source,
    /throw HBExternalDisplayValidationError\.invalid\("visibleItemStart"\)/,
  );
});

test("visible item limit is a single shared constant of 6 rows used by models and view controller", async () => {
  const [models, viewController] = await Promise.all([
    read("ios/HBExternalDisplayModels.swift"),
    read("ios/HBExternalDisplayViewController.swift"),
  ]);

  assert.match(models, /let hbExternalDisplayVisibleItemLimit\s*=\s*6\b/);
  assert.match(viewController, /let limit = hbExternalDisplayVisibleItemLimit/);
  // 旧的 12 行上限不得在窗口计算里残留。
  assert.doesNotMatch(models, /items\.count - 12/);
  assert.doesNotMatch(viewController, /itemCount - 12|start \+ 12/);
});

test("native item accepts WPF-aligned optional fields, validates them and echoes them back to React", async () => {
  const source = await read("ios/HBExternalDisplayModels.swift");

  for (const field of [
    "itemNumber",
    "lookupCode",
    "grossAmount",
    "discountRate",
    "imageUri",
  ]) {
    // Record 字段 + 回传 React 层的 dictionary 键都必须存在，漏了就丢字段。
    assert.match(source, new RegExp(`@Field\\s+var ${field}:`));
    assert.match(source, new RegExp(`payload\\["${field}"\\]\\s*=\\s*${field}`));
  }
  assert.match(source, /var grossAmount: HBExternalDisplayMoneyRecord\?/);
  assert.match(source, /\(1\.\.\.64\)\.contains\(itemNumber\.count\)/);
  assert.match(source, /\(1\.\.\.64\)\.contains\(lookupCode\.count\)/);
  assert.match(source, /\^\\d\{1,3\}\(\?:\\\.\\d\{1,2\}\)\?\$/);
  // grossAmount 与 discountRate 必须同时出现。
  assert.match(
    source,
    /\(grossAmount == nil\) == \(discountRate == nil\)[\s\S]*invalid\("items\[\\\(index\)\]\.discountRate"\)/,
  );
  // imageUri 与 advert.localUri 共用同一套本地 file URL 校验。
  assert.match(source, /private func validatedLocalFileURL\(/);
  assert.match(source, /uri\.count\s*<=\s*2_048/);
  assert.match(source, /url\.isFileURL/);
  assert.match(source, /url\.host == nil \|\| url\.host == "" \|\| url\.host == "localhost"/);
  assert.match(source, /validatedLocalFileURL\(localUri,\s*field:\s*"advert\.localUri"\)/);
  assert.match(source, /validatedLocalFileURL\(\$0,\s*field:\s*"items\[\\\(index\)\]\.imageUri"\)/);
});

test("UIKit fallback uses the shared WPF geometry and keeps idle advert full-screen", async () => {
  const source = await read("ios/HBExternalDisplayViewController.swift");

  // 标题栏与 "Your order" 标题行已删除。
  assert.doesNotMatch(source, /titleBar|windowTitleLabel|Customer Display/);
  assert.doesNotMatch(source, /Your order|orderTitleLabel/);
  assert.doesNotMatch(source, /closeButton|dismissButton|UIButton/);

  // 几何公式与 React 层一致：s = h/768、画布宽夹 1024...1366、占比 0.60 + 0.08 * ...
  assert.match(source, /struct HBExternalDisplayLayoutMetrics/);
  assert.match(source, /let s = height \/ 768/);
  assert.match(source, /min\(max\(768 \* width \/ height, 1024\), 1366\)/);
  assert.match(source, /0\.60 \+ 0\.08 \* \(\(1366 - canvasWidth\) \/ \(1366 - 1024\)\)/);
  assert.match(source, /let margin = 18 \* s/);
  assert.match(source, /let summaryHeight = 152 \* s/);
  assert.match(source, /let gap = 20 \* s/);
  assert.match(source, /contentWidth \* \(1 - cartShare\) - margin/);

  // 面板 frame 在 viewDidLayoutSubviews 里按当前 bounds 计算，旋转/分辨率变化会重排。
  assert.match(
    source,
    /override func viewDidLayoutSubviews\(\)[\s\S]*applyFrames\(\)/,
  );
  assert.match(source, /HBExternalDisplayLayoutMetrics\(size:\s*view\.bounds\.size\)/);
  assert.match(source, /orderPanel\.frame\s*=\s*metrics\.cartFrame/);
  assert.match(source, /advertContainer\.frame\s*=\s*metrics\.advertFrame/);
  assert.match(source, /summaryPanel\.frame\s*=\s*metrics\.summaryFrame/);
  assert.match(source, /abs\(s - contentScale\)\s*>\s*0\.0005/);

  // 广告面板圆角 18s、背景表面色；空闲全屏广告无边距/圆角/边框。
  assert.match(source, /advertContainer\.layer\.cornerRadius\s*=\s*18 \* s/);
  assert.match(source, /advertContainer\.backgroundColor\s*=\s*Palette\.surface/);
  assert.match(
    source,
    /snapshot\.mode\s*==\s*\.idle\s*&&\s*snapshot\.items\.isEmpty\s*&&\s*snapshot\.advert\s*!=\s*nil/,
  );
  assert.match(source, /orderPanel\.isHidden\s*=\s*fullScreenAdvert/);
  assert.match(source, /summaryPanel\.isHidden\s*=\s*fullScreenAdvert/);
  assert.match(source, /advertContainer\.frame\s*=\s*view\.bounds/);
  assert.match(source, /advertContainer\.layer\.cornerRadius\s*=\s*0/);
  assert.match(source, /advertContainer\.layer\.borderWidth\s*=\s*0/);
  assert.match(source, /UIView\.performWithoutAnimation/);
});

test("UIKit fallback uses the WPF palette", async () => {
  const source = await read("ios/HBExternalDisplayViewController.swift");

  assert.match(source, /background = UIColor\(red: 9 \/ 255, green: 17 \/ 255, blue: 31 \/ 255/);
  assert.match(source, /surface = UIColor\(red: 16 \/ 255, green: 27 \/ 255, blue: 45 \/ 255/);
  assert.match(source, /accent = UIColor\(red: 105 \/ 255, green: 227 \/ 255, blue: 194 \/ 255/);
  assert.match(source, /amount = UIColor\(red: 1, green: 199 \/ 255, blue: 61 \/ 255/);
  assert.match(source, /mutedText = UIColor\.white\.withAlphaComponent\(0\.64\)/);
  assert.match(source, /headerText = UIColor\.white\.withAlphaComponent\(0\.90\)/);
  assert.match(source, /divider = UIColor\.white\.withAlphaComponent\(0\.12\)/);
});

test("UIKit fallback renders WPF four-column cart table and summary", async () => {
  const source = await read("ios/HBExternalDisplayViewController.swift");

  for (const title of ["Item Description", "Qty", "Price", "Total"]) {
    assert.match(source, new RegExp(`title: "${title}"`));
  }
  // 列宽 96s / 116s / 126s，行高 72s，列头 48s。
  assert.match(source, /px\(96\)/);
  assert.match(source, /px\(116\)/);
  assert.match(source, /px\(126\)/);
  assert.match(source, /height:\s*px\(72\)/);
  assert.match(source, /size:\s*17,\s*weight:\s*\.bold,\s*color:\s*Palette\.headerText/);
  // 奇偶行交替：第 1、3、5…（从 0 计）行用表面色。
  assert.match(source, /index % 2 == 1 \? Palette\.surface : Palette\.background/);
  // 货号 / 查询码、数量胶囊、折扣率、删除线原价。
  assert.match(source, /"Item No\. \\\(itemNumber\)"/);
  assert.match(source, /px\(21\)/);
  assert.match(source, /"-\\\(discountRate\)%"/);
  assert.match(source, /\.strikethroughStyle:\s*NSUnderlineStyle\.single\.rawValue/);
  assert.match(source, /item\.grossAmount != nil \? Palette\.accent : Palette\.text/);
  assert.match(source, /pill\.layer\.cornerRadius\s*=\s*px\(12\)/);
  assert.match(source, /label\.text\s*=\s*item\.quantity/);
  assert.doesNotMatch(source, /"× \\\(item\.quantity\)"/);
  // 空购物车不显示任何空态文案。
  assert.doesNotMatch(source, /basket is empty|emptyLabel/);

  // 汇总区：三列 弹性 / 220s / 220s，Total To Pay 与状态卡。
  assert.match(source, /"Item Quantity"/);
  assert.match(source, /"SKU Count"/);
  assert.match(source, /title:\s*"Subtotal"/);
  assert.match(source, /title:\s*"GST"/);
  assert.match(source, /title:\s*"Savings"/);
  assert.match(source, /middleColumn\.widthAnchor\.constraint\(equalToConstant:\s*px\(220\)\)/);
  assert.match(source, /rightColumn\.widthAnchor\.constraint\(equalToConstant:\s*px\(220\)\)/);
  assert.match(source, /totalCaption\.text\s*=\s*"Total To Pay"/);
  assert.match(source, /size:\s*62,\s*weight:\s*\.black,\s*color:\s*Palette\.amount/);
  assert.match(source, /constraint\(equalToConstant:\s*px\(68\)\)/);
  assert.match(source, /subtotalValueLabel\.text\s*=\s*format\(summary\.subtotal\)/);
  // Savings 为 0 时整栏隐藏，且以 "-" 前缀显示。
  assert.match(source, /let hasSavings = snapshot\.discount\.cents != 0/);
  assert.match(source, /savingsMetricView\.alpha = hasSavings \? 1 : 0/);
  assert.match(source, /format:\s*"-\$%d\.%02d"/);
  assert.doesNotMatch(source, /Amount due|Your order|Unit price/);
  assert.match(
    source,
    /private func visibleItemWindow\([\s\S]*for snapshot: HBExternalDisplaySnapshot[\s\S]*-> HBExternalDisplayItemWindow/,
  );
  assert.match(source, /replaceItemRows\(with:\s*window\.items\)/);
  assert.match(source, /moreItemsLabel\.text\s*=\s*moreItemsText\(/);
});

test("UIKit fallback loads thumbnails only from validated local files and never touches the network", async () => {
  const [viewController, models] = await Promise.all([
    read("ios/HBExternalDisplayViewController.swift"),
    read("ios/HBExternalDisplayModels.swift"),
  ]);
  const combined = `${models}\n${viewController}`;

  assert.match(viewController, /UIImage\(contentsOfFile:\s*url\.path\)/);
  assert.match(viewController, /item\.imageUrl/);
  // 加载失败回退购物袋占位。
  assert.match(viewController, /UIImage\(systemName:\s*"bag\.fill"/);
  assert.doesNotMatch(
    combined,
    /URLSession|URLRequest|NSURLConnection|Data\(contentsOf|String\(contentsOf|contentsOf:\s*(?:url|URL)|URLSessionTask|WKWebView|CFNetwork|Network\.framework|import Network/,
  );
});

test("UIKit fallback slices from visibleItemStart and shows the hidden-row hint at the panel corner", async () => {
  const source = await read("ios/HBExternalDisplayViewController.swift");

  assert.match(source, /start = max\(itemCount - limit, 0\)/);
  assert.match(source, /let end = min\(start \+ limit, itemCount\)/);
  assert.match(source, /Array\(snapshot\.items\[start\.\.<end\]\)/);
  assert.match(source, /hiddenAbove:\s*start/);
  assert.match(source, /hiddenBelow:\s*max\(itemCount - end, 0\)/);
  assert.match(
    source,
    /private func moreItemsText\(hiddenAbove: Int, hiddenBelow: Int\) -> String\?/,
  );
  assert.match(source, /hiddenAbove > 0 && hiddenBelow > 0/);
  assert.match(source, /hiddenAbove > 0/);
  assert.match(source, /hiddenBelow > 0/);
  assert.match(source, /return nil/);
  // 提示小字在购物车面板右下角。
  assert.match(
    source,
    /moreItemsLabel\.trailingAnchor\.constraint\(\s*equalTo:\s*orderPanel\.trailingAnchor/,
  );
  assert.match(
    source,
    /moreItemsLabel\.bottomAnchor\.constraint\(\s*equalTo:\s*orderPanel\.bottomAnchor/,
  );
  assert.match(source, /english:\s*"\\\(hiddenAbove\) earlier · \\\(hiddenBelow\) later"/);
  assert.match(source, /chinese:\s*"上方 \\\(hiddenAbove\) 件 · 下方 \\\(hiddenBelow\) 件"/);
  assert.match(source, /english:\s*"\\\(hiddenAbove\) earlier"/);
  assert.match(source, /chinese:\s*"前面还有 \\\(hiddenAbove\) 件"/);
  assert.match(source, /english:\s*"\\\(hiddenBelow\) later"/);
  assert.match(source, /chinese:\s*"后面还有 \\\(hiddenBelow\) 件"/);
});

test("UIKit fallback renders Ready for Payment by default and keeps change/success copy", async () => {
  const source = await read("ios/HBExternalDisplayViewController.swift");

  // idle/cart/payment 与等待态：WPF 的 Ready for Payment / Insert or tap card。
  assert.match(source, /case \.none,\s*\.some\(\.idle\),\s*\.some\(\.cart\),\s*\.some\(\.payment\):/);
  assert.match(source, /english:\s*"Ready for Payment"/);
  assert.match(source, /english:\s*"Insert or tap card"/);
  // change/success 保留现有文案。
  for (const copy of [
    "Your change",
    "Payment complete",
    "Thank you for shopping with us",
  ]) {
    assert.match(source, new RegExp(`english:\\s*"${copy}"`));
  }
  assert.match(source, /statusSubtitleLabel\.text\s*=\s*format\(change\)/);
  assert.match(source, /change\.cents\s*!=\s*0/);
  assert.match(source, /english:\s*"Change \\\(format\(change\)\)"/);
  assert.match(source, /chinese:\s*"找零 \\\(format\(change\)\)"/);
});

test("UIKit fallback always selects English copy regardless of iPad language", async () => {
  const source = await read("ios/HBExternalDisplayViewController.swift");

  assert.match(
    source,
    /private func localizedText\(english: String, chinese _: String\) -> String \{\s*english\s*\}/,
  );
  assert.doesNotMatch(source, /Locale\.preferredLanguages/);
});

test("UIKit fallback safely derives summary and unit price when optional fields are missing", async () => {
  const source = await read("ios/HBExternalDisplayViewController.swift");

  assert.match(
    source,
    /private func resolvedSummary\([\s\S]*for snapshot: HBExternalDisplaySnapshot[\s\S]*-> HBExternalDisplaySummary/,
  );
  assert.match(source, /if let summary = snapshot\.summary \{[\s\S]*return summary/);
  assert.match(
    source,
    /subtotal:\s*HBExternalDisplayMoney\(\s*cents:\s*snapshot\.total\.cents\s*\+\s*snapshot\.discount\.cents\s*\)/,
  );
  assert.match(
    source,
    /private func unitPriceText\(for item: HBExternalDisplayItem\) -> String/,
  );
  assert.match(source, /if let unitPrice = item\.unitPrice \{[\s\S]*return format\(unitPrice\)/);
  assert.match(source, /private func unitPriceText[\s\S]*return "—"/);
  assert.doesNotMatch(source, /Double\(item\.quantity\)/);
});

test("UIKit fallback fits media without cropping, reuses identical adverts, and clears identity on stop", async () => {
  const source = await read("ios/HBExternalDisplayViewController.swift");

  assert.match(source, /advertImageView\.contentMode\s*=\s*\.scaleAspectFit/);
  assert.match(source, /layer\.videoGravity\s*=\s*\.resizeAspect\b/);
  assert.match(source, /videoLayer\?\.frame\s*=\s*advertContainer\.bounds/);
  assert.match(
    source,
    /struct AdvertIdentity:[\s\S]*let kind: String[\s\S]*let localUri: String/,
  );
  assert.match(
    source,
    /let identity = AdvertIdentity\([\s\S]*kind:\s*advert\.kind\.rawValue,[\s\S]*localUri:\s*advert\.localUri[\s\S]*\)/,
  );
  assert.match(
    source,
    /guard currentAdvertIdentity != identity else \{ return nil \}[\s\S]*stopMedia\(\)/,
  );
  assert.match(source, /currentAdvertIdentity\s*=\s*identity/);
  assert.match(
    source,
    /func stopMedia\(\)[\s\S]*currentAdvertIdentity\s*=\s*nil/,
  );
  assert.match(
    source,
    /guard asset\.isPlayable else \{[\s\S]*clearAdvert\(\)[\s\S]*return "advert-video-unavailable"/,
  );
});

test("video advert becomes reusable only when healthy and tears down runtime failures safely", async () => {
  const source = await read("ios/HBExternalDisplayViewController.swift");

  assert.match(source, /private var videoStatusObservation: NSKeyValueObservation\?/);
  assert.match(source, /private var videoFailureObserver: NSObjectProtocol\?/);
  assert.match(source, /private var videoStalledObserver: NSObjectProtocol\?/);
  assert.match(source, /private var pendingVideoIdentity: AdvertIdentity\?/);
  assert.match(source, /private var videoStartupTimeoutWorkItem: DispatchWorkItem\?/);
  assert.match(source, /private var videoRetryWorkItem: DispatchWorkItem\?/);
  assert.match(
    source,
    /item\.observe\(\s*\\\.status,[\s\S]*options:\s*\[\.initial,\s*\.new\]/,
  );
  assert.match(source, /\.AVPlayerItemFailedToPlayToEndTime/);
  assert.match(source, /\.AVPlayerItemPlaybackStalled/);
  assert.match(source, /queue:\s*\.main/);
  assert.match(source, /DispatchQueue\.main\.async/);
  assert.match(
    source,
    /let templateItem\s*=\s*AVPlayerItem\(asset:\s*asset\)[\s\S]*AVPlayerLooper\(player:\s*player,\s*templateItem:\s*templateItem\)[\s\S]*guard let playbackItem\s*=\s*player\.currentItem[\s\S]*observeVideoPlayback\([\s\S]*item:\s*playbackItem,[\s\S]*advert:\s*advert,[\s\S]*identity:\s*identity/,
  );
  assert.match(
    source,
    /case \.readyToPlay:[\s\S]*cancelVideoStartupTimeout\(\)[\s\S]*pendingVideoIdentity\s*=\s*nil[\s\S]*currentAdvertIdentity\s*=\s*identity/,
  );
  assert.match(
    source,
    /case \.failed:[\s\S]*handleVideoPlaybackFailure\([\s\S]*item:\s*item,[\s\S]*advert:\s*advert,[\s\S]*identity:\s*identity/,
  );
  assert.match(
    source,
    /private func handleVideoPlaybackFailure[\s\S]*guard[\s\S]*!isHandlingVideoFailure[\s\S]*videoPlayerItem === item \|\| videoPlayer\?\.currentItem === item[\s\S]*recordVideoFailure\(for:\s*identity\)[\s\S]*clearAdvert\(\)[\s\S]*HBExternalDisplayCoordinator\.shared\.reportFailure\("advert-video-playback-failed"\)[\s\S]*scheduleVideoRetry\(advert:\s*advert,\s*identity:\s*identity\)/,
  );
  assert.match(
    source,
    /videoFailureCounts\[identity,\s*default:\s*0\]\s*\+=\s*1/,
  );
  assert.match(
    source,
    /videoFailureCounts\[identity,\s*default:\s*0\]\s*<\s*maximumVideoFailureCount/,
  );
  assert.match(
    source,
    /func stopMedia\(\)[\s\S]*cancelVideoStartupTimeout\(\)[\s\S]*cancelVideoRetry\(\)[\s\S]*videoStatusObservation\?\.invalidate\(\)[\s\S]*NotificationCenter\.default\.removeObserver\(videoFailureObserver\)[\s\S]*NotificationCenter\.default\.removeObserver\(videoStalledObserver\)[\s\S]*pendingVideoIdentity\s*=\s*nil[\s\S]*currentAdvertIdentity\s*=\s*nil/,
  );
  assert.match(
    source,
    /deinit \{[\s\S]*videoStartupTimeoutWorkItem\?\.cancel\(\)[\s\S]*videoRetryWorkItem\?\.cancel\(\)[\s\S]*videoStatusObservation\?\.invalidate\(\)[\s\S]*NotificationCenter\.default\.removeObserver\(videoFailureObserver\)[\s\S]*NotificationCenter\.default\.removeObserver\(videoStalledObserver\)/,
  );
  assert.match(
    source,
    /private func scheduleVideoStartupTimeout[\s\S]*handleVideoPlaybackFailure[\s\S]*DispatchQueue\.main\.asyncAfter/,
  );
  assert.match(
    source,
    /private func scheduleVideoRetry[\s\S]*videoFailureCounts\[identity,\s*default:\s*0\]\s*<\s*maximumVideoFailureCount[\s\S]*lastRequestedAdvertIdentity\s*==\s*identity[\s\S]*guard[\s\S]*videoRetryToken\s*==\s*token[\s\S]*lastRequestedAdvertIdentity\s*==\s*identity[\s\S]*render\(advert:\s*advert\)[\s\S]*reportFailure\(failure\)[\s\S]*DispatchQueue\.main\.asyncAfter/,
  );
});

test("Expo module exports the external display bridge", async () => {
  const [moduleConfig, swiftModule] = await Promise.all([
    read("expo-module.config.json"),
    read("ios/HBExternalDisplayModule.swift"),
  ]);

  assert.match(moduleConfig, /"HBExternalDisplayModule"/);
  assert.match(swiftModule, /Name\("HBExternalDisplay"\)/);
  assert.match(swiftModule, /Events\("onStatusChanged",\s*"onSnapshotChanged"\)/);
  assert.match(swiftModule, /AsyncFunction\("publishSnapshot"\)/);
});

test("second React Native surface mounts only after registration and render handshakes", async () => {
  const [factory, coordinator, swiftModule] = await Promise.all([
    read("ios/HBExternalDisplayReactSurfaceFactory.swift"),
    read("ios/HBExternalDisplayCoordinator.swift"),
    read("ios/HBExternalDisplayModule.swift"),
  ]);

  assert.match(factory, /ExpoAppDelegate/);
  assert.match(factory, /rootViewFactory/);
  assert.match(factory, /withModuleName:\s*"HBExternalDisplay"/);
  assert.match(factory, /isUserInteractionEnabled\s*=\s*false/);
  assert.match(coordinator, /reactSurfaceReady/);
  assert.match(coordinator, /markReactSurfaceRendered/);
  assert.match(swiftModule, /AsyncFunction\("markReactSurfaceReady"\)/);
  assert.match(swiftModule, /AsyncFunction\("markReactSurfaceRendered"\)/);
  assert.match(swiftModule, /Events\("onStatusChanged",\s*"onSnapshotChanged"\)/);
});

test("JavaScript registers a noninteractive AppRegistry root", async () => {
  const source = await read(
    "../../src/core/peripherals/customer-display/native/external-display-react-surface.tsx",
  );

  assert.match(source, /AppRegistry\.registerComponent/);
  assert.match(source, /HBExternalDisplay/);
  assert.match(source, /markReactSurfaceReady/);
  assert.match(source, /markReactSurfaceRendered\(surfaceId\)/);
  assert.ok(
    source.indexOf('addListener(\n      "onSnapshotChanged"') <
      source.indexOf("markReactSurfaceRendered(surfaceId)"),
    "snapshot listener must be attached before native replay handshake",
  );
  assert.match(source, /pointerEvents="none"/);
  assert.match(source, /accessible=\{false\}/);
});
