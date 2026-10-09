import {
  THUMBNAIL_MAX_WIDTH,
  buildThumbnailFileName,
  captureVideoThumbnail,
  computeThumbnailSize,
  createVideoThumbnailUrl,
  pickThumbnailTime,
} from './videoThumbnail'

function assertEqual(actual: unknown, expected: unknown, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)
  if (actualJson !== expectedJson) {
    throw new Error(`${message}. Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

// 抽帧时间点：长视频取第 1 秒；不足 2 秒取一半；时长未知/非法取首帧（0）
assertEqual(pickThumbnailTime(8), 1, 'Long videos should use the 1 second mark')
assertEqual(pickThumbnailTime(2), 1, 'A 2 second video should use the 1 second mark')
assertEqual(pickThumbnailTime(1), 0.5, 'Short videos should use half the duration')
assertEqual(pickThumbnailTime(0), 0, 'Zero duration should fall back to the first frame')
assertEqual(pickThumbnailTime(Number.NaN), 0, 'Unknown duration should fall back to the first frame')
assertEqual(pickThumbnailTime(Number.POSITIVE_INFINITY), 0, 'Infinite duration should fall back to the first frame')

// 等比缩放：超过最大宽度才缩小，保持宽高比；不放大；非法尺寸为 null
assertDeepEqual(computeThumbnailSize(1366, 768), { width: THUMBNAIL_MAX_WIDTH, height: 360 }, 'Landscape should scale to max width')
assertDeepEqual(computeThumbnailSize(772, 870), { width: THUMBNAIL_MAX_WIDTH, height: 721 }, 'Portrait wider than max should scale down')
assertDeepEqual(computeThumbnailSize(400, 300), { width: 400, height: 300 }, 'Small media must not be enlarged')
assertDeepEqual(computeThumbnailSize(10000, 1), { width: THUMBNAIL_MAX_WIDTH, height: 1 }, 'Height must never drop below 1')
assertEqual(computeThumbnailSize(0, 100), null, 'Zero width should be invalid')
assertEqual(computeThumbnailSize(undefined, 100), null, 'Missing width should be invalid')
assertEqual(computeThumbnailSize(100, null), null, 'Missing height should be invalid')

// 文件名：去扩展名加 -cover.jpg；无名或只有扩展名时兜底
assertEqual(buildThumbnailFileName('halloween-w3-glow-1366x768.mp4'), 'halloween-w3-glow-1366x768-cover.jpg', 'Cover name should reuse the video name')
assertEqual(buildThumbnailFileName('a.b.c.mov'), 'a.b.c-cover.jpg', 'Only the last extension should be removed')
assertEqual(buildThumbnailFileName('noext'), 'noext-cover.jpg', 'Names without extension should be kept')
assertEqual(buildThumbnailFileName('.mp4'), 'video-cover.jpg', 'Empty base name should fall back')

// Node 环境没有 window：抽帧与生成都必须安全返回 null，不抛错（浏览器里失败也走同一条路径）
const fakeFile = { name: 'clip.mp4', type: 'video/mp4', size: 1 } as File
assertEqual(await captureVideoThumbnail(fakeFile), null, 'Capture without a browser should resolve to null')
assertEqual(await createVideoThumbnailUrl(fakeFile), null, 'Thumbnail creation without a browser should resolve to null')

console.log('videoThumbnail.test: ok')
