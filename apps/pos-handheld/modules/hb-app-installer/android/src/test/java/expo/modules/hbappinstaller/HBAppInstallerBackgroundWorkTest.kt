package expo.modules.hbappinstaller

import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class HBAppInstallerBackgroundWorkTest {
  /** 模拟 Expo 的 expo.modules.AsyncFunctionQueue：所有模块的异步函数共用这一条线程。 */
  private fun sharedQueue() = Executors.newSingleThreadExecutor().asCoroutineDispatcher()

  @Test fun `a long download no longer blocks other async functions on the shared queue`() {
    runBlocking {
      sharedQueue().use { queue ->
        val scope = CoroutineScope(queue + SupervisorJob())
        val started = CountDownLatch(1)
        val release = CountDownLatch(1)
        val download = scope.async {
          runOffModulesQueue {
            started.countDown()
            release.await()
            "downloaded"
          }
        }
        assertTrue(started.await(5, TimeUnit.SECONDS))

        // 下载还没结束时，同一队列上的「读收银员授权」（SecureStore）必须立即返回。
        assertEquals("authorization", withTimeout(2_000) { scope.async { "authorization" }.await() })

        release.countDown()
        assertEquals("downloaded", withTimeout(5_000) { download.await() })
      }
    }
  }

  @Test fun `blocking directly on the shared queue is exactly the bug being guarded against`() {
    runBlocking {
      sharedQueue().use { queue ->
        val scope = CoroutineScope(queue + SupervisorJob())
        val started = CountDownLatch(1)
        val release = CountDownLatch(1)
        scope.launch {
          started.countDown()
          release.await()
        }
        assertTrue(started.await(5, TimeUnit.SECONDS))

        val blocked = try {
          withTimeout(300) { scope.async { "authorization" }.await() }
          false
        } catch (error: TimeoutCancellationException) {
          true
        }
        release.countDown()
        assertTrue("对照：下载直接阻塞共享队列时，读收银员授权会一直等到下载结束", blocked)
      }
    }
  }

  @Test fun `cancelling interrupts the blocking work so the download loop can stop`() {
    runBlocking {
      val started = CountDownLatch(1)
      val interrupted = CountDownLatch(1)
      val job = launch(Dispatchers.Default) {
        runOffModulesQueue {
          started.countDown()
          try {
            Thread.sleep(10_000)
          } catch (error: InterruptedException) {
            interrupted.countDown()
            throw error
          }
        }
      }
      assertTrue(started.await(5, TimeUnit.SECONDS))

      // 中断后下载循环按「已取消」收尾并清理临时文件，见 HBAppInstallerDownloaderTest 的取消用例。
      job.cancelAndJoin()
      assertTrue("取消协程（如应用上下文销毁）须中断下载线程", interrupted.await(5, TimeUnit.SECONDS))
    }
  }
}
