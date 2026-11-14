import { normalizePackOrderReceive } from '../../services/packingService'
import type {
  PackCartonItem,
  PackCartonSummary,
  PackLocateRow,
  PackOrderReceive,
  PackOwner,
  PackPalletDetail,
  PackPalletSummary,
} from '../../types/packing'

import {
  UNASSIGNED_DESTINATION,
  buildPalletSteps,
  canDispatchCarton,
  assignIncomingOrderEntries,
  canDispatchPallet,
  cartonStateKind,
  checkScannedCarton,
  classifyOrderReceiveFailure,
  collectOrderReceiveEntries,
  destinationKind,
  destinationPlaceName,
  destinationToRequest,
  destinationToValue,
  findCartonItemByCode,
  formatPackTime,
  groupLocateRows,
  incomingItemOrders,
  incomingState,
  isCartonCountConfirmed,
  isOrderShipMismatch,
  isReadOnlyForOwner,
  isSameDestination,
  isShippableDestination,
  looksLikePackNo,
  normalizeLocationName,
  palletStateKind,
  parsePackUtc,
  parseWeightInput,
  pendingOrderCartons,
  pendingPalletCartons,
  resolveActualInTransitCount,
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

// ---- 按订单签收（二期）----
const inTransit = { status: 3 }
const sealedCarton = { status: 2 }
assertDeepEqual(
  collectOrderReceiveEntries([
    { ...inTransit, orderGuid: 'o-1', orderNo: 'SO001' },
    { ...inTransit, orderGuid: 'o-2', orderNo: 'SO002' },
    { ...inTransit, orderGuid: 'O-1', orderNo: 'SO001' },
    { ...inTransit, orderGuid: 'o-1', orderNo: 'SO001' },
  ]),
  [
    { orderGuid: 'o-1', orderNo: 'SO001', cartonCount: 3 },
    { orderGuid: 'o-2', orderNo: 'SO002', cartonCount: 1 },
  ],
  '同一订单（忽略 GUID 大小写）只给一个入口，按首次出现的顺序，统计这里的在途箱数',
)
assertDeepEqual(
  collectOrderReceiveEntries([
    { ...inTransit },
    { ...inTransit, orderGuid: null, orderNo: 'SO003' },
    { ...inTransit, orderGuid: 'o-4', orderNo: '  ' },
    { ...sealedCarton, orderGuid: 'o-5', orderNo: 'SO005' },
    { status: 1, orderGuid: 'o-6', orderNo: 'SO006' },
  ]),
  [],
  '没订单号 / 没订单 GUID / 不在路上的箱不给入口',
)
assertDeepEqual(
  collectOrderReceiveEntries([{ status: 3, orderGuid: ' o-7 ', orderNo: ' SO007 ' }]),
  [{ orderGuid: 'o-7', orderNo: 'SO007', cartonCount: 1 }],
  '订单号 / GUID 去掉首尾空白',
)
assertEqual(
  pendingOrderCartons({ cartons: [{ status: 3 }, { status: 2 }, { status: 3 }, { status: 1 }] }).length,
  2,
  '订单里还在路上的箱数',
)
assertEqual(pendingOrderCartons({ cartons: [] }).length, 0, '没有箱就没有在途箱')

// ---- 待签收清单里的「按订单签收」入口 ----
// 散箱行：用 orderGuid / orderNo；没有订单关联（或旧后端没有该字段）没有入口。
assertDeepEqual(
  incomingItemOrders({ kind: 'carton', orderGuid: 'o-1', orderNo: 'SO001' }),
  [{ orderGuid: 'o-1', label: 'SO001' }],
  '散箱行取 orderGuid / orderNo',
)
assertDeepEqual(incomingItemOrders({ kind: 'carton' }), [], '散箱没有订单关联（旧后端缺字段）没有入口')
assertDeepEqual(incomingItemOrders({ kind: 'carton', orderGuid: '  ', orderNo: 'SO001' }), [], '订单 GUID 为空白不给入口')
assertDeepEqual(
  incomingItemOrders({ kind: 'carton', orderGuid: 'abcdef12-0000-0000-0000-000000000000', orderNo: null }),
  [{ orderGuid: 'abcdef12-0000-0000-0000-000000000000', label: 'abcdef12' }],
  '没有订单号时用 GUID 前 8 位当显示名',
)
// 托盘行：用 orders[]，散箱字段不参与；同一行按 GUID（忽略大小写）去重，缺 GUID 的丢弃。
assertDeepEqual(
  incomingItemOrders({
    kind: 'pallet',
    orderGuid: 'ignored',
    orderNo: 'IGNORED',
    orders: [
      { orderGuid: 'o-1', orderNo: 'SO001' },
      { orderGuid: 'o-2', orderNo: ' SO002 ' },
      { orderGuid: 'O-1', orderNo: 'SO001' },
      { orderGuid: '', orderNo: 'SO003' },
    ],
  }),
  [
    { orderGuid: 'o-1', label: 'SO001' },
    { orderGuid: 'o-2', label: 'SO002' },
  ],
  '托盘行取 orders[]，行内去重、丢弃缺 GUID 的',
)
assertDeepEqual(incomingItemOrders({ kind: 'pallet' }), [], '托盘没有订单关联时 orders 缺失，没有入口')
assertDeepEqual(incomingItemOrders({ kind: 'pallet', orders: null }), [], 'orders 为 null 也没有入口')
assertEqual(
  incomingItemOrders({ kind: 'pallet', orders: Array.from({ length: 20 }, (_, index) => ({ orderGuid: `o-${index}`, orderNo: `SO${index}` })) }).length,
  20,
  '托盘最多 20 个订单，全部保留',
)

// 清单里同一订单只在第一次出现的行给入口，后面的行只显示订单号。
const assigned = assignIncomingOrderEntries([
  { key: 'pending:p1', orders: [{ orderGuid: 'o-1', label: 'SO001' }, { orderGuid: 'o-2', label: 'SO002' }] },
  { key: 'pending:p2', orders: [{ orderGuid: 'O-2', label: 'SO002' }, { orderGuid: 'o-3', label: 'SO003' }] },
  { key: 'pending:c1', orders: [{ orderGuid: 'o-1', label: 'SO001' }] },
  { key: 'pending:c2', orders: [] },
  { key: 'pending:p3', orders: [{ orderGuid: 'o-4', label: 'SO004' }] },
])
assertDeepEqual(assigned.get('pending:p1'), { entries: [{ orderGuid: 'o-1', label: 'SO001' }, { orderGuid: 'o-2', label: 'SO002' }], covered: [] }, '托盘多订单：每个订单一个入口')
assertDeepEqual(
  assigned.get('pending:p2'),
  { entries: [{ orderGuid: 'o-3', label: 'SO003' }], covered: [{ orderGuid: 'O-2', label: 'SO002' }] },
  '后面的托盘：已出现过的订单（忽略 GUID 大小写）只显示订单号，新订单给入口',
)
assertDeepEqual(
  assigned.get('pending:c1'),
  { entries: [], covered: [{ orderGuid: 'o-1', label: 'SO001' }] },
  '散箱的订单前面已给过入口：只显示订单号，不重复占位',
)
assertEqual(assigned.has('pending:c2'), false, '没有订单的行不在结果里')
assertDeepEqual(assigned.get('pending:p3'), { entries: [{ orderGuid: 'o-4', label: 'SO004' }], covered: [] }, '只有一个订单：一个入口')
assertEqual(assigned.size, 4, '结果只含有订单的行')
assertEqual(assignIncomingOrderEntries([]).size, 0, '空清单没有入口')
// 清单被「来自」过滤后重新分配：入口跟着留下来的第一行走，不会因为前面的行被过滤掉而消失。
assertDeepEqual(
  assignIncomingOrderEntries([{ key: 'pending:c1', orders: [{ orderGuid: 'o-1', label: 'SO001' }] }]).get('pending:c1'),
  { entries: [{ orderGuid: 'o-1', label: 'SO001' }], covered: [] },
  '过滤后第一次出现的行拿到入口',
)

// 实到箱数核对与整托签收同一做法：必须是非负整数且等于在途箱数；没有在途箱不能签收。
assertEqual(isCartonCountConfirmed(3, 3), true, '数对了可以签收全部')
assertEqual(isCartonCountConfirmed(2, 3), false, '少数了不能签收')
assertEqual(isCartonCountConfirmed(4, 3), false, '多数了不能签收')
assertEqual(isCartonCountConfirmed(null, 3), false, '没填不能签收')
assertEqual(isCartonCountConfirmed(0, 0), false, '没有在途箱不能签收')

assertEqual(isOrderShipMismatch({ allocPieces: 12, shippedPieces: 12 }), false, '发出 = 配货不高亮')
assertEqual(isOrderShipMismatch({ allocPieces: 12, shippedPieces: 10 }), true, '少发了高亮')
assertEqual(isOrderShipMismatch({ allocPieces: 12, shippedPieces: 14 }), true, '多发了高亮')
assertEqual(isOrderShipMismatch({ allocPieces: 0, shippedPieces: 0 }), false, '配货 0 且发出 0 不高亮')
assertEqual(isOrderShipMismatch({ allocPieces: null, shippedPieces: 5 }), false, '没有配货数无从比较，不高亮')
assertEqual(isOrderShipMismatch({ shippedPieces: 5 }), false, '配货数缺失（null 不输出）不高亮')

assertEqual(classifyOrderReceiveFailure('COUNT_MISMATCH'), 'countMismatch', '409 数量不符')
assertEqual(classifyOrderReceiveFailure('NOT_IN_TRANSIT'), 'notInTransit', '409 没有在途箱')
assertEqual(classifyOrderReceiveFailure('ORDER_NOT_FOUND'), 'notFound', '404 订单不在范围内')
assertEqual(classifyOrderReceiveFailure('PACKING_NOT_ALLOWED'), 'other', '其他错误码')
assertEqual(classifyOrderReceiveFailure(undefined), 'other', '没有错误码')

assertEqual(resolveActualInTransitCount({ actualInTransitCartonCount: 5 }, 3), 5, '用后端带回的实际在途数')
assertEqual(resolveActualInTransitCount({ actualInTransitCartonCount: 0 }, 3), 0, '实际在途 0 也要如实提示')
assertEqual(resolveActualInTransitCount({}, 3), 3, '没带回时用当前读到的在途数')
assertEqual(resolveActualInTransitCount(null, 3), 3, '没有 data 时用当前读到的在途数')
assertEqual(resolveActualInTransitCount({ actualInTransitCartonCount: -1 }, 3), 3, '非法值回退')

const receiveNormalized = normalizePackOrderReceive({ orderGuid: 'o-1', storeCode: '1008' } as PackOrderReceive)
assertDeepEqual(
  [receiveNormalized.cartons, receiveNormalized.products, receiveNormalized.inTransitCount, receiveNormalized.receivedCount],
  [[], [], 0, 0],
  '按订单签收返回缺字段时补默认值',
)
assertEqual(receiveNormalized.actualInTransitCartonCount, undefined, '可空字段保持缺失')

// ---- 存放位置名称 ----
assertEqual(normalizeLocationName('  后仓 A 架 '), '后仓 A 架', '去掉首尾空白')
assertEqual(normalizeLocationName('   '), null, '空名称无效')
assertEqual(normalizeLocationName('x'.repeat(51)), null, '超过 50 个字无效')
assertEqual(normalizeLocationName('x'.repeat(50)), 'x'.repeat(50), '50 个字有效')

console.log('packingLogic.test: ok')
