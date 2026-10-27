import type { SqliteConnectionPort } from "./types";

/** 本机已提交的一条退货明细（来自 local_return_records），供退货查单合并未同步的退货。 */
export type LocalReturnRecord = Readonly<{
  returnOrderGuid: string;
  originalOrderGuid: string;
  originalOrderDetailGuid: string | null;
  returnQuantity: number;
  returnAmountCents: number;
  /** 退货单是否已被服务端确认（local_orders.state = Synced）。 */
  synced: boolean;
}>;

export interface LocalReturnRecordReader {
  /**
   * 读取本机针对某原单已提交的退货明细。
   * unsyncedOnly=true 时只返回尚未同步成功的退货单（在线查单合并用，已同步的以服务端为准）；
   * false 返回全部（本地降级用，此时看不到服务端的任何退货记录）。
   */
  listForOriginalOrder(input: Readonly<{
    originalOrderGuid: string;
    unsyncedOnly: boolean;
  }>): Promise<readonly LocalReturnRecord[]>;
}

type LocalReturnRecordRow = Readonly<{
  return_order_guid: unknown;
  original_order_guid: unknown;
  original_order_detail_guid: unknown;
  return_quantity: unknown;
  return_amount_cents: unknown;
  state: unknown;
}>;

/** 只读：不预留、不扣减容量，容量裁决仍由退货账本与容量表在事务内完成。 */
export class SqliteLocalReturnRecordReader implements LocalReturnRecordReader {
  readonly #connection: SqliteConnectionPort;

  public constructor(connection: SqliteConnectionPort) {
    this.#connection = connection;
  }

  public async listForOriginalOrder(input: Readonly<{
    originalOrderGuid: string;
    unsyncedOnly: boolean;
  }>): Promise<readonly LocalReturnRecord[]> {
    const rows = await this.#connection.getAll<LocalReturnRecordRow>(
      `SELECT record.return_order_guid, record.original_order_guid,
        record.original_order_detail_guid, record.return_quantity,
        record.return_amount_cents, orders.state
       FROM local_return_records record
       JOIN local_orders orders ON orders.order_guid = record.return_order_guid
       WHERE record.original_order_guid = ?
       ORDER BY record.created_at_iso, record.return_detail_guid`,
      [input.originalOrderGuid],
    );
    const records = rows.map(mapRow);
    return input.unsyncedOnly
      ? records.filter((record) => !record.synced)
      : records;
  }
}

function mapRow(row: LocalReturnRecordRow): LocalReturnRecord {
  return Object.freeze({
    returnOrderGuid: requiredText(row.return_order_guid, "return order guid"),
    originalOrderGuid: requiredText(row.original_order_guid, "original order guid"),
    originalOrderDetailGuid:
      typeof row.original_order_detail_guid === "string" &&
      row.original_order_detail_guid.trim()
        ? row.original_order_detail_guid.trim()
        : null,
    returnQuantity: positiveInteger(row.return_quantity, "return quantity"),
    returnAmountCents: positiveInteger(row.return_amount_cents, "return amount"),
    synced: row.state === "Synced",
  });
}

function requiredText(value: unknown, label: string): string {
  if (typeof value !== "string" || !value.trim()) {
    throw new TypeError(`Invalid local return record ${label}.`);
  }
  return value.trim();
}

function positiveInteger(value: unknown, label: string): number {
  const parsed = typeof value === "string" ? Number(value) : value;
  if (typeof parsed !== "number" || !Number.isSafeInteger(parsed) || parsed <= 0) {
    throw new TypeError(`Invalid local return record ${label}.`);
  }
  return parsed;
}
