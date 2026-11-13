import type { PackCartonItem, PackLocateRow } from '../../../types/warehousePacking'

import {
  UNASSIGNED_DESTINATION,
  describeDestination,
  destinationToRequest,
  destinationToValue,
  findCartonItemByCode,
  formatPackTime,
  groupLocateRows,
  isSameDestination,
  looksLikePackNo,
  parsePackUtc,
  parseWeightInput,
  resolveOrderProgress,
  resolveScanStep,
  resolveStationHotkey,
  validateDestination,
} from './packingLogic'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  assertEqual(JSON.stringify(actual), JSON.stringify(expected), message)
}

// ---- 时间：不带时区后缀的值按 UTC 解析 ----
assertEqual(parsePackUtc('2026-11-13T04:32:00')?.toISOString(), '2026-11-13T04:32:00.000Z', '无后缀按 UTC')
assertEqual(parsePackUtc('2026-11-13T04:32:00Z')?.toISOString(), '2026-11-13T04:32:00.000Z', '带 Z 不重复追加')
assertEqual(parsePackUtc('2026-11-13T14:32:00+10:00')?.toISOString(), '2026-11-13T04:32:00.000Z', '带偏移照常解析')
assertEqual(parsePackUtc(''), null, '空值为 null')
assertEqual(parsePackUtc('not-a-date'), null, '非法值为 null')
assertEqual(formatPackTime(null), '—', '空值显示占位')
assertEqual(/^\d{2}-\d{2} \d{2}:\d{2}$/.test(formatPackTime('2026-11-13T04:32:00')), true, '时间格式 MM-DD HH:mm')

// ---- 去向 ----
assertDeepEqual(
  destinationToRequest({ destinationType: 1, storeCode: '1008', carrierGuid: 'c-1' }),
  { destinationType: 1, destinationStoreCode: '1008', carrierGuid: null },
  '直送分店不带运输公司',
)
assertDeepEqual(
  destinationToRequest({ destinationType: 2, storeCode: null, carrierGuid: 'c-1' }),
  { destinationType: 2, destinationStoreCode: null, carrierGuid: 'c-1' },
  '经运输公司时最终分店可空',
)
assertDeepEqual(
  destinationToRequest({ destinationType: 0, storeCode: '1008', carrierGuid: 'c-1' }),
  { destinationType: 0, destinationStoreCode: null, carrierGuid: null },
  '未指定时不带分店和运输公司',
)
assertEqual(validateDestination({ destinationType: 1 }), 'storeRequired', '分店去向必须选分店')
assertEqual(validateDestination({ destinationType: 2, storeCode: '1008' }), 'carrierRequired', '运输公司必须选一家')
assertEqual(validateDestination({ destinationType: 2, carrierGuid: 'c-1' }), null, '运输公司不要求最终分店')
assertEqual(validateDestination(UNASSIGNED_DESTINATION), null, '暂不指定可以保存')
assertDeepEqual(
  destinationToValue({ destinationType: 2, storeCode: '1008', storeName: 'Cairns', carrierGuid: 'c-1', carrierName: 'BEX' }),
  { destinationType: 2, storeCode: '1008', carrierGuid: 'c-1' },
  'DTO 转表单值',
)
assertDeepEqual(destinationToValue(null), UNASSIGNED_DESTINATION, '没有去向即未指定')
assertEqual(
  isSameDestination({ destinationType: 1, storeCode: '1008', carrierGuid: 'x' }, { destinationType: 1, storeCode: '1008' }),
  true,
  '直送分店时忽略残留的运输公司',
)
assertEqual(
  isSameDestination({ destinationType: 1, storeCode: '1008' }, { destinationType: 1, storeCode: '1009' }),
  false,
  '分店不同即不同',
)
assertEqual(describeDestination({ destinationType: 0 }), null, '未指定没有文字')
assertEqual(describeDestination({ destinationType: 1, storeCode: '1008', storeName: 'Sunnybank' }), 'Sunnybank', '分店显示名称')
assertEqual(describeDestination({ destinationType: 1, storeCode: '1008' }), '1008', '没有名称显示编码')
assertEqual(
  describeDestination({ destinationType: 2, storeName: 'Cairns', carrierName: 'Border Express' }),
  'Border Express → Cairns',
  '经运输公司显示「公司 → 分店」',
)
assertEqual(describeDestination({ destinationType: 2, carrierName: 'Toll' }), 'Toll', '没有最终分店只显示公司')

// ---- 计数方式 ----
const product = { middlePackageQuantity: 12, packingQuantity: 48 }
assertDeepEqual(resolveScanStep('one', product), { quantity: 1, fellBack: false }, '每扫 +1')
assertDeepEqual(resolveScanStep('middle', product), { quantity: 12, fellBack: false }, '按中包')
assertDeepEqual(resolveScanStep('packing', product), { quantity: 48, fellBack: false }, '按箱规')
assertDeepEqual(resolveScanStep('middle', { middlePackageQuantity: null, packingQuantity: 48 }), { quantity: 1, fellBack: true }, '没有中包数退回 1 件')
assertDeepEqual(resolveScanStep('packing', { middlePackageQuantity: 12, packingQuantity: 1 }), { quantity: 1, fellBack: true }, '箱规为 1 视为没有')
assertDeepEqual(resolveScanStep('packing', undefined), { quantity: 1, fellBack: true }, '不知道商品时退回 1 件')

function item(overrides: Partial<PackCartonItem>): PackCartonItem {
  return {
    productCode: 'P-1',
    itemNumber: 'HB23817',
    productName: 'Mug',
    barcode: '9345678123457',
    quantity: 24,
    packedInOtherCartons: 0,
    updatedAtUtc: '2026-11-13T04:32:00',
    ...overrides,
  }
}

const items = [item({}), item({ productCode: 'P-2', itemNumber: 'HB24102', barcode: '9345678000011' })]
assertEqual(findCartonItemByCode(items, ' 9345678000011 ')?.productCode, 'P-2', '按条码找到箱内商品')
assertEqual(findCartonItemByCode(items, 'hb23817')?.productCode, 'P-1', '货号忽略大小写')
assertEqual(findCartonItemByCode(items, 'P-2')?.productCode, 'P-2', '按商品编码')
assertEqual(findCartonItemByCode(items, 'UNKNOWN'), undefined, '箱里没有的码')
assertEqual(findCartonItemByCode(items, ''), undefined, '空码不匹配空字段')

// ---- 订货单对照 ----
assertDeepEqual(resolveOrderProgress(item({ orderQuantity: 36 })), { onOrder: true, remaining: 12, over: false }, '还差 12')
assertDeepEqual(
  resolveOrderProgress(item({ orderQuantity: 48, packedInOtherCartons: 36, quantity: 12 })),
  { onOrder: true, remaining: 0, over: false },
  '其他箱已装的要扣掉',
)
assertDeepEqual(
  resolveOrderProgress(item({ orderQuantity: 20, quantity: 24 })),
  { onOrder: true, remaining: -4, over: true },
  '装多了',
)
assertDeepEqual(resolveOrderProgress(item({ orderQuantity: null })), { onOrder: false, remaining: 0, over: false }, '订货单外')

// ---- 箱号 / 托盘号、快捷键、重量 ----
assertEqual(looksLikePackNo('BX2611130042'), true, '箱号')
assertEqual(looksLikePackNo(' pl261113007 '), true, '托盘号（小写、带空格）')
assertEqual(looksLikePackNo('9345678123457'), false, '商品条码')
assertEqual(looksLikePackNo('BX-SHELF'), false, '以 BX 开头的货号不算')
assertEqual(resolveStationHotkey({ key: 'F2' }), 'newCarton', 'F2 新箱')
assertEqual(resolveStationHotkey({ key: 'F9' }), 'sealCarton', 'F9 封箱并打印')
assertEqual(resolveStationHotkey({ key: 'F10' }), 'sealPallet', 'F10 封托并打印')
assertEqual(resolveStationHotkey({ key: 'F9', ctrlKey: true }), null, '带修饰键不处理')
assertEqual(resolveStationHotkey({ key: 'Enter' }), null, '其他键不处理')
assertEqual(parseWeightInput(null), null, '不填重量')
assertEqual(parseWeightInput(''), null, '空串视为不填')
assertEqual(parseWeightInput('12.345'), 12.35, '保留两位小数')
assertEqual(parseWeightInput(-1), undefined, '负数无效')
assertEqual(parseWeightInput(10000), undefined, '超出范围无效')
assertEqual(parseWeightInput('abc'), undefined, '非数字无效')

// ---- 位置分组 ----
function row(overrides: Partial<PackLocateRow>): PackLocateRow {
  return {
    cartonGuid: 'c-1',
    cartonNo: 'BX2611130001',
    cartonStatus: 2,
    originName: 'HB WAREHOUSE',
    destination: { destinationType: 0 },
    quantity: 1,
    updatedAtUtc: '2026-11-13T01:00:00',
    ...overrides,
  }
}

const groups = groupLocateRows([
  row({ cartonGuid: 'c-1', locationCode: null, quantity: 5 }),
  row({ cartonGuid: 'c-2', locationCode: 'B-12', quantity: 24, updatedAtUtc: '2026-11-13T02:00:00' }),
  row({ cartonGuid: 'c-3', locationCode: 'A-03', quantity: 48, updatedAtUtc: '2026-11-13T03:00:00' }),
  row({ cartonGuid: 'c-4', locationCode: ' A-03 ', quantity: 24, updatedAtUtc: '2026-11-13T04:00:00' }),
])
assertEqual(groups.map((group) => group.locationCode).join('|'), 'A-03|B-12|', '有货位的按编码排序，未指定的排最后')
assertEqual(groups.map((group) => group.quantity).join('|'), '72|24|5', '每组小计')
assertEqual(groups[0].rows.map((entry) => entry.cartonGuid).join('|'), 'c-4|c-3', '组内最近变动在前')
assertEqual(groupLocateRows([]).length, 0, '没有行就没有分组')

console.log('packingLogic.test: ok')
