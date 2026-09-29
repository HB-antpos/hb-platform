export const MAX_ITEM_NUMBERS = 3000
export const MAX_FILE_SIZE_BYTES = 5 * 1024 * 1024

export interface ImportIssue {
  row: number
  value: string
  reason: string
}

export interface ImportResult {
  itemNumbers: string[]
  sourceRowCount: number
  duplicateCount: number
  emptyRowCount: number
  issues: ImportIssue[]
}

type SourceRow = {
  row: number
  values: string[]
  issue?: Omit<ImportIssue, 'row'>
}

const ITEM_NUMBER_HEADERS = new Set([
  '货号', '商品货号', '产品货号', '货品编号', '商品编号', '商品编码', '产品编码',
  'item number', 'item no', 'item #', 'itemnumber', 'itemno', 'sku',
  'product code', 'product number', 'product no',
])

const MULTIPLE_COLUMNS_REASON = '仅支持单列货号'
const TOO_MANY_ITEMS_REASON = `最多可导入 ${MAX_ITEM_NUMBERS} 个货号`

function createEmptyResult(): ImportResult {
  return { itemNumbers: [], sourceRowCount: 0, duplicateCount: 0, emptyRowCount: 0, issues: [] }
}

function normalizeHeader(value: string): string {
  return value.trim().replace(/\s+/g, ' ').toLowerCase()
}

function isItemNumberHeader(value: string): boolean {
  return ITEM_NUMBER_HEADERS.has(normalizeHeader(value))
}

function normalizeItemNumber(value: string): string {
  return value.trim().toLowerCase()
}

function buildImportResult(rows: SourceRow[]): ImportResult {
  const result = createEmptyResult()
  const seenItemNumbers = new Set<string>()
  let firstNonEmptyRowSeen = false

  result.sourceRowCount = rows.length

  rows.forEach((sourceRow) => {
    const nonEmptyValues = sourceRow.values.map((value) => value.trim()).filter(Boolean)
    if (sourceRow.issue) {
      result.issues.push({ row: sourceRow.row, ...sourceRow.issue })
      return
    }
    if (!nonEmptyValues.length) {
      result.emptyRowCount += 1
      return
    }

    if (!firstNonEmptyRowSeen) {
      firstNonEmptyRowSeen = true
      if (nonEmptyValues.length === 1 && isItemNumberHeader(nonEmptyValues[0])) {
        return
      }
    }

    // Excel 复制单列时可能附带空的尾部 tab；只以实际非空单元格判断是否多列。
    if (nonEmptyValues.length !== 1) {
      result.issues.push({
        row: sourceRow.row,
        value: nonEmptyValues.join('\t'),
        reason: MULTIPLE_COLUMNS_REASON,
      })
      return
    }

    const itemNumber = nonEmptyValues[0]
    const normalized = normalizeItemNumber(itemNumber)
    if (seenItemNumbers.has(normalized)) {
      result.duplicateCount += 1
      return
    }

    if (result.itemNumbers.length >= MAX_ITEM_NUMBERS) {
      result.issues.push({ row: sourceRow.row, value: itemNumber, reason: TOO_MANY_ITEMS_REASON })
      return
    }

    seenItemNumbers.add(normalized)
    result.itemNumbers.push(itemNumber)
  })

  return result
}

function splitTextLines(text: string): string[] {
  const lines = text.replace(/^\uFEFF/, '').replace(/\r\n?/g, '\n').split('\n')
  if (lines.length === 1 && lines[0] === '') {
    return []
  }
  if (lines[lines.length - 1] === '') {
    lines.pop()
  }
  return lines
}

/** 解析直接粘贴的文本；带 tab 或逗号的行必须提示用户改用单列内容。 */
export function parsePastedItemNumbers(text: string): ImportResult {
  const rows = splitTextLines(text).map((line, index) => {
    const values = line.split(/\t|,/)
    return { row: index + 1, values }
  })
  return buildImportResult(rows)
}

interface ParsedCsv {
  rows: string[][]
  malformedRows: Map<number, string>
}

/**
 * 仅实现 CSV 的文本语法（含 BOM、CRLF、引号和双引号转义），不将内容交给公式或表格程序执行。
 */
function parseCsv(text: string): ParsedCsv {
  const source = text.replace(/^\uFEFF/, '')
  const rows: string[][] = []
  let row: string[] = []
  let field = ''
  let inQuotes = false
  let quotedFieldClosed = false
  let rowNumber = 1
  const malformedRows = new Map<number, string>()

  for (let index = 0; index < source.length; index += 1) {
    const character = source[index]
    if (inQuotes) {
      if (character === '"') {
        if (source[index + 1] === '"') {
          field += '"'
          index += 1
        } else {
          inQuotes = false
          quotedFieldClosed = true
        }
      } else {
        field += character
      }
      continue
    }

    if (quotedFieldClosed && character !== ',' && character !== '\r' && character !== '\n') {
      // RFC 4180 中关闭引号后只能是分隔符或行结束；不能悄悄把 "001"x 改成 001x。
      if (!/\s/.test(character)) {
        malformedRows.set(rowNumber, 'CSV 关闭引号后包含非法字符')
        field += character
      }
      continue
    }
    if (character === '"' && field === '') {
      inQuotes = true
      continue
    }
    if (character === ',') {
      row.push(field)
      field = ''
      quotedFieldClosed = false
      continue
    }
    if (character === '\r' || character === '\n') {
      if (character === '\r' && source[index + 1] === '\n') {
        index += 1
      }
      row.push(field)
      rows.push(row)
      row = []
      field = ''
      quotedFieldClosed = false
      rowNumber += 1
      continue
    }
    field += character
  }

  // 末尾换行不会再虚构一条空记录；只有仍有字段或列时才写入最后一行。
  if (field !== '' || row.length > 0 || source.length > 0 && !/[\r\n]$/.test(source)) {
    row.push(field)
    rows.push(row)
  }

  if (inQuotes) {
    malformedRows.set(rowNumber, 'CSV 引号未闭合')
  }
  return { rows, malformedRows }
}

export function parseCsvItemNumbers(text: string): ImportResult {
  const parsed = parseCsv(text)
  const rows: SourceRow[] = parsed.rows.map((values, index) => ({
    row: index + 1,
    values,
    ...(parsed.malformedRows.has(index + 1)
      ? { issue: { value: values.join(','), reason: parsed.malformedRows.get(index + 1)! } }
      : {}),
  }))
  return buildImportResult(rows)
}

function numericItemNumber(value: number, numFmt?: string): string {
  if (!Number.isFinite(value) || !Number.isInteger(value) || !numFmt) {
    return String(value)
  }

  // 只匹配纯 0 占位格式，避免把金额、日期等格式化数值误当成货号。
  const firstFormatSection = numFmt.split(';')[0]?.replace(/^\[[^\]]+\]/, '').trim()
  if (!firstFormatSection || !/^0+$/.test(firstFormatSection)) {
    return String(value)
  }

  const sign = value < 0 ? '-' : ''
  return `${sign}${String(Math.abs(value)).padStart(firstFormatSection.length, '0')}`
}

function richTextValue(value: unknown): string | undefined {
  if (!value || typeof value !== 'object' || !('richText' in value)) {
    return undefined
  }
  const richText = (value as { richText?: Array<{ text?: unknown }> }).richText
  if (!Array.isArray(richText)) {
    return undefined
  }
  return richText.map((part) => typeof part.text === 'string' ? part.text : '').join('')
}

function cellValueToText(value: unknown, numFmt?: string): { text: string; issue?: Omit<ImportIssue, 'row'> } {
  if (value === null || value === undefined) {
    return { text: '' }
  }
  if (typeof value === 'string') {
    return { text: value }
  }
  if (typeof value === 'number') {
    if (!Number.isSafeInteger(value)) {
      return { text: String(value), issue: { value: String(value), reason: '数值货号超出安全整数范围' } }
    }
    return { text: numericItemNumber(value, numFmt) }
  }
  if (typeof value === 'boolean') {
    return { text: String(value) }
  }

  const richText = richTextValue(value)
  if (richText !== undefined) {
    return { text: richText }
  }

  if (value instanceof Date) {
    return { text: value.toISOString(), issue: { value: value.toISOString(), reason: '不支持日期单元格作为货号' } }
  }

  if (typeof value === 'object' && ('formula' in value || 'sharedFormula' in value)) {
    const cachedResult = (value as { result?: unknown }).result
    if (cachedResult === undefined || cachedResult === null) {
      return { text: '', issue: { value: '', reason: '公式没有可用的缓存结果' } }
    }
    // 只读取工作簿已保存的缓存值，绝不执行公式。
    return cellValueToText(cachedResult, numFmt)
  }

  if (typeof value === 'object') {
    const record = value as Record<string, unknown>
    const reason = 'error' in record
      ? '不支持错误单元格作为货号'
      : 'hyperlink' in record
        ? '不支持超链接单元格作为货号'
        : '不支持的单元格类型'
    return { text: '', issue: { value: '', reason } }
  }

  return { text: '', issue: { value: String(value), reason: '不支持的单元格类型' } }
}

async function readXlsxItemNumbers(file: File): Promise<ImportResult> {
  const { default: ExcelJS } = await import('exceljs')
  const workbook = new ExcelJS.Workbook()
  await workbook.xlsx.load(await file.arrayBuffer())
  const worksheet = workbook.worksheets[0]
  if (!worksheet) {
    return { ...createEmptyResult(), issues: [{ row: 0, value: file.name, reason: '工作簿没有工作表' }] }
  }

  const rows: SourceRow[] = []
  for (let rowIndex = 1; rowIndex <= worksheet.rowCount; rowIndex += 1) {
    const worksheetRow = worksheet.getRow(rowIndex)
    const values: string[] = []
    let issue: Omit<ImportIssue, 'row'> | undefined
    worksheetRow.eachCell({ includeEmpty: true }, (cell) => {
      const converted = cellValueToText(cell.value, cell.numFmt)
      values.push(converted.text)
      issue ??= converted.issue
    })
    rows.push({ row: rowIndex, values, ...(issue ? { issue } : {}) })
  }
  return buildImportResult(rows)
}

export async function readItemNumberFile(file: File): Promise<ImportResult> {
  if (file.size > MAX_FILE_SIZE_BYTES) {
    return { ...createEmptyResult(), issues: [{ row: 0, value: file.name, reason: `文件不能超过 ${MAX_FILE_SIZE_BYTES / 1024 / 1024}MB` }] }
  }

  const fileName = file.name.toLowerCase()
  if (fileName.endsWith('.csv')) {
    return parseCsvItemNumbers(await file.text())
  }
  if (!fileName.endsWith('.xlsx')) {
    return { ...createEmptyResult(), issues: [{ row: 0, value: file.name, reason: '仅支持 .csv 或 .xlsx 文件' }] }
  }

  try {
    return await readXlsxItemNumbers(file)
  } catch {
    return { ...createEmptyResult(), issues: [{ row: 0, value: file.name, reason: '无法读取 XLSX 文件' }] }
  }
}
