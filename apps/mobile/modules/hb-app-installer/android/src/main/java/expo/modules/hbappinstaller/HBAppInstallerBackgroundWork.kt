package expo.modules.hbappinstaller

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runInterruptible

/**
 * 把安装器的重活（下载 APK、整包 SHA-256、解析 APK）移出 Expo 共享异步队列。
 *
 * Expo 所有模块的 AsyncFunction 默认挤在同一条 `expo.modules.AsyncFunctionQueue` 线程上排队。
 * 下载若直接阻塞在这条线程上，几分钟内 SecureStore 读令牌等调用全被卡住——
 * 每个 API 请求的拦截器都要读令牌，于是扫码、登录都要等下载完成才有结果（10-07 真机实测）。
 *
 * 挪到 IO 线程后共享队列立即空出来；协程被取消（如应用上下文销毁）时中断工作线程，
 * 下载循环与限速补睡据此按「已取消」收尾。
 */
internal suspend fun <T> runOffModulesQueue(block: () -> T): T = runInterruptible(Dispatchers.IO) { block() }
