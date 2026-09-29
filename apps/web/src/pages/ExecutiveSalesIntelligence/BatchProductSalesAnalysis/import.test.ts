import assert from 'node:assert/strict'
import ExcelJS from 'exceljs'
import {
  MAX_FILE_SIZE_BYTES,
  MAX_ITEM_NUMBERS,
  parseCsvItemNumbers,
  parsePastedItemNumbers,
  readItemNumberFile,
} from './import'

function createFile(name: string, content: string | Uint8Array, size = typeof content === 'string' ? new TextEncoder().encode(content).byteLength : content.byteLength): File {
  const bytes = typeof content === 'string' ? new TextEncoder().encode(content) : content
  return {
    name,
    size,
    text: async () => new TextDecoder().decode(bytes),
    arrayBuffer: async () => bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength) as ArrayBuffer,
  } as File
}

const pasted = parsePastedItemNumbers('货号\n00123\nabc\nABC\n\n  0007  \n')
assert.deepEqual(pasted.itemNumbers, ['00123', 'abc', '0007'], '文本导入必须保留前导零和首次展示值')
assert.equal(pasted.sourceRowCount, 6, '文本源行数应包括表头与空行，不包含末尾换行虚拟行')
assert.equal(pasted.duplicateCount, 1, '文本导入应忽略大小写重复货号')
assert.equal(pasted.emptyRowCount, 1, '空行应计入报告但不导入')

const pastedWithTrailingTab = parsePastedItemNumbers('SKU\t\nA-001\t\nSKU')
assert.deepEqual(pastedWithTrailingTab.itemNumbers, ['A-001', 'SKU'], '只有第一非空行的表头可忽略，尾部空 tab 不应误判多列')

const pastedWithColumns = parsePastedItemNumbers('A-001\t商品 A\nB-001,C')
assert.deepEqual(pastedWithColumns.itemNumbers, [], '粘贴多列内容不得静默选择第一列')
assert.equal(pastedWithColumns.issues.length, 2, '每个多列粘贴行都应报告问题')
assert.equal(pastedWithColumns.issues[0]?.reason, '仅支持单列货号', '多列问题应有明确原因')

const csv = parseCsvItemNumbers('\uFEFF货号\r\n"001""23"\r\n"A, B"\r\n\r\nabc\r\nABC\r\n')
assert.deepEqual(csv.itemNumbers, ['001"23', 'A, B', 'abc'], 'CSV 必须支持 BOM、CRLF、引号和双引号转义')
assert.equal(csv.emptyRowCount, 1, 'CSV 中的空记录必须忽略并计数')
assert.equal(csv.duplicateCount, 1, 'CSV 导入必须大小写不敏感去重')

const csvWithColumns = parseCsvItemNumbers('货号,名称\nA-001,商品 A\nB-001,')
assert.equal(csvWithColumns.issues.length, 2, 'CSV 实际有多列内容时必须拒绝')
assert.deepEqual(csvWithColumns.itemNumbers, ['B-001'], 'CSV 尾部空字段不应被误判为多列')

const malformedCsv = parseCsvItemNumbers('"A-001')
assert.equal(malformedCsv.issues[0]?.reason, 'CSV 引号未闭合', '未闭合 CSV 引号必须明确报告')

const invalidClosingQuoteCsv = parseCsvItemNumbers('"00123"x')
assert.equal(invalidClosingQuoteCsv.issues[0]?.reason, 'CSV 关闭引号后包含非法字符', '关闭引号后的裸字符不得被静默拼接')

assert.equal(MAX_ITEM_NUMBERS, 3000, '货号导入上限必须与后端保持 3000 一致')
const screenshotRows = Array.from({ length: 924 }, (_, index) => `WIN-${index + 1}`).join('\n')
assert.equal(parsePastedItemNumbers(screenshotRows).itemNumbers.length, 924, '924 个有效货号应完整导入')

const beyondLimit = parsePastedItemNumbers(Array.from({ length: MAX_ITEM_NUMBERS + 1 }, (_, index) => `N${index}`).join('\n'))
assert.equal(beyondLimit.itemNumbers.length, MAX_ITEM_NUMBERS, '导入必须限制有效货号数')
assert.equal(beyondLimit.issues[0]?.reason, `最多可导入 ${MAX_ITEM_NUMBERS} 个货号`, '超限货号必须可见地报告')
assert.equal(beyondLimit.issues.length, 1, '第 3001 个货号应作为超限问题报告')

const csvAtLimit = parseCsvItemNumbers(Array.from({ length: MAX_ITEM_NUMBERS }, (_, index) => `C${index}`).join('\n'))
assert.equal(csvAtLimit.itemNumbers.length, MAX_ITEM_NUMBERS, 'CSV 应支持导入 3000 个货号')
assert.equal(csvAtLimit.issues.length, 0, '3000 个有效 CSV 货号不应报告超限')

const workbook = new ExcelJS.Workbook()
const worksheet = workbook.addWorksheet('货号')
worksheet.getCell('A1').value = '货号'
worksheet.getCell('A2').value = 12
worksheet.getCell('A2').numFmt = '00000'
worksheet.getCell('A3').value = '00007'
worksheet.getCell('A4').value = { richText: [{ text: 'AB' }, { text: '-01' }] }
worksheet.getCell('A5').value = { formula: 'A2', result: 12 }
worksheet.getCell('A5').numFmt = '0000'
worksheet.getCell('A6').value = { formula: 'A2' }
worksheet.getCell('A7').value = new Date('2026-08-18T00:00:00.000Z')
worksheet.getCell('A8').value = { error: '#REF!' }
worksheet.getCell('A9').value = { text: '00123', hyperlink: 'https://example.com/product' }
worksheet.getCell('A10').value = Number.MAX_SAFE_INTEGER + 1
const xlsxBuffer = new Uint8Array(await workbook.xlsx.writeBuffer() as ArrayBuffer)
const xlsxResult = await readItemNumberFile(createFile('items.XLSX', xlsxBuffer))
assert.deepEqual(xlsxResult.itemNumbers, ['00012', '00007', 'AB-01', '0012'], 'XLSX 数字格式、字符串、富文本和缓存公式值必须正确读取')
assert.equal(xlsxResult.issues[0]?.reason, '公式没有可用的缓存结果', '无缓存的公式不得执行，必须报告问题')
assert.deepEqual(
  xlsxResult.issues.slice(1).map((issue) => issue.reason),
  ['不支持日期单元格作为货号', '不支持错误单元格作为货号', '不支持超链接单元格作为货号', '数值货号超出安全整数范围'],
  '日期、错误、超链接和不安全整数均必须拒绝，不能字符串化为错误货号',
)

const xlsxColumns = new ExcelJS.Workbook()
const xlsxColumnsSheet = xlsxColumns.addWorksheet('货号')
xlsxColumnsSheet.addRow(['A-001', '商品 A'])
const xlsxColumnsBuffer = new Uint8Array(await xlsxColumns.xlsx.writeBuffer() as ArrayBuffer)
const xlsxColumnsResult = await readItemNumberFile(createFile('columns.xlsx', xlsxColumnsBuffer))
assert.equal(xlsxColumnsResult.issues[0]?.reason, '仅支持单列货号', 'XLSX 多个非空单元格不得静默读取首列')

const largeWorkbook = new ExcelJS.Workbook()
const largeWorksheet = largeWorkbook.addWorksheet('货号')
for (let index = 1; index <= MAX_ITEM_NUMBERS; index += 1) largeWorksheet.addRow([`WIN-${index}`])
const largeXlsxBuffer = new Uint8Array(await largeWorkbook.xlsx.writeBuffer() as ArrayBuffer)
const largeXlsxResult = await readItemNumberFile(createFile('items-3000.xlsx', largeXlsxBuffer))
assert.equal(largeXlsxResult.itemNumbers.length, MAX_ITEM_NUMBERS, 'XLSX 应完整导入 3000 个货号')
assert.equal(largeXlsxResult.issues.length, 0, '3000 个有效 XLSX 货号不应报告超限')

const unsupported = await readItemNumberFile(createFile('items.xls', 'legacy'))
assert.equal(unsupported.issues[0]?.reason, '仅支持 .csv 或 .xlsx 文件', '不应宣称支持 .xls')

const oversized = await readItemNumberFile(createFile('items.csv', 'A-001', MAX_FILE_SIZE_BYTES + 1))
assert.match(oversized.issues[0]?.reason ?? '', /5MB/, '超过文件大小上限必须拒绝')

console.log('BatchProductSalesAnalysis.import.test: ok')
