# 移动端标签排版挪到 JS（改样式免重建原生包）

日期：2026-11-23　范围：`apps/mobile`（不含 `apps/pos-handheld/modules/hb-printer`）

## 背景

- 6 种标签（普通 / 特价 / 清货 / 大折扣 / 仓库商品 / 仓库货位）的排版写死在
  `HbPrinterModule.kt` 与 `plugins/ios/HbPrinterModule.swift` 各一份，每次改样式都要重建 iOS + APK（如 #1397）。
- 两端量字口径不同：Android `getTextBounds` 是字形墨迹高，iOS `NSString.size` 是行高，iOS 特价签另算 `inkTop` 补偿。
- 两端已有 `print(command, encoding)` 发原始 CPCL；缺的只是「把文字画成位图」。

## 定案

1. 原生层只提供通用小工具，排版全部在 JS（可 OTA）。
2. **两端打包同一套字体**：Roboto（Apache 2.0），取自 Android SDK `platforms/android-29/data/fonts`，
   Regular / Light / Medium / Bold / Black 五个字重，文件名加 `HbPrinter-` 前缀防冲突。
   Android 的 `sans-serif-black` 本身就是 Roboto Black，安卓出纸基本不变，iOS 变成与安卓一致。
3. **`writeBytes` 这次一起做**：JS 可直接下发二进制（以后用 `CG` 代替 `EG` 十六进制，传输量约减半）。
4. 二维码用 JS `qrcode`（已在 lockfile），条码仍用打印机 `BARCODE` 指令。

## 原生接口（两端一致，版本号 `version: 1`）

| 方法 | 入参 | 返回 |
|---|---|---|
| `getLabelRenderInfo()` | — | `{ version, fonts[], maxBatch, maxBitmapWidth, maxBitmapHeight, supportsFileFonts }` |
| `measureTexts(items)` | `[{ text, font, size }]` | `[{ advance, ascent, descent, inkLeft, inkTop, inkRight, inkBottom }]` |
| `renderTexts(items)` | `[{ text, font, size, inverse?, padding? }]` | `[{ width, height, originX, originY, hasInk, inkLeft, inkTop, inkRight, inkBottom, bits }]` |
| `writeBytes(base64)` | 原始字节的 base64 | `true` |

约定：

- `font`：内置键 `roboto-regular|roboto-light|roboto-medium|roboto-bold|roboto-black`，
  或 `file://` 开头的字体文件绝对路径（以后 OTA 下发新字体用），未知字体报 `PRINTER_FONT_NOT_FOUND`。
- `size`：打印点（dots，203dpi 下 1 点 ≈ 0.125mm），浮点。
- `measureTexts` 坐标以基线起点为原点、y 向下：`inkTop` 通常为负；`ascent` / `descent` 为正距离。
- `renderTexts` 画布 = 行框（`ascent + descent`）与墨迹框的并集，四周加 `padding`；`(originX, originY)` 是基线起点在位图中的位置；
  `ink*` 是阈值化后**实际黑白像素**的包围盒（右、下不含），两端同一口径；空白文字 `hasInk=false`。
- `bits`：1bit 位图 base64，每行 `ceil(width/8)` 字节、高位在左、1 = 黑，与 CPCL `EG` / `CG` 数据布局相同。
- 阈值：灰度 < 200 为黑（与现有 `isBlack` 一致）；`inverse` 时黑底白字。
- 单次最多 `maxBatch` 项，单图宽高受 `maxBitmapWidth` / `maxBitmapHeight` 限制，超限整批报错。

## 分期

| 期 | 内容 | 重建包 |
|---|---|---|
| P0 | 上述原生接口 + 字体打包 + JS 封装与能力判断；旧 `printXxxLabel` 原样保留 | ✅ 唯一一次 |
| P1 | JS 排版引擎（`Bitmap1` 位图合成、`fitText` / `wrapText` / `priceGroup`、自动合并重叠 EG），逐个移植 6 种标签，每种一个开关，默认仍走旧原生 | OTA |
| P2 | 真机对纸（Android / iOS 各打旧、新各一张），逐种打开开关；有问题改回开关再发 OTA | OTA |
| P3 | 旧包装机量很低后，在下一次本来就要做的原生版本里删掉 Kotlin / Swift 旧排版 | 顺带 |

兼容：`native.ts` 用 `getLabelRenderInfo` 存在且 `version >= 1` 判断新包；旧包继续走原生 `printXxxLabel`，
更老的 iOS 包仍回退到 JS CPCL（`TEXT` 字体）。

## 风险

- 出纸与现在不完全一致（基线、反白块内边距、二维码边长差几点），P2 必须逐种对纸。
- Skia 与 CoreGraphics 抗锯齿不同，同一字体边缘仍可能差 1 点；阈值化后影响很小。
- 中文：内置 Roboto 不含中文，两端渲染时都会自动回退到系统字体（Android minikin 回退链 / iOS CoreText 级联），
  能打出来但两端字形不完全相同；需要一致时再用 `file://` 下发中文字体。
- 包体：五个字重约 1.5MB（两端各一份）。

## P1 实现（2026-11-23）

代码在 `apps/mobile/src/modules/printer/label-layout/`：

| 文件 | 作用 |
|---|---|
| `mono-bitmap.ts` | 1bit 位图：裁剪（越界按底色补）、叠画（or / copy）、最近邻缩放、转十六进制 |
| `label-doc.ts` | 标签文档（位图块 + 打印机指令）→ EG 文本 / CG 二进制；下发前自动合并按字节补齐后会互相覆盖的位图 |
| `text.ts` | 复刻 Android `textToBitmap` / `longTextToBitmap` / `wrapText` / `fittedText`；字体映射；批量量字与「最大放得下字号」搜索 |
| `format.ts` | 与 Kotlin 同名函数逐条对齐的格式化（价格分拆、五成双、供应商缩写、EAN13 校验…） |
| `labels.ts` | 6 种标签排版，坐标公式逐条移植自 `HbPrinterModule.kt` |
| `config.ts` | 每种标签的开关（默认全关）与 EG / CG 下发方式（默认 EG）；运行时覆盖仅供测试 / 对纸 |

要点：

- **字体映射**按 Android 10+ minikin「基础字重 + 300 取最近」：`sans-serif-black`→Black，`sans-serif-light` 加粗→Medium、
  不加粗→Light，`Arial`（安卓上不存在，回退 sans-serif）加粗→Bold、不加粗→Regular，`sans-serif-condensed` 加粗→
  **Roboto Condensed Bold（本期补进内置字体，需随下一次原生包）**，旧测试包没有时退回 Bold。
- **位图口径**：从原生整行位图按 Android 口径裁出（横向从笔触起点、宽 = max(前进宽, 墨迹宽) + 2×padding；纵向只包墨迹）。
- **二维码**改用打印机自带 `BARCODE QR`（纠错 L），按旧 ZXing 的 64×64 占位框居中放置，排版坐标不变。
- **接入**：`native.ts` 的 6 个 `printNativeXxxLabel` 先看开关；开关打开且新原生包可用时走 JS 排版。
  排版 / 序列化阶段出错（还没发任何数据）回退原生排版；开始下发后失败直接抛出，不重放，避免重复出纸。
  CG 模式遇到非 ASCII 指令（如条码含中文）自动改用 EG。
- **与旧版一致保留的已知问题**（以后可单独发 OTA 修）：清货签商品名两行时第二行压到二维码；
  小号普通价签等级「A」固定在 x=300，会和「25%OFF」重叠；普通价签商品名右端贴着 `$`。

P2 对纸：做一个只留本地的测试包，用 `setJsLabelOverrides` 或改开关常量逐种打开，同一商品新旧各打一张比对。

## P2 对纸结论与开关（2026-11-23）

- 安卓 TC26 对纸包（APK 81：6 种全开，每次先打 JS 排版、再打原生排版）：**6 种标签全部一致**。
- 开关改为按平台：**安卓 6 种全开，iOS 仍全关**（iPhone 还没对纸，等 iOS 新包进 TestFlight 对纸后再开）。
- 下发方式仍为 EG。开关只在含 #1399 / #1401 的新原生包上生效；旧包始终走原生排版。
