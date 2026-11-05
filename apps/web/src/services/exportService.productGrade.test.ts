import ExcelJS from 'exceljs'
import { getImageDownloadCandidates, populateProductGradesWorksheet } from './exportService'
import type { ProductGradeListItem } from '../types/productGrade'

function assertEqual<T>(actual: T, expected: T, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function createSampleProduct(overrides: Partial<ProductGradeListItem> = {}): ProductGradeListItem {
  return {
    id: 'grade-1',
    productCode: 'product-1',
    grade: 'A',
    supplierCode: 'SUP001',
    supplierName: '义乌供应商',
    hbProductNo: 'HB100-001',
    productName: '测试商品',
    productImage: 'https://img.example.com/HB100-001.jpg',
    domesticPrice: 12.34,
    importPrice: 2.45,
    oemPrice: 4.56,
    barcode: '930000000001',
    createdAt: '2026-06-18T00:00:00Z',
    ...overrides,
  }
}

function withWindowOrigin(origin: string, run: () => void) {
  const originalWindow = Object.getOwnPropertyDescriptor(globalThis, 'window')
  Object.defineProperty(globalThis, 'window', {
    configurable: true,
    value: { location: { origin } },
  })
  try {
    run()
  } finally {
    if (originalWindow) {
      Object.defineProperty(globalThis, 'window', originalWindow)
    } else {
      Reflect.deleteProperty(globalThis, 'window')
    }
  }
}

{
  const workbook = new ExcelJS.Workbook()
  const worksheet = workbook.addWorksheet('商品等级')

  const result = populateProductGradesWorksheet(worksheet, [createSampleProduct()])

  assertEqual(result.productImageColIndex, 4, '默认导出应包含商品图片列')
  assertEqual(worksheet.getRow(1).getCell(5).value, '商品图片', '商品图片列应出现在条码后')
  assertEqual(worksheet.getRow(2).height, 58, '带图片导出应增加行高')
  assertEqual(worksheet.getRow(2).getCell(1).fill?.type, undefined, '表身不应设置斑马底色')
}

{
  const workbook = new ExcelJS.Workbook()
  const worksheet = workbook.addWorksheet('商品等级')

  const result = populateProductGradesWorksheet(worksheet, [createSampleProduct()], {
    includeProductImage: false,
  })

  assertEqual(result.productImageColIndex, -1, '关闭图片导出时不应创建图片列')
  assertEqual(worksheet.getRow(1).getCell(5).value, '商品名称', '关闭图片后商品名称列应前移')
  assertEqual(worksheet.getRow(2).height, 24, '不带图片导出应使用普通行高')
}

{
  // 没有查看国内供应商名称权限：后端不返回名称，导出不得留下空的「供应商」名称列。
  const workbook = new ExcelJS.Workbook()
  const worksheet = workbook.addWorksheet('商品等级')

  const result = populateProductGradesWorksheet(
    worksheet,
    [createSampleProduct({ supplierName: undefined })],
    { includeSupplierName: false },
  )

  const headers = Array.from({ length: worksheet.columnCount }, (_, index) => worksheet.getRow(1).getCell(index + 1).value)
  assertEqual(headers.includes('供应商'), false, '无名称权限时不应导出「供应商」名称列')
  assertEqual(headers[0], '供应商编码', '无名称权限时首列应为供应商编码')
  assertEqual(worksheet.getRow(2).getCell(1).value, 'SUP001', '供应商编码应照常导出')
  assertEqual(worksheet.getRow(2).getCell(2).value, 'HB100-001', '去掉名称列后其余列应整体前移')
  assertEqual(result.productImageColIndex, 3, '图片列序号应随名称列去掉而前移')
  assertEqual(worksheet.getRow(1).getCell(4).value, '商品图片', '图片列表头应与返回的列序号一致')
  assertEqual(worksheet.getRow(2).getCell(7).numFmt, '¥#,##0.00', '去掉名称列后国内价仍应使用 RMB 格式')
}

{
  // 默认（有权限）保持原有两列：供应商名称 + 供应商编码。
  const workbook = new ExcelJS.Workbook()
  const worksheet = workbook.addWorksheet('商品等级')

  populateProductGradesWorksheet(worksheet, [createSampleProduct()])

  assertEqual(worksheet.getRow(1).getCell(1).value, '供应商', '默认导出应保留供应商名称列')
  assertEqual(worksheet.getRow(1).getCell(2).value, '供应商编码', '默认导出应保留供应商编码列')
  assertEqual(worksheet.getRow(2).getCell(1).value, '义乌供应商', '默认导出应写入供应商名称')
}

{
  const workbook = new ExcelJS.Workbook()
  const worksheet = workbook.addWorksheet('商品等级')

  populateProductGradesWorksheet(worksheet, [
    createSampleProduct(),
    createSampleProduct({
      id: 'grade-2',
      productCode: 'product-2',
      domesticPrice: undefined,
      importPrice: undefined,
      oemPrice: undefined,
    }),
  ])

  assertEqual(worksheet.getColumn('domesticPrice').numFmt, undefined, '价格格式不应写到整列元数据')
  assertEqual(worksheet.getRow(2).getCell(8).numFmt, '¥#,##0.00', '国内价应使用 RMB 格式')
  assertEqual(worksheet.getRow(2).getCell(9).numFmt, '$#,##0.00', '进口价应使用 AUD 格式')
  assertEqual(worksheet.getRow(2).getCell(10).numFmt, '$#,##0.00', '零售价应使用 AUD 格式')
  assertEqual(worksheet.getRow(3).getCell(8).value, '', '空国内价应保持空白')
  assertEqual(worksheet.getRow(3).getCell(9).value, '', '空进口价应保持空白')
  assertEqual(worksheet.getRow(3).getCell(10).value, '', '空零售价应保持空白')
}

withWindowOrigin('https://erp.example.com', () => {
  const candidates = getImageDownloadCandidates(
    'https://hotbargain-yw-2023-1300114625.cos.ap-shanghai.myqcloud.com/YW200/a b.jpg?size=500',
  )

  assertEqual(candidates.length, 2, '白名单跨域图片应优先走代理并保留直连兜底')
  assertEqual(
    candidates[0],
    '/api/react/v1/image-proxy?url=https%3A%2F%2Fhotbargain-yw-2023-1300114625.cos.ap-shanghai.myqcloud.com%2FYW200%2Fa%2520b.jpg%3Fsize%3D500',
    '白名单跨域图片代理地址应排在第一位',
  )
  assertEqual(
    candidates[1],
    'https://hotbargain-yw-2023-1300114625.cos.ap-shanghai.myqcloud.com/YW200/a%20b.jpg?size=500&hbImageExport=1',
    '白名单跨域图片直连兜底应追加导出参数',
  )
})

withWindowOrigin('https://erp.example.com', () => {
  const candidates = getImageDownloadCandidates(
    'https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/HB100/a.jpg?size=500&token=abc',
  )

  assertEqual(
    candidates[1],
    'https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/HB100/a.jpg?size=500&token=abc&hbImageExport=1',
    '已有 query 参数的白名单直连兜底应保留原 query 并追加导出参数',
  )
})

withWindowOrigin('https://erp.example.com', () => {
  const candidates = getImageDownloadCandidates(
    'https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/HB100/signed.jpg?size=500&q-signature=abc123&q-key-time=1%3B2',
  )

  assertEqual(candidates.length, 2, '签名 COS 图片仍应保留代理优先和直连兜底')
  assertEqual(
    candidates[1],
    'https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/HB100/signed.jpg?size=500&q-signature=abc123&q-key-time=1%3B2',
    '签名 COS 直连兜底不应追加导出参数，避免破坏签名校验',
  )
})

withWindowOrigin('https://erp.example.com', () => {
  const candidates = getImageDownloadCandidates('https://img.supplier.example.com/a b.jpg?size=500')

  assertEqual(candidates.length, 0, '未知跨域图片不应产生浏览器直连或后端代理候选')
})

withWindowOrigin('https://erp.example.com', () => {
  const candidates = getImageDownloadCandidates('http://localhost/private.jpg')

  assertEqual(candidates.length, 0, 'localhost 图片不应产生浏览器直连或后端代理候选')
})

withWindowOrigin('https://erp.example.com', () => {
  const candidates = getImageDownloadCandidates('http://169.254.169.254/latest/meta-data')

  assertEqual(candidates.length, 0, 'link-local 地址不应产生浏览器直连或后端代理候选')
})

withWindowOrigin('https://erp.example.com', () => {
  const candidates = getImageDownloadCandidates('/uploads/HB100-001.jpg')

  assertEqual(candidates.length, 1, '同源图片不应额外走代理')
  assertEqual(candidates[0], 'https://erp.example.com/uploads/HB100-001.jpg', '同源相对路径应归一化为绝对地址')
})
