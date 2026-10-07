package expo.modules.hbappinstaller

internal const val DOWNLOAD_PROGRESS_EVENT = "onDownloadProgress"
private const val DEFAULT_PROGRESS_INTERVAL_MILLIS = 200L

/** 下载线程同步回调；实现方只做轻量转发，不得阻塞。 */
internal fun interface ApkDownloadProgressListener {
  fun onProgress(bytesWritten: Long, totalBytes: Long)
}

internal fun monotonicMillis(): Long = System.nanoTime() / 1_000_000L

/**
 * 下载进度节流：开始传输时先报 0，之后百分比前进且距上次至少 [minIntervalMillis] 才报，
 * 写满时必报一次。安卓 10 手持机性能有限，节流后一次下载最多约百次跨线程事件。
 * 上报失败（事件未声明、JS 运行时已销毁等）只丢弃这一帧进度，绝不中断下载。
 */
internal class ApkDownloadProgressReporter(
  private val totalBytes: Long,
  private val listener: ApkDownloadProgressListener?,
  private val elapsedMillis: () -> Long,
  private val minIntervalMillis: Long = DEFAULT_PROGRESS_INTERVAL_MILLIS,
) {
  private var lastPercent = -1
  private var lastReportedAt = 0L

  fun report(bytesWritten: Long) {
    val target = listener ?: return
    if (totalBytes <= 0L) return
    val written = bytesWritten.coerceIn(0L, totalBytes)
    val percent = (written * 100L / totalBytes).toInt()
    val now = elapsedMillis()
    val complete = written == totalBytes
    if (lastPercent >= 0) {
      if (complete && lastPercent == 100) return
      if (!complete && (percent <= lastPercent || now - lastReportedAt < minIntervalMillis)) return
    }
    lastPercent = percent
    lastReportedAt = now
    try {
      target.onProgress(written, totalBytes)
    } catch (ignored: Exception) {
      // 进度只是展示信息；下载结果仍以大小与 SHA-256 校验为准。
    }
  }
}
