import type {
  PackCartonItem,
  PackCartonSummary,
  PackLocateRow,
  PackOwner,
  PackPalletDetail,
  PackPalletSummary,
} from '../../types/packing'

import {
  UNASSIGNED_DESTINATION,
  buildPalletSteps,
  canDispatchCarton,
  canDispatchPallet,
  cartonStateKind,
  checkScannedCarton,
  destinationKind,
  destinationPlaceName,
  destinationToRequest,
  destinationToValue,
  findCartonItemByCode,
  formatPackTime,
  groupLocateRows,
  incomingState,
  isCartonCountConfirmed,
  isReadOnlyForOwner,
  isSameDestination,
  isShippableDestination,
  looksLikePackNo,
  normalizeLocationName,
  palletStateKind,
  parsePackUtc,
  parseWeightInput,
  pendingPalletCartons,
  resolveInitialOwnerCode,
  resolveOrderProgress,
  resolveReceiveBlock,
  resolveScanStep,
  resolveStationHotkey,
  selectCheckedCartons,
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

// ---- 所属地点：本机记住的地点必须仍在账号范围内 ----
const owners: PackOwner[] = [
  { code: '1008', name: 'Sunnybank', isWarehouse: false },
  { code: '1011', name: 'Inala', isWarehouse: false },
  { code: 'WAREHOUSE', name: 'HB Warehouse', isWarehouse: true },
]
assertEqual(resolveInitialOwnerCode(owners, '1011'), '1011', '记住的地点仍可用')
assertEqual(resolveInitialOwnerCode(owners, ' warehouse '), 'WAREHOUSE', '忽略大小写和空白')
assertEqual(resolveInitialOwnerCode(owners, '9999'), '1008', '记住的地点已不可操作时取第一个')
assertEqual(resolveInitialOwnerCode(owners, null), '1008', '没记住过取第一个')
assertEqual(resolveInitialOwnerCode([], '1008'), null, '没有可操作地点')

// ---- 去向 ----
assertDeepEqual(
  destinationToRequest({ destinationType: 2, destinationCode: '1011', carrierGuid: 'c-1', viaCarrier: true }),
  { destinationType: 2, destinationCode: '1011', carrierGuid: 'c-1' },
  '其他分店带编码和怎么送，界面标记不提交',
)
assertDeepEqual(
  destinationToRequest({ destinationType: 3, destinationCode: '1011', carrierGuid: null }),
  { destinationType: 3, destinationCode: null, carrierGuid: null },
  '总仓编码由后端固定，不传',
)
assertDeepEqual(
  destinationToRequest({ destinationType: 1, destinationCode: '1011', carrierGuid: 'c-1' }),
  { destinationType: 1, destinationCode: null, carrierGuid: null },
  '本地存放不带地点和运输公司',
)
assertDeepEqual(
  destinationToRequest({ destinationType: 7 }),
  { destinationType: 0, destinationCode: null, carrierGuid: null },
  '未知类型按未指定',
)
assertEqual(validateDestination({ destinationType: 2 }), 'storeRequired', '其他分店必须选分店')
assertEqual(validateDestination({ destinationType: 2, destinationCode: '1011' }), null, '其他分店选了分店即可（自己送）')
assertEqual(validateDestination({ destinationType: 3 }, true), 'warehouseFromWarehouse', '总仓不能发给总仓')
assertEqual(validateDestination({ destinationType: 3 }, false), null, '分店可以发给总仓')
assertEqual(
  validateDestination({ destinationType: 2, destinationCode: '1011', viaCarrier: true }),
  'carrierRequired',
  '选了交运输公司就要选一家',
)
assertEqual(
  validateDestination({ destinationType: 2, destinationCode: '1011', viaCarrier: true, carrierGuid: 'c-1' }),
  null,
  '交运输公司并选了一家',
)
assertEqual(validateDestination({ destinationType: 1, viaCarrier: true }), null, '本地存放不要求运输公司')
assertEqual(validateDestination(UNASSIGNED_DESTINATION), null, '暂不指定可以保存')
assertDeepEqual(
  destinationToValue({ destinationType: 2, code: '1011', name: 'Inala', carrierGuid: 'c-1', carrierName: 'Toll' }),
  { destinationType: 2, destinationCode: '1011', carrierGuid: 'c-1', viaCarrier: true },
  'DTO 转表单值：有运输公司即交运输公司',
)
assertDeepEqual(
  destinationToValue({ destinationType: 3, code: 'WAREHOUSE', name: 'HB Warehouse' }),
  { destinationType: 3, destinationCode: 'WAREHOUSE', carrierGuid: null, viaCarrier: false },
  '没有运输公司即自己送',
)
assertDeepEqual(destinationToValue(null), UNASSIGNED_DESTINATION, '没有去向即未指定')
assertEqual(
  isSameDestination(
    { destinationType: 1, destinationCode: 'x', carrierGuid: 'y' },
    { destinationType: 1, destinationCode: null, carrierGuid: null },
  ),
  true,
  '本地存放忽略残留的地点和运输公司',
)
assertEqual(
  isSameDestination({ destinationType: 2, destinationCode: '1011' }, { destinationType: 2, destinationCode: '1012' }),
  false,
  '分店不同即不同',
)
assertEqual(destinationKind({ destinationType: 0 }), 'unassigned', '未指定')
assertEqual(destinationKind({ destinationType: 1 }), 'local', '本地存放')
assertEqual(destinationKind({ destinationType: 2, code: '1011' }), 'store', '其他分店')
assertEqual(destinationKind({ destinationType: 3, code: 'WAREHOUSE' }), 'warehouse', '总仓')
assertEqual(isShippableDestination({ destinationType: 2, code: '1011' }), true, '其他分店可发出')
assertEqual(isShippableDestination({ destinationType: 2 }), false, '没选分店不可发出')
assertEqual(isShippableDestination({ destinationType: 1 }), false, '本地存放不可发出')
assertEqual(destinationPlaceName({ destinationType: 2, code: '1011', name: 'Inala' }), 'Inala', '其他分店显示名称')
assertEqual(destinationPlaceName({ destinationType: 3, code: 'WAREHOUSE' }), 'WAREHOUSE', '没有名称显示编码')
assertEqual(destinationPlaceName({ destinationType: 1 }), null, '本地存放没有收货地点')

// ---- 状态展示 ----
function carton(overrides: Partial<PackCartonSummary> = {}): PackCartonSummary {
  return {
    cartonGuid: 'c-1',
    cartonNo: 'BX2611130042',
    status: 2,
    ownerCode: '1008',
    ownerName: 'Sunnybank',
    originCode: '1008',
    originName: 'Sunnybank',
    destination: { destinationType: 2, code: '1011', name: 'Inala' },
    skuCount: 2,
    pieceCount: 30,
    createdByName: 'Tom',
    createdAtUtc: '2026-11-13T01:00:00',
    updatedAtUtc: '2026-11-13T01:00:00',
    ...overrides,
  }
}

function pallet(overrides: Partial<PackPalletSummary> = {}): PackPalletSummary {
  return {
    palletGuid: 'p-1',
    palletNo: 'PL261113007',
    status: 2,
    ownerCode: '1008',
    ownerName: 'Sunnybank',
    originCode: '1008',
    originName: 'Sunnybank',
    destination: { destinationType: 2, code: '1011', name: 'Inala' },
    cartonCount: 5,
    inTransitCartonCount: 0,
    skuCount: 9,
    pieceCount: 120,
    createdByName: 'Tom',
    createdAtUtc: '2026-11-13T01:00:00',
    updatedAtUtc: '2026-11-13T01:00:00',
    ...overrides,
  }
}

assertEqual(cartonStateKind(carton({ status: 1 }), '1008'), 'packing', '装箱中')
assertEqual(cartonStateKind(carton({ status: 2 }), '1008'), 'stored', '已封箱且在本地点手里是本地存放')
assertEqual(cartonStateKind(carton({ status: 3 }), '1008'), 'transit', '在路上')
assertEqual(cartonStateKind(carton({ status: 2, ownerCode: '1011' }), '1008'), 'received', '所属地点换了是对方已签收')
assertEqual(cartonStateKind(carton({ status: 2, ownerCode: '1008' }), ' 1008'), 'stored', '编码比较忽略空白')
assertEqual(cartonStateKind(carton({ status: 9 }), '1008'), 'voided', '作废')
assertEqual(palletStateKind(pallet({ status: 1 }), '1008'), 'building', '组托中')
assertEqual(palletStateKind(pallet({ status: 2 }), '1008'), 'stored', '托盘本地存放')
assertEqual(palletStateKind(pallet({ status: 3 }), '1008'), 'transit', '托盘在路上')
assertEqual(palletStateKind(pallet({ status: 2, ownerCode: '1011' }), '1008'), 'received', '托盘对方已签收')
assertEqual(isReadOnlyForOwner(carton({ status: 3 }), '1008'), true, '在路上只读')
assertEqual(isReadOnlyForOwner(carton({ status: 2, ownerCode: '1011' }), '1008'), true, '已被对方签收只读')
assertEqual(isReadOnlyForOwner(carton({ status: 2 }), '1008'), false, '本地存放可改')
assertEqual(canDispatchCarton(carton(), '1008'), true, '已封箱、去向是其他分店：可发出')
assertEqual(canDispatchCarton(carton({ palletGuid: 'p-1' }), '1008'), false, '在托盘上的箱不能单独发')
assertEqual(canDispatchCarton(carton({ status: 1 }), '1008'), false, '装箱中不能发')
assertEqual(canDispatchCarton(carton({ destination: { destinationType: 1 } }), '1008'), false, '本地存放不能发')
assertEqual(canDispatchCarton(carton({ status: 3 }), '1008'), false, '已发出不能再发')
assertEqual(canDispatchPallet(pallet(), '1008'), true, '已封托、去向是其他分店：可发出')
assertEqual(canDispatchPallet(pallet({ cartonCount: 0 }), '1008'), false, '空托盘不能发')
assertEqual(canDispatchPallet(pallet({ status: 1 }), '1008'), false, '组托中不能发')
assertEqual(canDispatchPallet(pallet({ destination: { destinationType: 0 } }), '1008'), false, '没指定去向不能发')

// ---- 托盘四步进度 ----
const steps = buildPalletSteps(pallet({ sealedAtUtc: '2026-11-13T02:00:00', sealedByName: 'Lily' }))
assertEqual(steps.map((step) => step.key).join(','), 'build,seal,dispatch,receive', '四步顺序')
assertEqual(steps.map((step) => step.done).join(','), 'true,true,false,false', '组托封托已完成，发出签收未完成')
const allSteps = buildPalletSteps(
  pallet({
    sealedAtUtc: '2026-11-13T02:00:00',
    dispatchedAtUtc: '2026-11-13T03:00:00',
    dispatchedByName: 'Jack',
    receivedAtUtc: '2026-11-13T05:00:00',
    receivedByName: 'Amy',
  }),
)
assertEqual(allSteps.map((step) => step.done).join(','), 'true,true,true,true', '签收有时间时四步全部点亮')
assertEqual(allSteps[3].by, 'Amy', '签收人')

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

// ---- 订货单对照（只有总仓才有）----
assertDeepEqual(resolveOrderProgress(item({ orderQuantity: 36 })), { onOrder: true, remaining: 12, over: false }, '还差 12')
assertDeepEqual(
  resolveOrderProgress(item({ orderQuantity: 48, packedInOtherCartons: 36, quantity: 12 })),
  { onOrder: true, remaining: 0, over: false },
  '其他箱已装的要扣掉',
)
assertDeepEqual(resolveOrderProgress(item({ orderQuantity: 20, quantity: 24 })), { onOrder: true, remaining: -4, over: true }, '装多了')
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

// ---- 到货签收 ----
const received = carton({ cartonGuid: 'c-9', cartonNo: 'BX2611130009', status: 2, ownerCode: '1011' })
const receivePallet = {
  ...pallet({ status: 3, inTransitCartonCount: 3, cartonCount: 4 }),
  cartons: [
    carton({ cartonGuid: 'c-1', cartonNo: 'BX2611130001', status: 3 }),
    carton({ cartonGuid: 'c-2', cartonNo: 'BX2611130002', status: 3 }),
    carton({ cartonGuid: 'c-3', cartonNo: 'BX2611130003', status: 3 }),
    received,
  ],
  products: [],
} as PackPalletDetail
assertEqual(pendingPalletCartons(receivePallet).map((entry) => entry.cartonGuid).join(','), 'c-1,c-2,c-3', '只有还在路上的箱需要核对')
assertEqual(isCartonCountConfirmed(0, 3), false, '初始 0 不能确认')
assertEqual(isCartonCountConfirmed(3, 3), true, '数对了可以确认')
assertEqual(isCartonCountConfirmed('3', 3), true, '字符串数字也算')
assertEqual(isCartonCountConfirmed(2, 3), false, '少了不能确认')
assertEqual(isCartonCountConfirmed(4, 3), false, '多了不能确认')
assertEqual(isCartonCountConfirmed(null, 3), false, '没填不能确认')
assertEqual(isCartonCountConfirmed(1.5, 3), false, '小数不能确认')
assertEqual(isCartonCountConfirmed(0, 0), false, '没有待签收的箱不能确认')
assertEqual(resolveReceiveBlock('NOT_FOR_YOU'), 'notForYou', '不是发给你的')
assertEqual(resolveReceiveBlock('NOT_IN_TRANSIT'), 'notInTransit', '不在路上')
assertEqual(resolveReceiveBlock(null), null, '没有阻止原因')

const checked = new Set<string>()
assertEqual(checkScannedCarton(receivePallet, ' bx2611130002 ', checked), true, '扫到托盘上的箱就勾上（忽略大小写和空白）')
assertEqual(checkScannedCarton(receivePallet, 'BX2611130009', checked), false, '已签收的箱不再勾选')
assertEqual(checkScannedCarton(receivePallet, 'BX9999999999', checked), false, '不在这个托盘上的箱不勾选')
assertDeepEqual(selectCheckedCartons(receivePallet, checked), ['c-2'], '只签已核对的箱')
checked.add('c-9')
checked.add('c-unknown')
assertDeepEqual(selectCheckedCartons(receivePallet, checked), ['c-2'], '已签收的箱和不在托盘上的 GUID 不会传给接口')

assertDeepEqual(
  incomingState({ kind: 'pallet', cartonCount: 5, receivedCartonCount: 3 }),
  { kind: 'partial', received: 3, total: 5 },
  '托盘部分签收',
)
assertDeepEqual(incomingState({ kind: 'pallet', cartonCount: 5, receivedCartonCount: 0 }), { kind: 'transit' }, '还没签收')
assertDeepEqual(incomingState({ kind: 'carton', cartonCount: 1, receivedCartonCount: 0 }), { kind: 'transit' }, '散箱运输中')
assertDeepEqual(
  incomingState({ kind: 'pallet', cartonCount: 5, receivedCartonCount: 5, receivedAtUtc: '2026-11-13T06:00:00' }),
  { kind: 'received' },
  '已签收',
)

// ---- 商品位置分组 ----
function row(overrides: Partial<PackLocateRow>): PackLocateRow {
  return {
    cartonGuid: 'c-1',
    cartonNo: 'BX2611130001',
    cartonStatus: 2,
    ownerCode: '1008',
    ownerName: 'Sunnybank',
    originName: 'Sunnybank',
    destination: { destinationType: 1 },
    quantity: 1,
    updatedAtUtc: '2026-11-13T01:00:00',
    group: 'stored',
    ...overrides,
  }
}

const sections = groupLocateRows([
  row({ cartonGuid: 'c-1', locationCode: null, quantity: 5 }),
  row({ cartonGuid: 'c-2', locationCode: 'B-12', quantity: 24, updatedAtUtc: '2026-11-13T02:00:00' }),
  row({ cartonGuid: 'c-3', locationCode: 'A-03', quantity: 48, updatedAtUtc: '2026-11-13T03:00:00' }),
  row({ cartonGuid: 'c-4', locationCode: ' A-03 ', quantity: 24, updatedAtUtc: '2026-11-13T04:00:00' }),
  row({ cartonGuid: 'c-5', group: 'transit', cartonStatus: 3, quantity: 12 }),
  row({ cartonGuid: 'c-6', group: 'elsewhere', ownerName: 'Inala', ownerCode: '1011', quantity: 7 }),
  row({ cartonGuid: 'c-7', group: 'other', ownerName: 'Morayfield', ownerCode: '1013', quantity: 3 }),
  row({ cartonGuid: 'c-8', group: 'other', ownerName: 'Alderley', ownerCode: '1014', quantity: 4 }),
  row({ cartonGuid: 'c-9', group: 'something-new', quantity: 1 }),
])
assertEqual(sections.map((section) => section.group).join(','), 'stored,transit,elsewhere,other', '分组顺序：本地存放、在路上、对方已签收、其他分店')
assertEqual(sections.map((section) => section.quantity).join(','), '101,12,7,8', '每组小计（未知分组并入其他分店）')
assertEqual(sections[0].rows.map((entry) => entry.cartonGuid).join(','), 'c-4,c-3,c-2,c-1', '本地存放：有位置的按位置排序在前（同位置最近变动在前），没位置的最后')
assertEqual(sections[3].rows.map((entry) => entry.cartonGuid).join(','), 'c-8,c-7,c-9', '其他分店按所属分店名排序')
assertEqual(groupLocateRows([]).length, 0, '没有行就没有分组')

// ---- 存放位置名称 ----
assertEqual(normalizeLocationName('  后仓 A 架 '), '后仓 A 架', '去掉首尾空白')
assertEqual(normalizeLocationName('   '), null, '空名称无效')
assertEqual(normalizeLocationName('x'.repeat(51)), null, '超过 50 个字无效')
assertEqual(normalizeLocationName('x'.repeat(50)), 'x'.repeat(50), '50 个字有效')

console.log('packingLogic.test: ok')
