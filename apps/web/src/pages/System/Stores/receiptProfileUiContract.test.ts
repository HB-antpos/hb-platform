import assert from 'node:assert/strict'
import { readFileSync, readdirSync, statSync } from 'node:fs'
import { join, relative, resolve } from 'node:path'

// 分店管理页「小票下发」的源码契约：Web 没有 jsdom，组件交互测不了，
// 这里锁住几条容易被后续改动悄悄破坏、且代价高的约束（权限入口、状态加载不阻塞列表、首屏体积）。

const pageSource = readFileSync(resolve('src/pages/System/Stores/index.tsx'), 'utf8')

// 1) 状态异步补充：列表请求成功回调里只能「发起」状态请求，不能 await，否则会拖慢列表首屏。
assert.ok(
  pageSource.includes('void refreshReceiptStatuses(result.items.map((item) => item.storeGUID))'),
  '列表加载成功后应异步（void）请求当前页分店的小票下发状态',
)
assert.ok(!/await\s+refreshReceiptStatuses/.test(pageSource), '不得 await 状态请求：它不能阻塞列表渲染')

// 2) 下发入口只给有 Stores.Edit 的用户：勾选操作条里的按钮在 HasPermission(Stores.Edit) 内，行内「更多」菜单项在 canEditStores 分支内。
const selectionBar = pageSource.slice(
  pageSource.indexOf('<SelectionActionBar'),
  pageSource.indexOf('</SelectionActionBar>'),
)
assert.ok(selectionBar.includes('<HasPermission code={P.Stores.Edit}>'), '批量下发按钮必须包在 Stores.Edit 权限内')
assert.ok(selectionBar.includes("t('system.stores.receiptProfile.publishAction')"), '勾选操作条应提供「下发小票资料」')

const menuStart = pageSource.indexOf('...(canEditStores')
const menuEnd = pageSource.indexOf("key: 'users'")
assert.ok(menuStart > 0 && menuEnd > menuStart, '应能定位「更多」菜单的编辑权限分支')
assert.ok(pageSource.slice(menuStart, menuEnd).includes("key: 'publishReceipt'"), '单店下发菜单项必须在 canEditStores 分支内')

// 3) 无编辑权限的用户仍能看到状态列（只读），所以状态列不能放进权限分支。
assert.ok(
  pageSource.includes("key: 'receiptProfile'") && !pageSource.slice(pageSource.indexOf("key: 'receiptProfile'") - 200, pageSource.indexOf("key: 'receiptProfile'")).includes('canEditStores'),
  '小票下发状态列对所有能查看分店的用户可见',
)

// 4) 文案走页面级消息文件，不进全局语言包（首屏 gzip 预算很紧）。
for (const language of ['zh', 'en']) {
  const globalSource = readFileSync(resolve(`src/i18n/locales/${language}.json`), 'utf8')
  assert.ok(!globalSource.includes('receiptProfile'), `${language}.json 不应包含小票下发文案，应放在页面级消息文件`)
}

// 5) 下发服务只被分店管理页的懒加载代码块引用，不进首屏包。
function walk(directory: string): string[] {
  return readdirSync(directory).flatMap((name) => {
    const path = join(directory, name)
    return statSync(path).isDirectory() ? walk(path) : [path]
  })
}
const importers = walk(resolve('src'))
  .filter((path) => /\.(ts|tsx)$/.test(path) && !/\.test\.tsx?$/.test(path))
  .filter((path) => readFileSync(path, 'utf8').includes('storeReceiptProfileService'))
  .map((path) => relative(resolve('src'), path).split('\\').join('/'))
  .filter((path) => path !== 'services/storeReceiptProfileService.ts')
assert.ok(importers.length > 0, '应有页面引用下发服务')
assert.ok(
  importers.every((path) => path.startsWith('pages/System/Stores/')),
  `下发服务只能被分店管理页引用，实际引用方: ${importers.join(', ')}`,
)

// 6) 分店管理页仍是路由里的懒加载页面，新增的组件随它分包。
const routesSource = readFileSync(resolve('src/router/routes.tsx'), 'utf8')
assert.ok(
  routesSource.includes("lazy(() => import('../pages/System/Stores'))"),
  '分店管理页必须保持懒加载，下发相关组件才不会进入首屏包',
)

console.log('receiptProfileUiContract.test: ok')
