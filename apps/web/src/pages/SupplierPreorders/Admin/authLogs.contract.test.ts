import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

import { AUTH_LOG_ACTIONS } from './supplierPreorderAdmin.logic'

// 订单表「操作记录」：订货数量变更与提交订单的前后端约定（需在 apps/web 目录下运行，与其他 web 单测一致）。
const read = (path: string) => readFileSync(resolve(path), 'utf8')

// 1) 动作名 / 范围名必须与后端常量一致（后端写入什么，前端才认得出什么）
const backendModels = read('../../services/backend/BlazorApp.Shared/Models/HBweb/SupplierPreorderModels.cs')
for (const [name, value] of [
  ['LineChanged', 'LineChanged'],
  ['OrderSubmitted', 'OrderSubmitted'],
] as const) {
  assert.ok(backendModels.includes(`public const string ${name} = "${value}";`), `后端缺少动作常量 ${name}`)
  assert.ok((AUTH_LOG_ACTIONS as readonly string[]).includes(value), `前端动作清单缺少 ${value}（界面会直接显示英文原文）`)
}
assert.ok(backendModels.includes('public const string Order = "Order";'), '后端缺少 Scope = Order 常量')
// 后端 WriteAuthLogAsync 里写的管理类动作名，前端也都要有文案
const backendWrites = [...read('../../services/backend/BlazorApp.Api/Services/React/SupplierPreorders/SupplierPreorderService.Forms.cs').matchAll(/WriteAuthLogAsync\("FormStore", (?:[a-z]+ \? )?"(\w+)"/g)].map((match) => match[1])
for (const action of backendWrites) assert.ok((AUTH_LOG_ACTIONS as readonly string[]).includes(action), `管理类动作 ${action} 缺少前端文案`)

// 2) 前端：详情页「操作记录」页签用带视图切换的面板；表格把 scope / keyword 传给接口；授权页不传 scope（仍是管理类记录）
assert.ok(read('src/pages/SupplierPreorders/Admin/FormDetail.tsx').includes('<FormLogsPanel formGuid={formGuid}'), '详情页必须用 FormLogsPanel')
const panel = read('src/pages/SupplierPreorders/Admin/FormLogsPanel.tsx')
assert.ok(panel.includes("scope={view === 'orders' ? 'Order' : undefined}"), '只有「订货数量」视图才传 scope = Order，默认仍是管理类记录')
assert.ok(panel.includes("keyword={view === 'orders' ? keyword : undefined}"), '关键字只在「订货数量」视图里生效')
assert.ok(read('src/pages/SupplierPreorders/Admin/AuthLogsTable.tsx').includes('formGuid, userGuid, scope, keyword'), '表格必须把 scope / keyword 传给接口')
assert.ok(!/<AuthLogsTable[^>]*scope=/.test(read('src/pages/SupplierPreorders/Orderers/index.tsx')), '下单人员授权页不传 scope，保持只看管理类记录')
