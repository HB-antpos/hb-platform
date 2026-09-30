import assert from 'node:assert/strict'

import { toProductImagePreviewUrl, toProductThumbnailUrl } from './productImageThumbnail'
import {
  PRODUCT_IMAGE_VERSION_BATCH_SIZE,
  PRODUCT_IMAGE_VERSION_TTL_MS,
  getProductImageVersion,
  getProductImageVersionsRevision,
  requestProductImageVersion,
  resetProductImageVersionsForTest,
  subscribeProductImageVersions,
} from './productImageVersions'

const replaced = 'https://hotbargain-yw-2023-1300114625.cos.ap-shanghai.myqcloud.com/YW200/HB312-003.jpg'
const untouched = 'https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/250/WGC1098.jpg'
const external = 'https://www.malmar.com.au/images/products/DOGPOWS.jpg'

const waitForFlush = () => new Promise((resolve) => setTimeout(resolve, 60))

// —— 地址拼接：换过的图追加版本参数，没版本号时与原先完全一致 ——
assert.equal(
  toProductThumbnailUrl(replaced, undefined, '1790000000'),
  `${replaced}?imageMogr2/thumbnail/72x72/format/webp&v=1790000000`,
  '换过的 COS 图缩略图应在图片处理参数后追加 &v=',
)
assert.equal(
  toProductThumbnailUrl(replaced),
  `${replaced}?imageMogr2/thumbnail/72x72/format/webp`,
  '没有版本号时缩略图地址不变，继续命中已有缓存',
)
assert.equal(toProductThumbnailUrl(external, undefined, '1'), external, '外链图片不追加版本参数')
assert.equal(toProductImagePreviewUrl(replaced, '1790000000'), `${replaced}?v=1790000000`, '预览原图也换缓存键')
assert.equal(toProductImagePreviewUrl(replaced), replaced, '没有版本号时预览地址不变')
assert.equal(toProductImagePreviewUrl(external, '1'), external, '外链预览地址不变')

// —— 同一轮渲染的地址合成一次请求，只查 COS 图，结果通知订阅方 ——
{
  const calls: string[][] = []
  resetProductImageVersionsForTest({
    fetcher: async (urls) => {
      calls.push(urls)
      return { [replaced]: '1790000000' }
    },
  })
  let notified = 0
  subscribeProductImageVersions(() => {
    notified += 1
  })

  requestProductImageVersion(replaced)
  requestProductImageVersion(untouched)
  requestProductImageVersion(replaced)
  requestProductImageVersion(external)
  requestProductImageVersion(undefined)
  await waitForFlush()

  assert.deepEqual(calls, [[replaced, untouched]], '一轮渲染只发一个请求、去重且跳过外链')
  assert.equal(getProductImageVersion(replaced), '1790000000')
  assert.equal(getProductImageVersion(untouched), undefined, '没改过的图没有版本号')
  assert.equal(notified, 1, '拿到新版本号后通知图片组件重新渲染')
  assert.equal(getProductImageVersionsRevision(), 1, '整页表格靠修订计数感知版本号变化')

  // 10 分钟内重复渲染不再查询。
  requestProductImageVersion(replaced)
  await waitForFlush()
  assert.equal(calls.length, 1, '近期查过的地址不重复查询')
}

// —— 超过有效期后重新查询，能拿到之后又换过一次的新版本号 ——
{
  let clock = 1_000_000
  let serverVersion = '1'
  const calls: string[][] = []
  resetProductImageVersionsForTest({
    now: () => clock,
    fetcher: async (urls) => {
      calls.push(urls)
      return { [replaced]: serverVersion }
    },
  })

  requestProductImageVersion(replaced)
  await waitForFlush()
  serverVersion = '2'
  clock += PRODUCT_IMAGE_VERSION_TTL_MS
  requestProductImageVersion(replaced)
  await waitForFlush()

  assert.equal(calls.length, 2, '过期后重新查询')
  assert.equal(getProductImageVersion(replaced), '2', '再次换图后版本号随之更新')
}

// —— 查询失败不影响显示，下次渲染允许重试 ——
{
  let attempts = 0
  resetProductImageVersionsForTest({
    fetcher: async () => {
      attempts += 1
      if (attempts === 1) {
        throw new Error('network down')
      }
      return { [replaced]: '9' }
    },
  })

  requestProductImageVersion(replaced)
  await waitForFlush()
  assert.equal(getProductImageVersion(replaced), undefined, '失败时没有版本号，按原地址显示')

  requestProductImageVersion(replaced)
  await waitForFlush()
  assert.equal(attempts, 2, '失败的地址下次渲染应重新查询')
  assert.equal(getProductImageVersion(replaced), '9')
}

// —— 超过后端单次上限时拆成多批 ——
{
  const batchSizes: number[] = []
  resetProductImageVersionsForTest({
    fetcher: async (urls) => {
      batchSizes.push(urls.length)
      return {}
    },
  })

  for (let index = 0; index < PRODUCT_IMAGE_VERSION_BATCH_SIZE + 5; index += 1) {
    requestProductImageVersion(`https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/250/${index}.jpg`)
  }
  await waitForFlush()

  assert.deepEqual(batchSizes, [PRODUCT_IMAGE_VERSION_BATCH_SIZE, 5], '按后端上限分批请求')
}

resetProductImageVersionsForTest()
console.log('productImageVersions tests passed')
