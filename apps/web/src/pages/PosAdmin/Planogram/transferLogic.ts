import type { PlanogramExportDto, PlanogramImportUnresolvedItem } from '../../../types/planogram'
import { PLANOGRAM_EXPORT_FORMAT_VERSION } from '../../../types/planogram'
import { sanitizeFileNamePart } from '../../Warehouse/StoreOrders/printUtils'
import { normalizeExportFile } from '../../../services/planogramService'

/** 方案 JSON 导入 / 导出的纯逻辑（文件解析、大小限制、文件名、重名判断、缺失编码分组），不碰 DOM / 请求。 */

/** 导入文件大小上限：明细最多 2000 条，正常文件不到 1 MB；超过 5 MB 肯定不是导出文件。 */
export const MAX_IMPORT_BYTES = 5 * 1024 * 1024

export type ImportParseFailure =
  /** 文件超过 5 MB。 */
  | { ok: false; reason: 'tooLarge' }
  /** 空文件。 */
  | { ok: false; reason: 'empty' }
  /** 不是合法 JSON。 */
  | { ok: false; reason: 'notJson' }
  /** 是 JSON，但不像商品陈列图导出文件（缺方案头 / 货架编码 / 明细数组）。 */
  | { ok: false; reason: 'notPlanogram' }

export type ImportParseResult = { ok: true; file: PlanogramExportDto } | ImportParseFailure

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

/**
 * 解析用户选的文件内容：先看大小，再解析 JSON，再核对「像不像导出文件」（plan 对象、shelfModelCode 字符串、items 数组）。
 * formatVersion 认不认识、编码能不能解析、字段是否合法都交给后端校验并按错误码提示，这里不猜。
 */
export function parseImportFile(text: string, sizeBytes: number): ImportParseResult {
  if (sizeBytes > MAX_IMPORT_BYTES) return { ok: false, reason: 'tooLarge' }
  // 去掉 BOM：Windows 记事本另存的 UTF-8 文件开头会带它，JSON.parse 不认
  const clean = text.replace(/^﻿/, '')
  if (!clean.trim()) return { ok: false, reason: 'empty' }
  let parsed: unknown
  try {
    parsed = JSON.parse(clean)
  } catch {
    return { ok: false, reason: 'notJson' }
  }
  if (!isRecord(parsed) || !isRecord(parsed.plan) || typeof parsed.shelfModelCode !== 'string' || !Array.isArray(parsed.items)) {
    return { ok: false, reason: 'notPlanogram' }
  }
  return { ok: true, file: normalizeExportFile(parsed as Partial<PlanogramExportDto>) }
}

/** 文件里的格式版本是不是当前支持的（用于在弹窗里提前提示；真正的校验仍在后端）。 */
export function isSupportedFormatVersion(file: Pick<PlanogramExportDto, 'formatVersion'>): boolean {
  return file.formatVersion === PLANOGRAM_EXPORT_FORMAT_VERSION
}

/** 下载文件名：<方案名>.planogram.json（非法字符换成 _；名称为空回退到 planogram）。 */
export function exportJsonFileName(planName: string): string {
  const base = sanitizeFileNamePart(planName) || 'planogram'
  return `${base}.planogram.json`
}

/** 导出文件内容：缩进 2 个空格，便于人工查看 / 版本对比。 */
export function stringifyExportFile(file: PlanogramExportDto): string {
  return `${JSON.stringify(file, null, 2)}\n`
}

function normalizeName(name: string): string {
  return name.trim().toLowerCase()
}

/** 方案名称是否与已有方案重名（忽略首尾空格与大小写）。后端不限制重名，这里是为了避免列表里出现两个一模一样的方案。 */
export function isDuplicatePlanName(name: string, existingNames: readonly string[]): boolean {
  const target = normalizeName(name)
  return target.length > 0 && existingNames.some((existing) => normalizeName(existing) === target)
}

/** 重名时给一个默认的新名字：在原名后加「 (导入)」，仍重名就依次加序号，总长不超过 100。 */
export function suggestImportName(name: string, existingNames: readonly string[], suffix: string, maxLength = 100): string {
  const base = name.trim()
  // 先截原名再接后缀：名称已经很长时，整体截断会把后缀截没（等于没改名）
  let candidate = `${base.slice(0, Math.max(0, maxLength - suffix.length))}${suffix}`
  let counter = 2
  while (isDuplicatePlanName(candidate, existingNames) && counter < 1000) {
    const tail = `${suffix}${counter}`
    candidate = `${base.slice(0, Math.max(0, maxLength - tail.length))}${tail}`
    counter += 1
  }
  return candidate
}

export interface UnresolvedGroups {
  shelf: string[]
  hook: string[]
  product: string[]
}

/** PLANOGRAM_IMPORT_UNRESOLVED 的缺失编码按种类分组（货架型号 / 挂钩型号 / 商品货号），组内排序去重。 */
export function groupUnresolved(items: readonly PlanogramImportUnresolvedItem[]): UnresolvedGroups {
  const groups: UnresolvedGroups = { shelf: [], hook: [], product: [] }
  for (const item of items) {
    if (!groups[item.kind].includes(item.code)) groups[item.kind].push(item.code)
  }
  for (const list of [groups.shelf, groups.hook, groups.product]) list.sort((a, b) => a.localeCompare(b))
  return groups
}
