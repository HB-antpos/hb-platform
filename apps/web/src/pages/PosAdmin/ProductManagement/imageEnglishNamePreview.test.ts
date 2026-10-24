import type { ProductImageDescriptionSummary } from '../../../services/productDescriptionService'
import {
  buildImageEnglishNamePreviewRows,
  buildImageEnglishNameUpdates,
  buildPosProductImageDescriptionItems,
} from './imageEnglishNamePreview'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)
  if (actualJson !== expectedJson) {
    throw new Error(`${message}。Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

const rows = [
  { key: 'k1', productCode: 'P1', productName: '陶瓷杯', productImage: ' https://img.example.com/p1.jpg ' },
  { key: 'k2', productCode: 'P2', productName: 'Storage Box', productImage: '' },
  { key: 'k3', productCode: '', productName: '没有编码', productImage: 'https://img.example.com/p3.jpg' },
  { key: 'k4', productCode: 'P4', productName: '', productImage: '' },
  { key: 'k5', productCode: 'P5', productName: '塑料盒', productImage: 'https://img.example.com/p5.jpg' },
  { key: 'k6', productCode: 'P6', productName: '收纳篮', productImage: 'https://img.example.com/p6.jpg' },
]

assertDeepEqual(
  buildPosProductImageDescriptionItems(rows),
  [
    { key: 'k1', imageUrl: 'https://img.example.com/p1.jpg', chineseName: '陶瓷杯' },
    { key: 'k2', chineseName: 'Storage Box' },
    { key: 'k5', imageUrl: 'https://img.example.com/p5.jpg', chineseName: '塑料盒' },
    { key: 'k6', imageUrl: 'https://img.example.com/p6.jpg', chineseName: '收纳篮' },
  ],
  '没有商品编码、既没图也没名称的行不发请求；图片地址去掉空白',
)

const summary: ProductImageDescriptionSummary = {
  results: {
    k1: { key: 'k1', description: 'Ceramic Mug 350ml', source: 'image' },
    k2: { key: 'k2', description: 'Storage Box', source: 'text' },
    k5: { key: 'k5', description: '塑料盒', source: 'text' },
    k6: { key: 'k6', description: null, source: 'failed', reason: '图片下载失败' },
  },
  imageCount: 1,
  textCount: 2,
  failedCount: 1,
}

const previewRows = buildImageEnglishNamePreviewRows(rows, summary)
assertDeepEqual(previewRows.map((row) => row.key), ['k1'], '未变化、仍含中文、失败的结果都不进预览')
assertEqual(previewRows[0].currentName, '陶瓷杯', '预览应显示当前名称')
assertEqual(previewRows[0].source, 'image', '预览应带上结果来源')

assertDeepEqual(
  buildImageEnglishNameUpdates(previewRows),
  [{ productCode: 'P1', productName: 'Ceramic Mug 350ml', englishName: 'Ceramic Mug 350ml' }],
  '确认后同时覆盖商品名称和英文名称',
)

console.log('imageEnglishNamePreview.test: ok')
