import type { PackCartonDetail } from "../packing/types";

// 拣货与装箱联动（二期）的类型，与后端 WarehousePickingPackProgressDto / WarehousePickingCartonSyncDto 等对应。
// 后端统一 WhenWritingNull，空字段在归一化时补成 null。

/** 箱状态沿用打包模块：1 装箱中 / 2 已封箱 / 3 在途（作废的箱不返回）。 */
export const PACK_CARTON_STATUS = { packing: 1, sealed: 2, inTransit: 3 } as const;

export interface PackProgressLine {
  productCode: string;
  productName: string | null;
  itemNumber: string | null;
  /** 订货件数（订单行订货数量合计，退货行不计）。 */
  orderedPieces: number;
  /** 拣货记录合计。 */
  pickedPieces: number;
  /** 本订单全部非作废箱里该商品的件数合计。 */
  packedPieces: number;
}

/** 装箱中的箱里一个商品的件数（pack-progress 里 status = 1 的箱才带）。 */
export interface PackProgressCartonItem {
  productCode: string;
  quantity: number;
}

export interface PackProgressCarton {
  cartonGuid: string;
  cartonNo: string;
  status: number;
  skuCount: number;
  pieceCount: number;
  palletNo: string | null;
  createdByName: string;
  /** 建箱时间（ISO，UTC）；旧后端没有。 */
  createdAtUtc: string | null;
  /** 箱是当前拣货人建的；认不出拣货人时全为 false。 */
  isMine: boolean;
  /**
   * 装箱中的箱的全部商品件数（不论谁建的，空箱为 []）；已封 / 在途的箱、以及旧后端没有该字段时为 null。
   * 有它才能知道接着用的旧箱里各商品现有多少件。
   */
  items: PackProgressCartonItem[] | null;
}

export interface PackProgress {
  orderGuid: string;
  orderNo: string | null;
  storeCode: string | null;
  storeName: string | null;
  orderDate: string | null;
  flowStatus: number;
  /** 1 拣货中 / 2 已提交；还没开始拣货为 null。 */
  sessionStatus: number | null;
  lines: PackProgressLine[];
  cartons: PackProgressCarton[];
  /** 装箱中的箱数（含空箱；提交时空箱自动作废，其余装箱中的箱拦住提交）。 */
  openCartonCount: number;
  emptyCartonCount: number;
  sealedCartonCount: number;
  /** 每个商品拣 = 装，且没有非空的装箱中的箱。 */
  reconciled: boolean;
}

/** 边拣边装：带 cartonGuid 的拣货写入成功后，箱的汇总与该商品在箱里的件数。 */
export interface CartonSync {
  cartonGuid: string;
  cartonNo: string;
  skuCount: number;
  pieceCount: number;
  /** 这次涉及的商品在该箱里现有的件数（CARTON_QTY_INSUFFICIENT 时是箱内现有件数）。 */
  itemPieces: number;
}

export interface PackOpenCarton {
  cartonGuid: string;
  cartonNo: string;
  skuCount: number;
  pieceCount: number;
}

/** 提交对账不一致的商品：diff = picked - packed（正：拣多了没装进箱；负：箱里装多了）。 */
export interface PackMismatch {
  productCode: string;
  productName: string | null;
  itemNumber: string | null;
  /** 本订单上该商品的订单行；商品只在箱里、不在订单上时为空。 */
  detailGuids: string[];
  picked: number;
  packed: number;
  diff: number;
}

/** 提交被装箱拦下的两种原因（后端 409 的 data）。 */
export type PackSubmitBlock =
  | { kind: "openCartons"; openCartons: PackOpenCarton[] }
  | { kind: "notReconciled"; mismatches: PackMismatch[] };

/** 先拣后装：设置某商品在箱里的绝对件数后的结果。 */
export interface PackSetItemResult {
  carton: PackCartonDetail;
  overPacked: boolean;
  overPackedBy: number;
}

/** 当前正在装的箱（客户端状态）。 */
export interface CurrentCarton {
  cartonGuid: string;
  cartonNo: string;
  skuCount: number;
  pieceCount: number;
  /** 已知的该箱各商品件数：商品编码（小写）→ 件数。 */
  items: Record<string, number>;
  /**
   * items 是否覆盖整只箱：自己新建的空箱、或刚拿到完整箱详情时为 true；
   * 接着用以前装过的箱时为 false，没出现在 items 里的商品件数未知（不能当 0）。
   */
  itemsComplete: boolean;
  /** 建箱 / 封箱返回的完整箱数据，用来画标签预览；接着用的旧箱没有。 */
  base: PackCartonDetail | null;
}
