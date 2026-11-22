package com.hbweb.expo

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import android.graphics.Typeface
import android.net.Uri
import android.util.Base64
import java.io.File
import java.util.concurrent.ConcurrentHashMap
import kotlin.math.ceil
import kotlin.math.floor
import kotlin.math.max
import kotlin.math.min

/**
 * 标签打印的通用文字渲染器：只负责「按字体、字号把一段文字量出尺寸 / 画成 1bit 位图」，
 * 坐标、字号、文案等排版全部由 JS 决定（可走 OTA）。接口约定见 docs/plans/2026-11-23-mobile-label-js-layout.md，
 * 必须与 iOS 的 HbPrinterTextRenderer.swift 保持同一口径。
 */
internal class HbPrinterTextRenderer(private val context: Context) {
  class RenderError(val code: String, message: String) : Exception(message)

  data class TextMetrics(
    val advance: Float,
    val ascent: Float,
    val descent: Float,
    val inkLeft: Float,
    val inkTop: Float,
    val inkRight: Float,
    val inkBottom: Float,
  )

  data class TextBitmap(
    val width: Int,
    val height: Int,
    val originX: Int,
    val originY: Int,
    val hasInk: Boolean,
    val inkLeft: Int,
    val inkTop: Int,
    val inkRight: Int,
    val inkBottom: Int,
    val bitsBase64: String,
  )

  private val typefaces = ConcurrentHashMap<String, Typeface>()

  /** 按字体键取 Typeface：内置键读 APK assets，file:// 读本地字体文件（以后 OTA 下发新字体用）；结果缓存。 */
  fun typeface(font: String): Typeface {
    typefaces[font]?.let { return it }
    val loaded = try {
      val assetName = BUNDLED_FONTS[font]
      when {
        assetName != null -> Typeface.createFromAsset(context.assets, "printer-fonts/$assetName")
        font.startsWith("file://") -> {
          // Expo 文件 URI 可能带百分号转义（空格等），按 URI 解码出真实路径。
          val path = Uri.parse(font).path
          val file = path?.let { File(it) }
          if (file == null || !file.isFile) null else Typeface.createFromFile(file)
        }
        else -> null
      }
    } catch (error: Exception) {
      null
    } ?: throw RenderError("PRINTER_FONT_NOT_FOUND", "Printer font is not available: $font")
    typefaces[font] = loaded
    return loaded
  }

  private fun paint(font: String, size: Float): Paint {
    if (!size.isFinite() || size <= 0f || size > MAX_FONT_SIZE) {
      throw RenderError("PRINTER_RENDER_INVALID", "Font size is out of range: $size")
    }
    return Paint(Paint.ANTI_ALIAS_FLAG or Paint.SUBPIXEL_TEXT_FLAG).apply {
      typeface = typeface(font)
      textSize = size
      color = Color.BLACK
      textAlign = Paint.Align.LEFT
      // 关掉字形微调，量出来的前进宽度与 CoreText 一样是不取整的设计宽度，两端换行 / 缩字结果一致。
      hinting = Paint.HINTING_OFF
    }
  }

  fun measure(text: String, font: String, size: Float): TextMetrics {
    checkText(text)
    val paint = paint(font, size)
    val metrics = paint.fontMetrics
    val ink = inkBounds(paint, text)
    return TextMetrics(
      advance = paint.measureText(text),
      ascent = -metrics.ascent,
      descent = metrics.descent,
      inkLeft = ink.left,
      inkTop = ink.top,
      inkRight = ink.right,
      inkBottom = ink.bottom,
    )
  }

  fun render(text: String, font: String, size: Float, inverse: Boolean, padding: Int): TextBitmap {
    checkText(text)
    if (padding < 0 || padding > MAX_PADDING) {
      throw RenderError("PRINTER_RENDER_INVALID", "Padding is out of range: $padding")
    }
    val paint = paint(font, size)
    val metrics = paint.fontMetrics
    val ink = inkBounds(paint, text)
    val advance = paint.measureText(text)

    // 画布 = 行框（ascent..descent、0..advance）与墨迹框的并集，四周再加 padding；基线起点取整到像素。
    val left = floor(min(0f, ink.left)).toInt()
    val right = ceil(max(advance, ink.right)).toInt()
    val top = floor(min(metrics.ascent, ink.top)).toInt()
    val bottom = ceil(max(metrics.descent, ink.bottom)).toInt()
    val width = max(1, right - left + padding * 2)
    val height = max(1, bottom - top + padding * 2)
    if (width > MAX_BITMAP_WIDTH || height > MAX_BITMAP_HEIGHT) {
      throw RenderError("PRINTER_RENDER_TOO_LARGE", "Rendered text is too large: ${width}x$height")
    }
    val originX = padding - left
    val originY = padding - top

    val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
    val canvas = Canvas(bitmap)
    canvas.drawColor(if (inverse) Color.BLACK else Color.WHITE)
    paint.color = if (inverse) Color.WHITE else Color.BLACK
    canvas.drawText(text, originX.toFloat(), originY.toFloat(), paint)

    val pixels = IntArray(width * height)
    bitmap.getPixels(pixels, 0, width, 0, 0, width, height)
    bitmap.recycle()

    val rowBytes = (width + 7) / 8
    val bits = ByteArray(rowBytes * height)
    var inkMinX = Int.MAX_VALUE
    var inkMinY = Int.MAX_VALUE
    var inkMaxX = -1
    var inkMaxY = -1
    for (y in 0 until height) {
      for (x in 0 until width) {
        val black = isBlack(pixels[y * width + x])
        if (black) {
          val index = y * rowBytes + (x shr 3)
          bits[index] = (bits[index].toInt() or (0x80 ushr (x and 7))).toByte()
        }
        // 墨迹 = 与底色不同的像素（正常是黑字，反白是白字），两端同一口径。
        if (black != inverse) {
          if (x < inkMinX) inkMinX = x
          if (x > inkMaxX) inkMaxX = x
          if (y < inkMinY) inkMinY = y
          if (y > inkMaxY) inkMaxY = y
        }
      }
    }
    val hasInk = inkMaxX >= 0
    return TextBitmap(
      width = width,
      height = height,
      originX = originX,
      originY = originY,
      hasInk = hasInk,
      inkLeft = if (hasInk) inkMinX else 0,
      inkTop = if (hasInk) inkMinY else 0,
      inkRight = if (hasInk) inkMaxX + 1 else 0,
      inkBottom = if (hasInk) inkMaxY + 1 else 0,
      bitsBase64 = Base64.encodeToString(bits, Base64.NO_WRAP),
    )
  }

  /**
   * 用字形轮廓量墨迹框（浮点，取轮廓控制点包围盒；TrueType 字形在极值处都有点，误差远小于 1 点）；空白文字返回全 0。
   * 只供排版估算，需要像素级对齐时以 render 返回的实际像素墨迹为准。
   */
  private fun inkBounds(paint: Paint, text: String): RectF {
    if (text.isBlank()) return RectF(0f, 0f, 0f, 0f)
    val path = Path()
    paint.getTextPath(text, 0, text.length, 0f, 0f, path)
    val bounds = RectF()
    @Suppress("DEPRECATION")
    path.computeBounds(bounds, true)
    return bounds
  }

  private fun checkText(text: String) {
    if (text.length > MAX_TEXT_LENGTH) {
      throw RenderError("PRINTER_RENDER_INVALID", "Text is too long: ${text.length}")
    }
  }

  // 阈值与现有标签位图 isBlack 一致：亮度 < 200 记为黑；透明视为白。
  private fun isBlack(pixel: Int): Boolean {
    if (Color.alpha(pixel) == 0) return false
    val luminance = (Color.red(pixel) * 299 + Color.green(pixel) * 587 + Color.blue(pixel) * 114) / 1000
    return luminance < 200
  }

  companion object {
    const val RENDER_VERSION = 1
    const val MAX_BATCH = 64
    const val MAX_BITMAP_WIDTH = 2048
    const val MAX_BITMAP_HEIGHT = 1024
    const val MAX_TEXT_LENGTH = 512
    const val MAX_FONT_SIZE = 600f
    const val MAX_PADDING = 64

    /** 内置字体键 → assets/printer-fonts 下的文件名；与 iOS 端、JS 端的键保持一致。 */
    val BUNDLED_FONTS = linkedMapOf(
      "roboto-regular" to "HbPrinter-Roboto-Regular.ttf",
      "roboto-light" to "HbPrinter-Roboto-Light.ttf",
      "roboto-medium" to "HbPrinter-Roboto-Medium.ttf",
      "roboto-bold" to "HbPrinter-Roboto-Bold.ttf",
      "roboto-black" to "HbPrinter-Roboto-Black.ttf",
      // 安卓旧排版的 sans-serif-condensed 加粗（大折扣标签的原价 / 省下金额 / %）。
      "roboto-condensed-bold" to "HbPrinter-RobotoCondensed-Bold.ttf",
    )
  }
}
