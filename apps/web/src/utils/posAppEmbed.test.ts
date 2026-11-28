import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import path from 'node:path'
import test from 'node:test'

import { isPosAppEmbed, POS_APP_EMBED_MODE, POS_APP_SCAN_EVENT, readPosAppScanBarcode } from './posAppEmbed'

// 测试在 apps/web 目录下运行（esbuild 打包后 import.meta.url 不再指向源码位置）。
const root = process.cwd()

test('只有 App 写入 pos-ipad 标记时才算嵌入模式', () => {
  const globalWithWindow = globalThis as unknown as { window?: Record<string, unknown> }
  const previous = globalWithWindow.window
  try {
    delete globalWithWindow.window
    assert.equal(isPosAppEmbed(), false)
    globalWithWindow.window = {}
    assert.equal(isPosAppEmbed(), false)
    globalWithWindow.window = { __HB_EMBED_MODE__: 'other' }
    assert.equal(isPosAppEmbed(), false)
    globalWithWindow.window = { __HB_EMBED_MODE__: POS_APP_EMBED_MODE }
    assert.equal(isPosAppEmbed(), true)
  } finally {
    globalWithWindow.window = previous
  }
})

test('扫码事件只接受非空字符串条码', () => {
  const event = (detail: unknown) => ({ detail }) as unknown as Event
  assert.equal(readPosAppScanBarcode(event({ barcode: ' 9312345678907 ' })), '9312345678907')
  assert.equal(readPosAppScanBarcode(event({ barcode: '  ' })), null)
  assert.equal(readPosAppScanBarcode(event({ barcode: 123 })), null)
  assert.equal(readPosAppScanBarcode(event(null)), null)
})

test('与大屏收银 App 约定的标记和事件名一致', () => {
  const appConfig = readFileSync(
    path.join(root, '../pos-ipad/src/features/ordering/ordering-config.ts'),
    'utf8',
  )
  assert.match(appConfig, new RegExp(`ORDERING_EMBED_MODE = "${POS_APP_EMBED_MODE}"`))
  assert.match(appConfig, new RegExp(`ORDERING_SCAN_EVENT = "${POS_APP_SCAN_EVENT}"`))
})

test('嵌入模式下订货前台隐藏退出、后台入口与插件推广，并接收 App 扫码', () => {
  const layout = readFileSync(path.join(root, 'src/layout/ShopLayout.tsx'), 'utf8')
  assert.match(layout, /const posAppEmbed = useMemo\(\(\) => isPosAppEmbed\(\), \[\]\)/)
  assert.match(layout, /\{posAppEmbed \? null : currentUser \? \(/)
  assert.match(layout, /currentUser && !posAppEmbed \? \(/)
  assert.match(layout, /access\.canAccessDashboard && !posAppEmbed \? \(/)
  assert.equal((layout.match(/!posAppEmbed\s*\n?\s*\? <SupplierOrderingExtensionEntry/g) ?? []).length, 2)
  assert.match(layout, /window\.addEventListener\(POS_APP_SCAN_EVENT, onScan\)/)
})
