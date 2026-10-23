import assert from 'node:assert/strict'
import { readdirSync, readFileSync } from 'node:fs'
import { resolve } from 'node:path'

import { HOLIDAY_TYPES, OPTIONAL_MAPPING_KEYS, REQUIRED_MAPPING_KEYS } from './supplierPreorderAdmin.logic'

// 管理端与下单人员授权页的文案都在页面级消息文件里（随页面懒注册）。键名写错不会报错、界面会直接显示原始键名，
// 所以这里静态核对：中英文键集合一致；源码里出现的字面量键都存在；模板键（按枚举拼接）每个枚举值都有文案。
// 需在 apps/web 目录下运行（与其他 web 单测一致）。

type Json = { [key: string]: Json | string }

function readJson(path: string): Json {
  return JSON.parse(readFileSync(resolve(path), 'utf8')) as Json
}

function flattenKeys(resource: Json, prefix = ''): string[] {
  return Object.entries(resource).flatMap(([key, value]) =>
    typeof value === 'string' ? [`${prefix}${key}`] : flattenKeys(value, `${prefix}${key}.`),
  )
}

function stripComments(source: string) {
  return source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:'"`])\/\/[^\n]*/g, '$1')
}

const pages = [
  { directory: 'src/pages/SupplierPreorders/Admin', namespace: 'spAdmin', file: 'spAdminMessages' },
  { directory: 'src/pages/SupplierPreorders/Orderers', namespace: 'spOrderers', file: 'spOrderersMessages' },
]

const allKeys = new Set<string>()
for (const page of pages) {
  const zh = flattenKeys(readJson(`${page.directory}/${page.file}.zh.json`)).sort()
  const en = flattenKeys(readJson(`${page.directory}/${page.file}.en.json`)).sort()
  const onlyZh = zh.filter((key) => !en.includes(key))
  const onlyEn = en.filter((key) => !zh.includes(key))
  assert.deepEqual([onlyZh, onlyEn], [[], []], `${page.file} 中英文键集合不一致`)
  assert.ok(zh.length > 0, `${page.file} 为空`)
  assert.ok(zh.every((key) => key.startsWith(`${page.namespace}.`)), `${page.file} 的键必须都在 ${page.namespace} 命名空间下`)
  zh.forEach((key) => allKeys.add(key))
}

// 源码里的字面量键（两个目录的源码可以互相引用对方命名空间，例如授权页复用操作记录表）
const missing: string[] = []
for (const page of pages) {
  const sourceNames = readdirSync(resolve(page.directory)).filter((name) => /\.(ts|tsx)$/.test(name) && !name.endsWith('.test.ts'))
  assert.ok(sourceNames.includes('index.tsx'), `没有扫描到 ${page.directory}/index.tsx`)
  for (const name of sourceNames) {
    const text = stripComments(readFileSync(resolve(page.directory, name), 'utf8'))
    for (const match of text.matchAll(/['"]((?:spAdmin|spOrderers)\.[A-Za-z0-9_.]+)['"]/g)) {
      if (!allKeys.has(match[1])) missing.push(`${name}: ${match[1]}`)
    }
  }
}
assert.deepEqual(missing, [], '源码引用了不存在的文案键')

// 模板键：按枚举值拼接的每一项都必须有文案
const templateKeys = [
  ...HOLIDAY_TYPES.map((value) => `spAdmin.common.holiday.${value}`),
  ...['Draft', 'Open', 'Closed'].map((value) => `spAdmin.common.status.${value}`),
  ...['None', 'Draft', 'Submitted'].map((value) => `spAdmin.common.orderStatus.${value}`),
  ...['Barcode', 'ItemNumber', 'PrevYearItemCode', 'None'].map((value) => `spAdmin.common.matchMethod.${value}`),
  ...['Matched', 'New', 'Warning', 'Invalid'].map((value) => `spAdmin.common.previewStatus.${value}`),
  ...[...REQUIRED_MAPPING_KEYS, ...OPTIONAL_MAPPING_KEYS].map((value) => `spAdmin.mapping.${value}`),
  ...['cartons', 'pieces', 'amount'].map((value) => `spAdmin.matrix.unit.${value}`),
  ...['StatusChanged', 'FormDeleted', 'StoresUpdated', 'StoresCopied', 'OrdererAdded', 'OrdererUpdated', 'OrdererRemoved'].map(
    (value) => `spAdmin.logs.action.${value}`,
  ),
]
assert.deepEqual(
  templateKeys.filter((key) => !allKeys.has(key)),
  [],
  '枚举拼接的文案键缺失',
)
