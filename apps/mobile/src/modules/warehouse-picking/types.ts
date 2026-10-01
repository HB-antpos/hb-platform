/** 拣货会话状态，与后端 WarehouseOrderPickSessionStatuses 一致。 */
export const PICK_SESSION_STATUS = { picking: 1, submitted: 2 } as const;

/** 拣货记录来源，与后端 WarehouseOrderPickSources 一致（手动改总数走单独接口）。 */
export const PICK_SOURCE = { scan: 1, increment: 2, decrement: 3 } as const;

/** 扫码按什么匹配上的，与后端 WarehouseOrderPickMatchKinds 一致。 */
export const PICK_MATCH = {
  barcode: 1,
  itemNumber: 2,
  productCode: 3,
  multiCode: 4,
  setChild: 5,
} as const;

/** “货位没货”原因，与后端 WarehouseOrderPickStockoutReasons 一致。 */
export const PICK_STOCKOUT_REASON = { locationEmpty: 1, wrongProduct: 2, damaged: 3 } as const;

export type PickOrderFilter = "mine" | "all" | "toPick" | "picking";

/**
 * 拣货范围：我的（经理派给我或我扫分单领取的那一段）/ 全部 / 有货位 / 无货位（未绑定配货位）。
 * “我的”只在订单有拣货分配时出现。
 */
/** help：先拣完的人去帮另一段，只看那一段；只在有分配的订单上临时出现，不存偏好。 */
export type PickScope = "mine" | "help" | "all" | "located" | "unlocated";

/** 走位方式：M 型每排同一端进出、列号全部从小到大（默认）；S 型单数排从小到大、双数排从大到小。 */
export type PickRoute = "m" | "s";

/** 仍有效的“货位没货”标记。 */
export interface PickStockout {
  reason: number;
  markedByName: string;
  markedAtUtc: string;
  pickedAtMark: number;
}

/** 当前拣货人：账号本人，或扫员工码换来的短期凭证。 */
export interface PickerIdentity {
  userGuid: string;
  name: string;
  method: "account" | "staffBarcode";
  /** 扫员工码得到的签名凭证；账号本人拣货时为 null。 */
  ticket: string | null;
  expiresAtUtc: string | null;
}

export interface PickedByEntry {
  pickerUserGuid: string;
  pickerName: string;
  quantity: number;
}

export interface PickSetChild {
  productCode: string;
  itemNumber: string | null;
  barcode: string | null;
  productName: string | null;
}

export interface PickSheetLine {
  detailGuid: string;
  productCode: string;
  itemNumber: string | null;
  barcode: string | null;
  productName: string | null;
  productImage: string | null;
  locationCode: string | null;
  orderedQuantity: number;
  /** 中包数（WarehouseProduct.MinOrderQuantity）；为空或 ≤0 时扫码不能按中包累加。 */
  minOrderQuantity: number | null;
  isSet: boolean;
  setChildren: PickSetChild[];
  pickedTotal: number;
  pickedBy: PickedByEntry[];
  stockout: PickStockout | null;
  /** 经理派单的负责人与所属分段；没有分配时为 null，分段待领取时负责人为 null 而段号有值。 */
  assigneeUserGuid: string | null;
  assigneeName: string | null;
  assignmentSegmentNo: number | null;
}

export interface PickCodeEntry {
  code: string;
  target: "line" | "location";
  detailGuids: string[];
  matchedBy: number | null;
  /** 套装子码 / 多码对应的子项名称，用于扫码提示“扫到了哪个子项”。 */
  label: string | null;
}

export interface PickParticipant {
  pickerUserGuid: string;
  pickerName: string;
  lastActiveAtUtc: string;
  lastDetailGuid: string | null;
}

export interface PickSessionInfo {
  status: number;
  startedAtUtc: string;
  startedByName: string;
  submittedAtUtc: string | null;
  submittedByName: string | null;
}

export interface PickSheet {
  orderGuid: string;
  orderNo: string | null;
  storeCode: string | null;
  storeName: string | null;
  orderDate: string | null;
  flowStatus: number;
  session: PickSessionInfo;
  lines: PickSheetLine[];
  codes: PickCodeEntry[];
  participants: PickParticipant[];
  serverTimeUtc: string;
}

export interface PickProgressLine {
  detailGuid: string;
  pickedTotal: number;
  pickedBy: PickedByEntry[];
  minOrderQuantity: number | null;
  stockout: PickStockout | null;
  assigneeUserGuid: string | null;
  assigneeName: string | null;
  assignmentSegmentNo: number | null;
}

export interface PickProgress {
  session: PickSessionInfo;
  lines: PickProgressLine[];
  participants: PickParticipant[];
  serverTimeUtc: string;
}

export interface PickLineMutationResult {
  line: PickProgressLine;
  appliedDelta: number;
  duplicate: boolean;
}

export interface PickOrderListItem {
  orderGuid: string;
  orderNo: string | null;
  storeCode: string | null;
  storeName: string | null;
  orderDate: string | null;
  flowStatus: number;
  lineCount: number;
  totalQuantity: number;
  pickedLineCount: number;
  sessionStatus: number | null;
  pickers: { pickerUserGuid: string; pickerName: string }[];
  /** 经理派单的各段负责人（按段号）；pickerUserGuid 为空表示该段待领取。 */
  assignees: PickOrderAssignee[];
}

export interface PickOrderAssignee {
  pickerUserGuid: string | null;
  pickerName: string | null;
  lineCount: number;
  segmentNo: number;
}

export interface PickOrderList {
  items: PickOrderListItem[];
  /** mine 为空表示服务端认不出拣货人（不显示“派给我”）。 */
  counts: { all: number; toPick: number; picking: number; mine: number | null };
}

/** 扫分单领取的结果：打开哪张单、看哪一段，以及这段现在归谁。 */
export interface PickSlipClaim {
  orderGuid: string;
  orderNo: string | null;
  segmentNo: number;
  segmentCount: number;
  pickerUserGuid: string | null;
  pickerName: string | null;
  lineCount: number;
  claimedByMe: boolean;
  claimedNow: boolean;
}

export interface PickerResolveResult {
  pickerUserGuid: string;
  pickerName: string;
  roleLabel: string | null;
  ticket: string;
  expiresAtUtc: string;
}

export interface PickSubmitResult {
  submittedAtUtc: string;
  submittedByName: string;
  lineCount: number;
  shortLineCount: number;
  overLineCount: number;
}

export interface PickCodeLookupResult {
  productCode: string;
  productName: string | null;
  itemNumber: string | null;
  barcode: string | null;
  locationCode: string | null;
  productImage: string | null;
}
