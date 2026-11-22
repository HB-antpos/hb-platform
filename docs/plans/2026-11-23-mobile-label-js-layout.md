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
