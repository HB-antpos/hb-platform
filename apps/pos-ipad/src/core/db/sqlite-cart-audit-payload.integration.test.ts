import assert from "node:assert/strict";
import { DatabaseSync, type SQLInputValue } from "node:sqlite";
import test from "node:test";

import {
  AuthorizedSalesOperationExecutor,
  type SalesOperationSecurity,
} from "@hb/pos-domain/features/sales/runtime/sales-operation-security";
import type { SqliteConnectionPort, SqlRunResult, SqlValue } from "@hb/pos-db/core/db/types";

import { PricingCart } from "@/features/sales/domain";

import { applyMigrations } from "./migrations";
import { createSqliteRepositories } from "./sqlite-repositories";

const NOW = "2026-08-01T00:00:00.000Z";

/**
 * 回归：购物车操作审计的每个商品项都带 referenceCode（目录同步引用码）。
 * 审计仓库的敏感键校验曾用无锚点子串正则，referenceCode 含 "reference" 而被误拒；
 * 而 AuthorizedSalesOperationExecutor 会吞掉审计异常（审计失败不能回滚已完成的购物车动作），
 * 结果 iPad 上改价、折扣、删行、清车、加购的成功审计全部静默不落库。
 * 这里用真实的 SQLite 审计仓库 + 真实执行器验证，避免单测里的内存假审计掩盖该问题。
 */
test("购物车操作审计经真实仓库落库，商品项的 referenceCode 不被敏感键校验误拒", async () => {
  const { repositories } = await createHarness();
  const cart = new PricingCart();
  const executor = createExecutor(repositories.audit);

  await executor.runCartMutation({
    permissionCode: "Permissions.PosTerminal.Sales.AddItem",
    action: "add-item",
    eventType: "CART_ITEM_ADD",
    getCart: () => cart.snapshot(),
    operation: () => {
      cart.addItem({
        lineId: "line-1",
        productCode: "P1",
        itemNumber: "100",
        lookupCode: "930000000001",
        displayName: "Tea",
        unitPrice: { currency: "AUD", cents: 500 },
        syncProvenance: { referenceCode: "REF-100", priceSource: 0 },
      });
    },
  });

  const pending = await repositories.audit.listPending(10);
  assert.equal(pending.length, 1, "成功的加购必须留下一条审计");
  assert.equal(pending[0]?.eventType, "CART_ITEM_ADD");
  const payload = pending[0]?.payload as { items: readonly { referenceCode: string | null }[] };
  assert.equal(payload.items[0]?.referenceCode, "REF-100");
});

test("改价与清车的审计同样落库", async () => {
  const { repositories } = await createHarness();
  const cart = new PricingCart();
  const executor = createExecutor(repositories.audit);
  cart.addItem({
    lineId: "line-1",
    productCode: "P1",
    itemNumber: null,
    lookupCode: "1",
    displayName: "Tea",
    unitPrice: { currency: "AUD", cents: 500 },
    syncProvenance: { referenceCode: "REF-1", priceSource: 0 },
  });

  await executor.runCartMutation({
    permissionCode: "Permissions.PosTerminal.Sales.ChangePrice",
    action: "set-line-unit-price",
    eventType: "CART_ITEM_PRICE_CHANGE",
    getCart: () => cart.snapshot(),
    operation: () => cart.setLineUnitPrice("line-1", { currency: "AUD", cents: 450 }),
  });
  await executor.runCartMutation({
    permissionCode: "Permissions.PosTerminal.Sales.ClearCart",
    action: "clear-cart",
    eventType: "CART_CLEAR",
    getCart: () => cart.snapshot(),
    operation: () => cart.removeLine("line-1"),
  });

  const types = (await repositories.audit.listPending(10)).map((event) => event.eventType);
  assert.deepEqual([...types].sort(), ["CART_CLEAR", "CART_ITEM_PRICE_CHANGE"]);
});

test("审计仓库仍然拒绝真正敏感的键名", async () => {
  const { repositories } = await createHarness();
  const sensitiveKeys = [
    "reference",
    "voucherReference",
    "paymentToken",
    "cardNumber",
    "sessionId",
    "txnRef",
    "authorizationCode",
  ];
  for (const key of sensitiveKeys) {
    await assert.rejects(
      () =>
        repositories.audit.append([
          {
            eventId: `evt-${key}`,
            eventType: "CART_CLEAR",
            occurredAtIso: NOW,
            orderGuid: null,
            correlationId: `corr-${key}`,
            payload: { outcome: "Succeeded", [key]: "x" },
          },
        ]),
      /Sensitive audit payload key is not allowed/,
      `键 ${key} 必须被拒绝`,
    );
  }
  assert.equal((await repositories.audit.listPending(10)).length, 0);
});

function createExecutor(
  audit: SalesOperationSecurity["audit"],
): AuthorizedSalesOperationExecutor {
  let next = 0;
  const id = () => `00000000-0000-4000-8000-${String(++next).padStart(12, "0")}`;
  return new AuthorizedSalesOperationExecutor(
    {
      authorization: {
        async authorizeAndRun(input, operation) {
          return {
            authorized: true,
            value: await operation({
              authorizationMode: "current-cashier",
              requestingCashierId: "C1",
              authorizingCashierId: null,
              permissionCode: input.permissionCode,
            }),
          };
        },
      },
      audit,
      createActionId: id,
      createAuditEventId: id,
      nowIso: () => NOW,
    },
    { cashierId: "C1", cashierName: "Alice", userGuid: null },
    { assertActive(): void {} },
  );
}

async function createHarness() {
  const connection = new NodeSqliteConnection(
    new DatabaseSync(":memory:", { enableForeignKeyConstraints: true }),
  );
  await applyMigrations(connection, () => NOW);
  const repositories = createSqliteRepositories(connection, {
    nowIso: () => NOW,
    createLeaseId: () => "unused-lease",
    auditScope: { storeCode: "S1", deviceCode: "IPAD-1" },
    encryptor: {
      async encrypt(value) { return new TextEncoder().encode(value); },
      async decrypt(value) { return new TextDecoder().decode(value); },
    },
  });
  return { connection, repositories };
}

class NodeSqliteConnection implements SqliteConnectionPort {
  private transactionActive = false;

  public constructor(private readonly database: DatabaseSync) {
    this.database.exec("PRAGMA foreign_keys = ON;");
  }

  public async exec(sql: string): Promise<void> {
    this.database.exec(sql);
  }

  public async run(
    sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<SqlRunResult> {
    const result = this.database
      .prepare(sql)
      .run(...parameters as readonly SQLInputValue[]);
    return {
      changes: Number(result.changes),
      lastInsertRowId: Number(result.lastInsertRowid),
    };
  }

  public async getFirst<T extends object>(
    sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<T | null> {
    const row = this.database
      .prepare(sql)
      .get(...parameters as readonly SQLInputValue[]);
    return row === undefined ? null : row as T;
  }

  public async getAll<T extends object>(
    sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<readonly T[]> {
    return this.database
      .prepare(sql)
      .all(...parameters as readonly SQLInputValue[]) as unknown as readonly T[];
  }

  public async withExclusiveTransaction<T>(
    operation: (transaction: SqliteConnectionPort) => Promise<T>,
  ): Promise<T> {
    if (this.transactionActive) throw new Error("Nested test transaction.");
    this.transactionActive = true;
    this.database.exec("BEGIN IMMEDIATE");
    try {
      const result = await operation(this);
      this.database.exec("COMMIT");
      return result;
    } catch (error) {
      this.database.exec("ROLLBACK");
      throw error;
    } finally {
      this.transactionActive = false;
    }
  }

  public async close(): Promise<void> {
    this.database.close();
  }
}
