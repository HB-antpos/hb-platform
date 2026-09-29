import {
  SqliteSharedHeldOrderClaimRepository,
  type SharedHeldOrderClaimRepositoryPort,
  type SharedPayloadEncryptorPort,
} from "../../features/shared-held-orders/shared-held-order-claim-repository";
import {
  SqliteSharedHeldOrderLocalPublication,
  type SharedHeldOrderLocalPublicationPort,
} from "../../features/shared-held-orders/shared-held-order-local-publication";
import {
  SqliteSharedHeldOrderPublicationQueue,
  type SharedHeldOrderPublicationQueuePort,
} from "../../features/shared-held-orders/shared-held-order-publication-queue";
import {
  freezeAuditScope,
  type AuditScope,
} from "@hb/pos-domain/core/contracts/audit-scope";
import { normalizeLineSyncProvenance } from "@hb/pos-domain/core/contracts/line-sync-provenance";
import type {
  ApprovedPaymentOrderCommit,
  ApprovedPaymentOrderCommitResult,
  CashFulfilmentDraft,
  CompleteCashOrderCommand,
  DurableCashOrderCommit,
  DurableCashOrderCommitResult,
  AuditEventDraft,
  LocalOrder,
  RecalledHoldCompletion,
} from "@hb/pos-domain/core/contracts/order";
import type { AppUpdateCacheScope } from "../contracts/ota-app-updates";
import type {
  DatabasePort,
  DatabaseTransactionPort,
  ApprovedPaymentOrderCommitPort,
  DurableCashOrderCommitPort,
} from "@hb/pos-domain/core/contracts/repositories";
import type { TerminalCartFence } from "@hb/pos-domain/core/contracts/terminal-cart";
import { SqliteApplicationLogOutbox } from "../logging/application-log";

import { SqliteCatalogCodeConflictRepository } from "./catalog-code-conflict-repository";
import { SqliteCatalogLookupOverlayRepository } from "./catalog-lookup-overlay-repository";
import { SqliteCatalogSnapshotRepository } from "./catalog-repository";
import { applyMigrations } from "./migrations";
import { PosIpadOtaUpdatePolicyRepository } from "./pos-ipad-ota-update-policy-repository";
import { PosIpadUpdatePolicyRepository } from "./pos-ipad-update-policy-repository";
import { PosSettingsRepository } from "./pos-settings-repository";
import { PaymentMethodSettingsRepository } from "@/features/settings/payment-method-settings";
import { ReceiptCompletionSettlementRepository } from "./receipt-completion-settlement-repository";
import {
  SqliteAttendanceSecurityFacade,
  type AttendanceSecurityTerminalScope,
} from "./sqlite-attendance-security-repository";
import { SqliteDailyCloseRepository } from "@hb/pos-db/core/db/sqlite-daily-close-repository";
import { SqliteOrderSyncStatusRepository } from "@hb/pos-db";
import {
  SqliteFulfilmentStore,
  type PersistedDrawerEventInput,
  type PersistedPrintJobInput,
} from "./sqlite-fulfilment-store";
import { SqliteInstallmentActionStore } from "./sqlite-installment-action-store";
import { SqliteInstallmentPaymentPersistenceFacade } from "./sqlite-installment-payment-persistence";
import { SqliteInstallmentSnapshotRepository } from "./sqlite-installment-snapshot-repository";
import {
  SqliteLocalHistoryStore,
  type LocalHistoryStoreScope,
} from "./sqlite-local-history-store";
import { SqliteLocalSyncHistoryStore } from "./sqlite-local-sync-history-store";
import { SqliteMixedPaymentOrderTruthStore } from "./sqlite-mixed-payment-order-truth-store";
import {
  SqliteMixedPaymentTenderStore,
  type MixedCashFinalCompletionDependencies,
  type MixedPaymentPersistenceIds,
} from "./sqlite-mixed-payment-tender-store";
import {
  SqliteOfflineReturnCapacity,
  type OfflineReturnCapacityFacade,
} from "@hb/pos-db/core/db/sqlite-offline-return-capacity";
import {
  SqliteOperationAuditRead,
  type OperationAuditLocalScope,
} from "@hb/pos-db/core/db/sqlite-operation-audit-read";
import { SqliteOrderSyncMaterialResolver } from "./sqlite-order-sync-material";
import { SqliteManualPaymentOrderCommitter } from "./sqlite-manual-payment-committer";
import { SqlitePaymentActionBindingStore } from "./sqlite-payment-action-binding-store";
import {
  SqlitePaymentDraftRecoveryStore,
  type PaymentDraftPersistenceIds,
} from "./sqlite-payment-draft-recovery-store";
import { SqlitePaymentRecoveryCenterStore } from "./sqlite-payment-recovery-center-store";
import { SqlitePaymentProtectedMaterialReader } from "@hb/pos-db/core/db/sqlite-payment-protected-material";
import { SqliteRefundVoucherPrintMaterial } from "./sqlite-refund-voucher-print-material";
import {
  createSqliteRepositories,
  persistRecalledHoldOrderSourceAndClaim,
  type PosRepositoryBundle,
  type SensitivePayloadEncryptor,
} from "./sqlite-repositories";
import { SqliteReturnApiAttemptStore } from "./sqlite-return-api-attempt-store";
import { SqliteReturnCapacityVault } from "./sqlite-return-capacity-vault";
import {
  SqliteReturnExecutionLedger,
  type ReturnExecutionPersistenceIds,
} from "./sqlite-return-execution-ledger";
import { SqliteReturnFulfilmentPlanStore } from "./sqlite-return-fulfilment-plan-store";
import { SqliteSettingsSafetyRepository } from "@hb/pos-db/core/db/sqlite-settings-safety-repository";
import { SqliteSpecialProductsRepository } from "@hb/pos-db/core/db/sqlite-special-products-repository";
import { SqliteVoucherBalanceMaterialStore } from "./sqlite-voucher-balance-material";
import { SqliteVoucherPreparationStore } from "./sqlite-voucher-preparation-store";
import { SqliteVoucherProtectedTokenStore } from "./sqlite-voucher-protected-token-store";
import {
  SqliteVoucherTenderReversalStore,
  type VoucherTenderReversalPersistenceIds,
} from "./sqlite-voucher-tender-reversal-store";
import type { PosDatabaseOptions, SqliteConnectionPort } from "@hb/pos-db/core/db/types";

import type { LocalSyncHistorySupportContext } from "@/features/sync-history";

type LocalSequenceRow = Readonly<{ next_sequence: number | string }>;
type SqlCipherVersionRow = Readonly<{ cipher_version?: unknown }>;

export class PosDatabase implements DatabasePort {
  private constructor(
    private readonly connection: SqliteConnectionPort,
    private readonly nowIso: () => string,
  ) {}

  public static async open(options: PosDatabaseOptions): Promise<PosDatabase> {
    const key = await options.keyProvider.getOrCreateDatabaseKey();
    if (!key) {
      throw new Error("SQLCipher database key is unavailable.");
    }
    const keyPragma = createSqlCipherKeyPragma(key);

    const connection = await options.driver.open(options.databaseName);
    try {
      await runDatabaseOpenStep("key", () => connection.exec(keyPragma));
      await runDatabaseOpenStep("cipher version", async () => {
        // 普通 SQLite 会静默接受 PRAGMA key；必须以非空版本确认 SQLCipher 真正启用。
        const row = await connection.getFirst<SqlCipherVersionRow>(
          "PRAGMA cipher_version;",
        );
        if (
          typeof row?.cipher_version !== "string" ||
          row.cipher_version.trim().length === 0
        ) {
          throw new Error("SQLCipher cipher_version is unavailable.");
        }
      });
      await runDatabaseOpenStep("key verification", () =>
        connection.getFirst("SELECT count(*) AS object_count FROM sqlite_master;"),
      );
      await runDatabaseOpenStep("cipher memory security", () =>
        connection.exec("PRAGMA cipher_memory_security = ON;"),
      );
      await runDatabaseOpenStep("foreign keys", () =>
        connection.exec("PRAGMA foreign_keys = ON;"),
      );
      await runDatabaseOpenStep("WAL", () =>
        connection.exec("PRAGMA journal_mode = WAL;"),
      );
      await runDatabaseOpenStep("busy timeout", () =>
        connection.exec("PRAGMA busy_timeout = 5000;"),
      );
      await applyMigrations(connection, options.nowIso);
      return new PosDatabase(connection, options.nowIso);
    } catch (error) {
      await connection.close();
      throw error;
    }
  }

  /**
   * 所有订单写入都必须借由独占事务执行，避免支付完成时的并发插入破坏账本。
   * 订单领域实现将在 Wave 2 注入具体 transaction repository。
   */
  public runInTransaction<T>(operation: (transaction: DatabaseTransactionPort) => Promise<T>): Promise<T> {
    return this.connection.withExclusiveTransaction(async (transaction) =>
      operation(new PosDatabaseTransaction(transaction, this.nowIso)),
    );
  }

  /** 递增值由单条 UPDATE ... RETURNING 取得，不依赖可回拨的设备时钟。 */
  public async nextLocalSequence(): Promise<number> {
    return this.connection.withExclusiveTransaction(async (transaction) => {
      await transaction.run(
        `INSERT INTO app_settings (setting_key, setting_value, updated_at_iso)
         VALUES ('local_sequence', '0', '1970-01-01T00:00:00.000Z')
         ON CONFLICT(setting_key) DO NOTHING`,
      );
      const row = await transaction.getFirst<LocalSequenceRow>(
        `UPDATE app_settings
         SET setting_value = CAST(setting_value AS INTEGER) + 1
         WHERE setting_key = 'local_sequence'
         RETURNING setting_value AS next_sequence`,
      );
      const sequence = Number(row?.next_sequence);
      if (!Number.isSafeInteger(sequence) || sequence <= 0) {
        throw new Error("Unable to allocate a valid local sequence.");
      }
      return sequence;
    });
  }

  public close(): Promise<void> {
    return this.connection.close();
  }

  /** 业务层仅取得冻结 Port 的实现，永不取得裸 SQLite connection。 */
  public repositories(
    encryptor: SensitivePayloadEncryptor,
    createLeaseId: () => string,
    auditScope?: AuditScope,
  ): PosRepositoryBundle {
    return createSqliteRepositories(this.connection, {
      nowIso: this.nowIso,
      createLeaseId,
      encryptor,
      ...(auditScope ? { auditScope } : {}),
    });
  }

  /** 程序日志独立于业务 outbox；上传失败不得阻塞订单、支付或员工审计。 */
  public applicationLogOutbox(): SqliteApplicationLogOutbox {
    return new SqliteApplicationLogOutbox(this.connection, this.nowIso);
  }

  /** 顶部状态只读取去重后的订单补传数量，不向 UI 暴露裸 outbox。 */
  public orderSyncStatus(): SqliteOrderSyncStatusRepository {
    return new SqliteOrderSyncStatusRepository(this.connection);
  }

  /** 异常刷卡订单独立于当前购物车保存；页面只能经窄恢复中心端口访问。 */
  public paymentRecoveryCenter(
    createAuditEventId: () => string,
  ): SqlitePaymentRecoveryCenterStore {
    return new SqlitePaymentRecoveryCenterStore(
      this.connection,
      createAuditEventId,
      createAuditEventId,
      this.nowIso,
    );
  }

  /** 人工确认已收款只经专用事务写入 manual tender，不能伪造 provider Approved。 */
  public manualPaymentOrderCommitter(): SqliteManualPaymentOrderCommitter {
    return new SqliteManualPaymentOrderCommitter(this.connection, this.nowIso);
  }

  /**
   * Hbpos 同步前即时恢复受保护支付引用；返回值不缓存、不落库，普通订单仓储保持脱敏。
   */
  public orderSyncMaterial(
    encryptor: SensitivePayloadEncryptor,
    createProtectedReference: () => string,
  ): SqliteOrderSyncMaterialResolver {
    return new SqliteOrderSyncMaterialResolver(this.connection, {
      returnCapacityVault: this.returnCapacityVault(encryptor),
      voucherProtectedTokens: this.voucherProtectedTokens(
        encryptor,
        createProtectedReference,
      ),
      paymentProtectedMaterials:
        this.paymentProtectedMaterials(encryptor),
    });
  }

  /** 目录仅经此仓储访问，feature 层不持有裸 SQLite connection。 */
  public catalogSnapshots(): SqliteCatalogSnapshotRepository {
    return new SqliteCatalogSnapshotRepository(this.connection);
  }

  /** 在线扫码只取得按目录代次隔离的增量覆盖层，不可修改完整目录快照。 */
  public catalogLookupOverlay(): SqliteCatalogLookupOverlayRepository {
    return new SqliteCatalogLookupOverlayRepository(
      this.connection,
      this.nowIso,
    );
  }

  /** 一码多商品候选按门店整体替换，与目录快照表隔离，不参与快照激活与清理。 */
  public catalogCodeConflicts(): SqliteCatalogCodeConflictRepository {
    return new SqliteCatalogCodeConflictRepository(this.connection);
  }

  /** 日结汇总与冻结归档只经专用 facade 访问，feature 不取得审计表或裸连接。 */
  public dailyCloses(): SqliteDailyCloseRepository {
    return new SqliteDailyCloseRepository(this.connection);
  }

  /** 特殊商品全量替换、标记和设备本地顺序统一由同一事务仓储维护。 */
  public specialProducts(): SqliteSpecialProductsRepository {
    return new SqliteSpecialProductsRepository(this.connection);
  }

  /** 分期缓存只提供按门店替换与读取；敏感展示字段始终经二次加密。 */
  public installmentSnapshots(
    encryptor: SensitivePayloadEncryptor,
  ): SqliteInstallmentSnapshotRepository {
    return new SqliteInstallmentSnapshotRepository(
      this.connection,
      encryptor,
    );
  }

  /** 分期 action、冻结命令与恢复状态只经二次加密耐久 store 访问。 */
  public installmentActions(
    encryptor: SensitivePayloadEncryptor,
  ): SqliteInstallmentActionStore {
    return new SqliteInstallmentActionStore(
      this.connection,
      encryptor,
      this.nowIso,
    );
  }

  /** 第二套分期 provider bootstrap 只取得独立账本、券 vault 与来源 vault。 */
  public installmentPaymentPersistence(
    encryptor: SensitivePayloadEncryptor,
    createProtectedReference: () => string,
  ): SqliteInstallmentPaymentPersistenceFacade {
    return new SqliteInstallmentPaymentPersistenceFacade(
      this.connection,
      encryptor,
      createProtectedReference,
      this.nowIso,
    );
  }

  /** 考勤 QR 与紧急登录安全状态仅按完整终端授权 scope 暴露三个既有窄 Port。 */
  public attendanceSecurity(
    encryptor: SensitivePayloadEncryptor,
    terminal: AttendanceSecurityTerminalScope,
  ): SqliteAttendanceSecurityFacade {
    return new SqliteAttendanceSecurityFacade(
      this.connection,
      encryptor,
      terminal,
      this.nowIso,
    );
  }

  /** 本机操作审计固定在当前门店设备，原始 payload_json 不向 feature 暴露。 */
  public operationAudits(
    scope: OperationAuditLocalScope,
  ): SqliteOperationAuditRead {
    return new SqliteOperationAuditRead(this.connection, scope);
  }

  /** 设置仅经受类型约束的 facade 访问，业务层不能读写任意 app_settings JSON。 */
  public settings(): PosSettingsRepository {
    return new PosSettingsRepository(this.connection, this.nowIso);
  }

  public paymentMethodSettings(): PaymentMethodSettingsRepository {
    return new PaymentMethodSettingsRepository(this.connection, this.nowIso);
  }

  /** Settings 危险动作只读取一致风险计数，不取得订单、支付、队列或裸连接。 */
  public settingsSafety(): SqliteSettingsSafetyRepository {
    return new SqliteSettingsSafetyRepository(this.connection);
  }

  /** 更新策略缓存只经窄 facade 访问，运行时不得读写任意 app_settings。 */
  public appUpdatePolicy(
    scope: AppUpdateCacheScope,
  ): PosIpadUpdatePolicyRepository {
    return new PosIpadUpdatePolicyRepository(
      this.connection,
      this.nowIso,
      scope,
    );
  }

  /** OTA 策略缓存与原生版本策略分离，并固定到门店、runtime 和当前二进制。 */
  public otaUpdatePolicy(
    scope: AppUpdateCacheScope,
  ): PosIpadOtaUpdatePolicyRepository {
    return new PosIpadOtaUpdatePolicyRepository(
      this.connection,
      this.nowIso,
      scope,
    );
  }

  /** 重打只可读取已持久化完成审计中的找零，不向 feature 暴露审计表或裸连接。 */
  public receiptCompletionSettlements(): ReceiptCompletionSettlementRepository {
    return new ReceiptCompletionSettlementRepository(this.connection);
  }

  /** 履约 facade 与订单仓储共用同一已解锁 SQLCipher 连接，但不向 feature 暴露裸连接。 */
  public fulfilmentStore(
    encryptor: SensitivePayloadEncryptor,
    createPrintJobId: () => string,
  ): SqliteFulfilmentStore {
    return new SqliteFulfilmentStore(this.connection, {
      encryptor,
      nowIso: this.nowIso,
      createPrintJobId,
    });
  }

  /**
   * 现金账本与首轮履约任务必须共用一个 SQLCipher 事务。
   * feature 只能取得专用 committer，不能取得已解锁 connection。
   */
  public cashOrderCommitter(
    encryptor: SensitivePayloadEncryptor,
  ): SqliteAtomicCashOrderCommitter {
    return new SqliteAtomicCashOrderCommitter(
      this.connection,
      encryptor,
      this.nowIso,
    );
  }

  /** 离线退款容量只读预检；真正扣减仍由现金 committer 的原子 CAS 完成。 */
  public offlineReturnCapacity(): OfflineReturnCapacityFacade {
    return new SqliteOfflineReturnCapacity(this.connection);
  }

  /** 已批准的卡/券支付只经此 facade 绑定原订单，恢复时绝不新建 OrderGuid。 */
  public paymentOrderCommitter(
    encryptor: SensitivePayloadEncryptor,
  ): SqliteApprovedPaymentOrderCommitter {
    return new SqliteApprovedPaymentOrderCommitter(this.connection, encryptor, this.nowIso);
  }

  /** Mixed payment 只能读取同一 SQLCipher 快照中的订单状态和完整 tender 事实。 */
  public mixedPaymentOrderTruth(): SqliteMixedPaymentOrderTruthStore {
    return new SqliteMixedPaymentOrderTruthStore(this.connection);
  }

  /**
   * Mixed cash/reversal 只经追加式 facade 变更 tender。精确清零必须注入冻结的
   * completion plan；缺少 planner 时失败关闭，绝不留下 zero-balance Completing。
   */
  public mixedPaymentTenders(
    ids: MixedPaymentPersistenceIds,
    finalCash?: MixedCashFinalCompletionDependencies,
  ): SqliteMixedPaymentTenderStore {
    return new SqliteMixedPaymentTenderStore(
      this.connection,
      ids,
      this.nowIso,
      finalCash,
    );
  }

  /** 在线 provider 调用前只经不可变 action binding facade 建立耐久幂等身份。 */
  public paymentActionBindings(): SqlitePaymentActionBindingStore {
    return new SqlitePaymentActionBindingStore(this.connection);
  }

  /** 支付前草稿、崩溃恢复与安全放弃只经同一窄 facade 访问。 */
  public paymentDraftRecovery(
    ids: PaymentDraftPersistenceIds,
  ): SqlitePaymentDraftRecoveryStore {
    return new SqlitePaymentDraftRecoveryStore(
      this.connection,
      ids,
      this.nowIso,
    );
  }

  /** 订单同步仅按完整 attempt 身份即时读取卡证据；不会暴露券 token 或裸连接。 */
  public paymentProtectedMaterials(
    encryptor: SensitivePayloadEncryptor,
  ): SqlitePaymentProtectedMaterialReader {
    return new SqlitePaymentProtectedMaterialReader(
      this.connection,
      encryptor,
    );
  }

  /** 原支付引用只进入二次加密 Vault；退货 feature 只持有不透明 capacityId。 */
  public returnCapacityVault(
    encryptor: SensitivePayloadEncryptor,
  ): SqliteReturnCapacityVault {
    return new SqliteReturnCapacityVault(
      this.connection,
      encryptor,
      this.nowIso,
    );
  }

  /** Hbpos 在线退款调用前先建立 API attempt/idempotency 耐久锚点。 */
  public returnApiAttempts(
    encryptor: SensitivePayloadEncryptor,
  ): SqliteReturnApiAttemptStore {
    return new SqliteReturnApiAttemptStore(this.connection, encryptor);
  }

  /**
   * 退货完整 plan、allocation、Unknown reservation 与最终订单仅经此 facade。
   * provider/API 调用前必须先 prepare action 和绑定对应 durable attempt。
   */
  public returnExecutionLedger(
    encryptor: SensitivePayloadEncryptor,
    ids: ReturnExecutionPersistenceIds,
  ): SqliteReturnExecutionLedger {
    return new SqliteReturnExecutionLedger(
      this.connection,
      encryptor,
      ids,
      this.nowIso,
    );
  }

  /** 已完成退货的冻结计划只能经此入口原子物化为打印和钱箱任务。 */
  public returnFulfilmentPlans(
    encryptor: SensitivePayloadEncryptor,
  ): SqliteReturnFulfilmentPlanStore {
    return new SqliteReturnFulfilmentPlanStore(
      this.connection,
      encryptor,
      this.nowIso,
    );
  }

  /**
   * 券退款回单只取得 getByAttempt 只读能力；即使内部 token store 被误用，
   * 也无法借此入口生成新的受保护引用。
   */
  public refundVoucherPrintMaterial(
    encryptor: SensitivePayloadEncryptor,
  ): SqliteRefundVoucherPrintMaterial {
    const protectedTokens = new SqliteVoucherProtectedTokenStore(
      this.connection,
      encryptor,
      () => {
        throw new Error(
          "Read-only refund voucher material cannot create protected references.",
        );
      },
      this.nowIso,
    );
    return new SqliteRefundVoucherPrintMaterial(
      this.connection,
      protectedTokens,
    );
  }

  /** 券码/退款原因在 attempt 创建前先写受保护上下文并按 action binding 绑定。 */
  public voucherPreparationStore(
    encryptor: SensitivePayloadEncryptor,
    createProtectedReference: () => string,
  ): SqliteVoucherPreparationStore {
    return new SqliteVoucherPreparationStore(
      this.connection,
      encryptor,
      createProtectedReference,
      this.nowIso,
    );
  }

  /** Voucher adapter 的 phase/token 状态只保存在 SQLCipher 二次密文。 */
  public voucherProtectedTokens(
    encryptor: SensitivePayloadEncryptor,
    createProtectedReference: () => string,
  ): SqliteVoucherProtectedTokenStore {
    return new SqliteVoucherProtectedTokenStore(
      this.connection,
      encryptor,
      createProtectedReference,
      this.nowIso,
    );
  }

  /**
   * 订单服务端核销后的礼券余额确认只可更新已存在的受保护 state；
   * 此只读构造入口禁止意外创建新的 protected reference。
   */
  public voucherBalanceMaterials(
    encryptor: SensitivePayloadEncryptor,
  ): SqliteVoucherBalanceMaterialStore {
    const protectedTokens = new SqliteVoucherProtectedTokenStore(
      this.connection,
      encryptor,
      () => {
        throw new Error(
          "Voucher balance material cannot create protected references.",
        );
      },
      this.nowIso,
    );
    return new SqliteVoucherBalanceMaterialStore(
      this.connection,
      protectedTokens,
    );
  }

  /**
   * Voucher tender 只有在受保护 release 事实可验证时，才可由此 facade 原子追加
   * 负 tender、reversal link 与成功审计；feature 永远不能取得裸连接。
   */
  public voucherTenderReversals(
    encryptor: SensitivePayloadEncryptor,
    ids: VoucherTenderReversalPersistenceIds,
  ): SqliteVoucherTenderReversalStore {
    return new SqliteVoucherTenderReversalStore(
      this.connection,
      encryptor,
      ids,
      this.nowIso,
    );
  }

  /** support context 由 runtime 注入；历史 facade 不读取 Keychain 或设备授权。 */
  public localSyncHistory(
    supportContext: LocalSyncHistorySupportContext,
  ): SqliteLocalSyncHistoryStore {
    return new SqliteLocalSyncHistoryStore(
      this.connection,
      this.nowIso,
      supportContext,
    );
  }

  /** 本机历史在 facade 构造时冻结可信门店/设备，feature 无法扩大读取范围。 */
  public localHistory(scope: LocalHistoryStoreScope): SqliteLocalHistoryStore {
    return new SqliteLocalHistoryStore(this.connection, scope);
  }

  /**
   * 跨设备共享挂单运行时仅经此 facade 取得 claim 持久化口；绝不把裸连接交给 feature。
   */
  public sharedHeldOrderClaims(
    encryptor: SharedPayloadEncryptorPort,
  ): SharedHeldOrderClaimRepositoryPort {
    return new SqliteSharedHeldOrderClaimRepository(
      this.connection,
      encryptor,
    );
  }

  /** 发布队列：只读/退避写，绝不返回明文 payload。 */
  public sharedHeldOrderPublicationQueue(): SharedHeldOrderPublicationQueuePort {
    return new SqliteSharedHeldOrderPublicationQueue(this.connection);
  }

  /** 原设备离线 recall 的本地副本读取口（只读，不改变挂单状态）。 */
  public sharedHeldOrderLocalPublication(
    encryptor: SharedPayloadEncryptorPort,
  ): SharedHeldOrderLocalPublicationPort {
    return new SqliteSharedHeldOrderLocalPublication(
      this.connection,
      encryptor,
    );
  }
}

function createSqlCipherKeyPragma(key: string): string {
  if (!/^[0-9a-f]{64}$/.test(key)) {
    throw new Error(
      "SQLCipher database key must be 64 lowercase hexadecimal characters.",
    );
  }
  // Expo SQLite 官方支持的 passphrase 形式最稳定；输入已限制为随机 hex，
  // 不存在引号注入，同时仍保留 256 bit Keychain 熵供 SQLCipher 派生页密钥。
  return `PRAGMA key = '${key}';`;
}

async function runDatabaseOpenStep<T>(
  step: string,
  operation: () => Promise<T>,
): Promise<T> {
  try {
    return await operation();
  } catch (error: unknown) {
    const message = error instanceof Error ? error.message : String(error);
    throw new Error(`SQLCipher ${step} failed: ${message}`);
  }
}

export type AtomicCashFulfilmentDraft = Readonly<{
  print: PersistedPrintJobInput | null;
  drawer: PersistedDrawerEventInput | null;
}>;

/**
 * 完整现金交易唯一的耐久提交点。
 *
 * 小票先在事务外加密；加密失败时不会创建订单。随后订单、行、tender、审计、
 * outbox、打印任务和钱箱事件在同一 BEGIN IMMEDIATE 中提交或整体回滚。
 */
export class SqliteAtomicCashOrderCommitter implements DurableCashOrderCommitPort {
  public constructor(
    private readonly connection: SqliteConnectionPort,
    private readonly encryptor: SensitivePayloadEncryptor,
    private readonly nowIso: () => string,
  ) {}

  public async completeCashOrderWithFulfilment(
    command: CompleteCashOrderCommand,
    fulfilment: AtomicCashFulfilmentDraft,
  ): Promise<void> {
    assertAtomicFulfilment(command, fulfilment);
    assertCashOrder(command);
    const receiptCiphertext = fulfilment.print === null
      ? null
      : await this.encryptor.encrypt(
        JSON.stringify(Array.from(fulfilment.print.receiptBytes)),
      );

    await this.connection.withExclusiveTransaction((transaction) =>
      this.persistCashOrder(transaction, command, fulfilment, receiptCiphertext),
    );
  }

  /**
   * 新收银路径的可崩溃重放提交点。先在 BEGIN IMMEDIATE 内读取 intent，
   * 因而相同确认不会再生成第二个 OrderGuid 或再次安排打印/钱箱任务。
   */
  public async completeDurableCashOrder(
    input: DurableCashOrderCommit,
  ): Promise<DurableCashOrderCommitResult> {
    assertCashCheckoutIntentIdentity(input);
    return this.connection.withExclusiveTransaction(async (transaction) => {
      const existing = await transaction.getFirst<CashCheckoutIntentRow>(
        "SELECT request_signature, order_guid, cash_due_cents, change_cents FROM cash_checkout_intents WHERE checkout_intent_id = ?",
        [input.intent.checkoutIntentId],
      );
      if (existing) {
        if (intentText(existing.request_signature) !== input.intent.requestSignature) {
          throw new Error("Cash checkout intent was replayed with different content.");
        }
        return {
          replayed: true,
          orderGuid: intentText(existing.order_guid),
          cashDueCents: intentInteger(existing.cash_due_cents),
          changeCents: intentInteger(existing.change_cents),
        };
      }

      const recallCompletion = validateNewDurableCashCheckout(input);
      const terminalFence = await readTerminalCartFenceForCheckout(
        transaction,
        input.command.order.storeCode,
        input.command.order.deviceCode,
      );
      validateTerminalFenceForCheckout(
        input,
        recallCompletion,
        terminalFence,
      );
      assertAtomicFulfilment(input.command, input.fulfilment);
      const receiptCiphertext = input.fulfilment.print === null
        ? null
        : await this.encryptor.encrypt(
          JSON.stringify(Array.from(input.fulfilment.print.receiptBytes)),
        );
      await this.persistCashOrder(transaction, input.command, input.fulfilment, receiptCiphertext);
      const completedAtIso = this.nowIso();
      await transaction.run(
        `INSERT INTO cash_checkout_intents (
          checkout_intent_id, request_signature, order_guid, cash_due_cents, change_cents, completed_at_iso
        ) VALUES (?, ?, ?, ?, ?, ?)`,
        [
          input.intent.checkoutIntentId,
          input.intent.requestSignature,
          input.command.order.orderGuid,
          input.intent.cashDueCents,
          input.intent.changeCents,
          completedAtIso,
        ],
      );
      if (recallCompletion) {
        await completeRecalledHoldInCashTransaction(
          transaction,
          input,
          recallCompletion,
        );
      }
      return {
        replayed: false,
        orderGuid: input.command.order.orderGuid,
        cashDueCents: input.intent.cashDueCents,
        changeCents: input.intent.changeCents,
      };
    });
  }

  private async persistCashOrder(
    transaction: SqliteConnectionPort,
    command: CompleteCashOrderCommand,
    fulfilment: AtomicCashFulfilmentDraft | CashFulfilmentDraft,
    receiptCiphertext: Uint8Array | null,
  ): Promise<void> {
    await new PosDatabaseTransaction(transaction, this.nowIso).completeCashOrder(command);
    const createdAtIso = this.nowIso();

    if (fulfilment.print && receiptCiphertext) {
      await transaction.run(
        `INSERT INTO print_jobs (
          job_id, order_guid, state, printer_id, receipt_ciphertext, is_reprint,
          retry_count, last_error_code, created_at_iso, updated_at_iso
        ) VALUES (?, ?, 'Queued', ?, ?, 0, 0, NULL, ?, ?)`,
        [
          fulfilment.print.jobId,
          fulfilment.print.orderGuid,
          fulfilment.print.printerId,
          receiptCiphertext,
          createdAtIso,
          createdAtIso,
        ],
      );
    }

    if (fulfilment.drawer) {
      await transaction.run(
        `INSERT INTO drawer_events (
          event_id, order_guid, printer_id, print_job_id, state, reason, retry_count,
          requested_at_iso, completed_at_iso, last_error_code, created_at_iso, updated_at_iso
        ) VALUES (?, ?, ?, ?, 'Required', ?, 0, NULL, NULL, NULL, ?, ?)`,
        [
          fulfilment.drawer.eventId,
          fulfilment.drawer.orderGuid,
          fulfilment.drawer.printerId,
          fulfilment.drawer.printJobId,
          fulfilment.drawer.reason,
          createdAtIso,
          createdAtIso,
        ],
      );
    }
  }
}

type CashCheckoutIntentRow = Record<string, unknown>;

type ApprovedPaymentAttemptRow = Readonly<{
  attempt_id: unknown;
  order_guid: unknown;
  provider: unknown;
  operation: unknown;
  amount_cents: unknown;
  state: unknown;
}>;

type ApprovedPaymentOrderRow = Readonly<{
  order_guid: unknown;
  state: unknown;
  actual_amount_cents: unknown;
  store_code: unknown;
  device_code: unknown;
}>;

type ApprovedPaymentTenderRow = Readonly<{
  tender_guid: unknown;
  order_guid: unknown;
  method: unknown;
  amount_cents: unknown;
}>;

/**
 * 仅处理已经 Approved 的 attempt。读取、tender 绑定、订单完成与履约都在同一
 * BEGIN IMMEDIATE 中，避免“已扣款但另建订单”或“重复恢复重复打印”。
 */
export class SqliteApprovedPaymentOrderCommitter implements ApprovedPaymentOrderCommitPort {
  public constructor(
    private readonly connection: SqliteConnectionPort,
    private readonly encryptor: SensitivePayloadEncryptor,
    private readonly nowIso: () => string,
  ) {}

  public completeApprovedPaymentOrder(
    input: ApprovedPaymentOrderCommit,
  ): Promise<ApprovedPaymentOrderCommitResult> {
    return this.connection.withExclusiveTransaction((transaction) => this.complete(transaction, input));
  }

  private async complete(
    transaction: SqliteConnectionPort,
    input: ApprovedPaymentOrderCommit,
  ): Promise<ApprovedPaymentOrderCommitResult> {
    const attempt = await transaction.getFirst<ApprovedPaymentAttemptRow>(
      "SELECT attempt_id, order_guid, provider, operation, amount_cents, state FROM payment_attempts WHERE attempt_id = ?",
      [input.attemptId],
    );
    if (!attempt || intentText(attempt.state) !== "Approved") {
      throw new Error("Only an Approved payment attempt can be committed to an order.");
    }
    const attemptOrderGuid = intentText(attempt.order_guid);
    if (attemptOrderGuid !== input.orderGuid) {
      throw new Error("Approved payment attempt belongs to a different order.");
    }
    const method = tenderMethodForProvider(intentText(attempt.provider));
    const amountCents = intentInteger(attempt.amount_cents);
    assertApprovedPaymentAmount(intentText(attempt.operation), amountCents);

    const order = await transaction.getFirst<ApprovedPaymentOrderRow>(
      `SELECT order_guid, state, actual_amount_cents, store_code, device_code
       FROM local_orders
       WHERE order_guid = ?`,
      [input.orderGuid],
    );
    if (!order) throw new Error("Approved payment order no longer exists.");
    const actualAmountCents = intentInteger(order.actual_amount_cents);
    if (actualAmountCents === 0 || Math.sign(actualAmountCents) !== Math.sign(amountCents)) {
      throw new Error("Approved payment amount has an invalid sign for the order.");
    }

    const existing = await transaction.getFirst<ApprovedPaymentTenderRow>(
      "SELECT tender_guid, order_guid, method, amount_cents FROM order_tenders WHERE payment_attempt_id = ?",
      [input.attemptId],
    );
    if (existing) {
      if (intentText(existing.order_guid) !== input.orderGuid || intentText(existing.method) !== method || intentInteger(existing.amount_cents) !== amountCents) {
        throw new Error("Approved payment attempt is already bound to an incompatible tender.");
      }
      return {
        replayed: true,
        orderGuid: input.orderGuid,
        tenderGuid: intentText(existing.tender_guid),
        completed: approvedPaymentReplayIsCompleted(order.state),
        signedTenderAmountCents: amountCents,
      };
    }

    const currentOrderState = intentText(order.state);
    if (currentOrderState !== "Draft" && currentOrderState !== "Completing") {
      throw new Error("Order cannot accept a new Approved tender in its current state.");
    }
    const totalRow = await transaction.getFirst<{ tender_total: unknown }>(
      "SELECT COALESCE(SUM(amount_cents), 0) AS tender_total FROM order_tenders WHERE order_guid = ?",
      [input.orderGuid],
    );
    const completedAmountCents = intentInteger(totalRow?.tender_total ?? 0) + amountCents;
    if (!isTenderTotalWithinOrder(actualAmountCents, completedAmountCents)) {
      throw new Error("Approved payment would overpay or over-refund the order.");
    }

    const completed = completedAmountCents === actualAmountCents;
    let recalledHoldCompletion: RecalledHoldCompletion | null = null;
    if (completed) {
      assertApprovedPaymentFulfilment(input);
      for (const event of input.completionAuditEvents) assertSafeAuditPayload(event.payload);
      recalledHoldCompletion = validateApprovedPaymentRecallCompletion(
        input,
        order,
      );
      const terminalFence = await readTerminalCartFenceForCheckout(
        transaction,
        intentText(order.store_code),
        intentText(order.device_code),
      );
      validateApprovedPaymentRecallFence(
        recalledHoldCompletion,
        terminalFence,
      );
    }

    // 在写 tender 前先以读取到的精确状态做 CAS；任何竞争或终态变化都整事务失败。
    const stateTransition = completed
      ? await transaction.run(
        "UPDATE local_orders SET state = 'PendingSync', updated_at_iso = ? WHERE order_guid = ? AND state = ?",
        [this.nowIso(), input.orderGuid, currentOrderState],
      )
      : await transaction.run(
        "UPDATE local_orders SET state = 'Completing', updated_at_iso = ? WHERE order_guid = ? AND state = ?",
        [this.nowIso(), input.orderGuid, currentOrderState],
      );
    if (stateTransition.changes !== 1) {
      throw new Error("Approved payment order state changed before the tender could be committed.");
    }
    await transaction.run(
      "INSERT INTO order_tenders (tender_guid, order_guid, method, amount_cents, payment_attempt_id, created_at_iso) VALUES (?, ?, ?, ?, ?, ?)",
      [input.tenderGuid, input.orderGuid, method, amountCents, input.attemptId, this.nowIso()],
    );

    if (!completed) {
      return { replayed: false, orderGuid: input.orderGuid, tenderGuid: input.tenderGuid, completed: false, signedTenderAmountCents: amountCents };
    }

    const now = this.nowIso();
    const auditScope = auditScopeFromPersistedOrder(order);
    for (const event of input.completionAuditEvents) {
      await appendScopedAuditEvent(transaction, event, auditScope, now);
    }
    await transaction.run(
      "INSERT INTO outbox_messages (message_id, aggregate_id, kind, payload_json, state, attempt_count, next_attempt_at_iso, lease_id, lease_expires_at_iso, last_error_code, created_at_iso, updated_at_iso) VALUES (?, ?, ?, ?, 'pending', 0, ?, NULL, NULL, NULL, ?, ?)",
      [input.outbox.messageId, input.outbox.aggregateId, input.outbox.kind, input.outbox.payloadJson, input.outbox.nextAttemptAtIso, now, now],
    );
    await persistApprovedPaymentFulfilment(transaction, input.fulfilment, input.orderGuid, this.encryptor, now);
    if (recalledHoldCompletion) {
      await completeRecalledHoldInApprovedPaymentTransaction(
        transaction,
        input.orderGuid,
        recalledHoldCompletion,
      );
    }
    return { replayed: false, orderGuid: input.orderGuid, tenderGuid: input.tenderGuid, completed: true, signedTenderAmountCents: amountCents };
  }
}

/** 订单账本是订单审计唯一可信的门店/终端来源，避免重注册后回读 runtime metadata。 */
function auditScopeFromLocalOrder(order: LocalOrder): AuditScope {
  return freezeAuditScope({
    storeCode: order.storeCode,
    deviceCode: order.deviceCode,
  });
}

function auditScopeFromPersistedOrder(
  order: ApprovedPaymentOrderRow,
): AuditScope {
  return freezeAuditScope({
    storeCode: strictIdentifier(order.store_code, "approved payment order store code"),
    deviceCode: strictIdentifier(order.device_code, "approved payment order device code"),
  });
}

/** 事务写入统一显式固化 scope；M30 trigger 只是旧写入点的兼容防线。 */
async function appendScopedAuditEvent(
  transaction: SqliteConnectionPort,
  event: AuditEventDraft,
  scope: AuditScope,
  nextAttemptAtIso: string,
): Promise<void> {
  await transaction.run(
    `INSERT INTO audit_events (
      event_id, event_type, occurred_at_iso, order_guid, correlation_id,
      payload_json, uploaded_at_iso, delivery_state, attempt_count,
      next_attempt_at_iso, last_error_code, scope_store_code, scope_device_code
    ) VALUES (?, ?, ?, ?, ?, ?, NULL, 'pending', 0, ?, NULL, ?, ?)`,
    [
      event.eventId,
      event.eventType,
      event.occurredAtIso,
      event.orderGuid,
      event.correlationId,
      JSON.stringify(event.payload),
      nextAttemptAtIso,
      scope.storeCode,
      scope.deviceCode,
    ],
  );
}

function tenderMethodForProvider(provider: string): "card" | "voucher" {
  if (provider === "square" || provider === "linkly-cloud" || provider === "manual-card") return "card";
  if (provider === "voucher") return "voucher";
  throw new Error("Approved payment provider is unsupported.");
}

function assertApprovedPaymentAmount(operation: string, amountCents: number): void {
  if ((operation === "purchase" && amountCents <= 0) || (operation === "refund" && amountCents >= 0)) {
    throw new Error("Approved payment operation has an invalid signed amount.");
  }
  if (operation !== "purchase" && operation !== "refund") throw new Error("Approved payment operation is unsupported.");
}

function isTenderTotalWithinOrder(actual: number, total: number): boolean {
  return actual > 0 ? total >= 0 && total <= actual : total <= 0 && total >= actual;
}

function approvedPaymentReplayIsCompleted(value: unknown): boolean {
  const state = intentText(value);
  if (state === "Draft" || state === "Completing") return false;
  if (
    state === "CompletedLocal" ||
    state === "PendingSync" ||
    state === "Syncing" ||
    state === "Synced" ||
    state === "Blocked403" ||
    state === "Rejected"
  ) {
    // Blocked403/Rejected 只描述补传结果，不撤销已落库 tender 和本地完成事实。
    return true;
  }
  throw new Error("Approved payment order has an invalid persisted state.");
}

function assertApprovedPaymentFulfilment(input: ApprovedPaymentOrderCommit): void {
  const { print, drawer } = input.fulfilment;
  if (print && (print.orderGuid !== input.orderGuid || !print.jobId.trim() || !print.printerId.trim() || print.receiptBytes.length === 0 || print.isReprint)) {
    throw new Error("Approved payment print fulfilment is invalid.");
  }
  if (drawer && (
    drawer.orderGuid !== input.orderGuid ||
    !drawer.eventId.trim() ||
    !drawer.printerId.trim() ||
    !drawer.reason.trim() ||
    drawer.printJobId !== (print?.jobId ?? null) ||
    (print !== null && drawer.printerId !== print.printerId)
  )) {
    throw new Error("Approved payment drawer fulfilment is invalid.");
  }
  if (input.outbox.aggregateId !== input.orderGuid || input.outbox.kind !== "order-sync") {
    throw new Error("Approved payment completion must enqueue its own order-sync outbox message.");
  }
}

async function persistApprovedPaymentFulfilment(
  transaction: SqliteConnectionPort,
  fulfilment: CashFulfilmentDraft,
  orderGuid: string,
  encryptor: SensitivePayloadEncryptor,
  now: string,
): Promise<void> {
  if (fulfilment.print) {
    const receiptCiphertext = await encryptor.encrypt(JSON.stringify(Array.from(fulfilment.print.receiptBytes)));
    await transaction.run(
      "INSERT INTO print_jobs (job_id, order_guid, state, printer_id, receipt_ciphertext, is_reprint, retry_count, last_error_code, created_at_iso, updated_at_iso) VALUES (?, ?, 'Queued', ?, ?, 0, 0, NULL, ?, ?)",
      [fulfilment.print.jobId, orderGuid, fulfilment.print.printerId, receiptCiphertext, now, now],
    );
  }
  if (fulfilment.drawer) {
    await transaction.run(
      "INSERT INTO drawer_events (event_id, order_guid, printer_id, print_job_id, state, reason, retry_count, requested_at_iso, completed_at_iso, last_error_code, created_at_iso, updated_at_iso) VALUES (?, ?, ?, ?, 'Required', ?, 0, NULL, NULL, NULL, ?, ?)",
      [fulfilment.drawer.eventId, orderGuid, fulfilment.drawer.printerId, fulfilment.drawer.printJobId, fulfilment.drawer.reason, now, now],
    );
  }
}

function intentText(value: unknown): string {
  if (typeof value !== "string") throw new Error("Invalid persisted cash checkout intent text.");
  return value;
}

function intentInteger(value: unknown): number {
  const integer = Number(value);
  if (!Number.isSafeInteger(integer)) throw new Error("Invalid persisted cash checkout intent amount.");
  return integer;
}

function assertCashCheckoutIntentIdentity(input: DurableCashOrderCommit): void {
  const { intent } = input;
  if (!intent.checkoutIntentId.trim() || !intent.requestSignature.trim()) {
    throw new Error("Cash checkout intent id and request signature are required.");
  }
}

function assertNewCashCheckoutIntent(input: DurableCashOrderCommit): void {
  const { intent, command } = input;
  if (!Number.isSafeInteger(intent.cashDueCents) || !Number.isSafeInteger(intent.changeCents)) {
    throw new Error("Cash checkout intent amounts must be integer cents.");
  }
  if (command.order.orderGuid.trim().length === 0) {
    throw new Error("Cash checkout intent requires an order guid.");
  }
}

type TerminalCartFenceCheckoutRow = Readonly<{
  store_code: unknown;
  device_code: unknown;
  kind: unknown;
  hold_id: unknown;
  recall_attempt_id: unknown;
  bound_order_guid: unknown;
  created_at_iso: unknown;
  held_status: unknown;
  held_store_code: unknown;
  held_device_code: unknown;
  held_recall_attempt_id: unknown;
}>;

type TerminalCartFenceCheckoutState = Readonly<{
  fence: TerminalCartFence;
  heldStatus: string;
  heldStoreCode: string;
  heldDeviceCode: string;
  heldRecallAttemptId: string | null;
}>;

function validateNewDurableCashCheckout(
  input: DurableCashOrderCommit,
): RecalledHoldCompletion | null {
  assertNewCashCheckoutIntent(input);
  const context = input.terminalContext;
  if (!context || (context.kind !== "none" && context.kind !== "recalled")) {
    throw new Error("Cash checkout terminal context is invalid.");
  }
  if (context.kind === "none") {
    assertOrdinaryDurableCashCheckout(input);
    assertCashOrder(input.command);
    if (input.recalledHoldCompletion !== null) {
      throw new Error(
        "Ordinary cash checkout cannot complete a recalled hold.",
      );
    }
    return null;
  }

  // 召回挂单只承接之前冻结的 sale 购物车；退款必须走普通终端上下文与容量账本。
  assertDurableCashSale(input.command);
  assertCashOrder(input.command);
  const completion = input.recalledHoldCompletion;
  if (!completion || completion.binding.kind !== "recalled") {
    throw new Error("Recalled cash checkout requires a matching completion.");
  }
  const contextScope = {
    storeCode: strictIdentifier(
      context.scope.storeCode,
      "terminal context store code",
    ),
    deviceCode: strictIdentifier(
      context.scope.deviceCode,
      "terminal context device code",
    ),
  };
  const completionScope = {
    storeCode: strictIdentifier(
      completion.binding.scope.storeCode,
      "recall completion store code",
    ),
    deviceCode: strictIdentifier(
      completion.binding.scope.deviceCode,
      "recall completion device code",
    ),
  };
  const holdId = strictIdentifier(context.holdId, "terminal context hold id");
  const recallAttemptId = strictIdentifier(
    context.recallAttemptId,
    "terminal context recall attempt id",
  );
  if (
    completionScope.storeCode !== contextScope.storeCode ||
    completionScope.deviceCode !== contextScope.deviceCode ||
    strictIdentifier(completion.binding.holdId, "recall completion hold id") !==
      holdId ||
    strictIdentifier(
      completion.binding.recallAttemptId,
      "recall completion attempt id",
    ) !== recallAttemptId
  ) {
    throw new Error(
      "Cash checkout terminal context and recall completion do not match.",
    );
  }

  const orderStoreCode = strictIdentifier(
    input.command.order.storeCode,
    "cash order store code",
  );
  const orderDeviceCode = strictIdentifier(
    input.command.order.deviceCode,
    "cash order device code",
  );
  if (
    orderStoreCode !== contextScope.storeCode ||
    orderDeviceCode !== contextScope.deviceCode
  ) {
    throw new Error("Recalled cash checkout belongs to a different terminal.");
  }

  const recalledAtIso = canonicalIso(
    completion.recalledAtIso,
    "recall completion time",
  );
  if (
    recalledAtIso !==
    canonicalIso(input.command.order.soldAtIso, "cash order sold time")
  ) {
    throw new Error("Recall completion time must match the cash sale time.");
  }
  const recallAudit = validateRecallCompletionAudit(
    completion.recallAudit,
    input.command.order.orderGuid,
    holdId,
    recalledAtIso,
  );
  return {
    binding: {
      kind: "recalled",
      scope: contextScope,
      holdId,
      recallAttemptId,
    },
    recalledAtIso,
    recallAudit,
  };
}

function assertDurableCashSale(command: CompleteCashOrderCommand): void {
  if (!command.order.lines.length) {
    throw new Error("Durable cash sale must contain at least one sale line.");
  }
  if (
    command.order.originalOrderGuid !== null ||
    command.order.lines.some(
      (line) =>
        line.kind !== "sale" ||
        line.returnSourceKey !== null ||
        line.originalOrderGuid !== null ||
        line.originalOrderDetailGuid !== null,
    )
  ) {
    throw new Error("Durable cash checkout cannot contain return lines.");
  }
}

function assertOrdinaryDurableCashCheckout(
  input: DurableCashOrderCommit,
): void {
  const { command } = input;
  const hasSale = command.order.lines.some((line) => line.kind === "sale");
  const hasReturn = command.order.lines.some((line) => line.kind === "return");
  if (hasSale && hasReturn) {
    throw new Error(
      "Ordinary durable cash checkout cannot mix sale and return lines.",
    );
  }
  if (hasReturn) {
    assertDurableCashReturn(input);
    return;
  }
  assertDurableCashSale(command);
}

function assertDurableCashReturn(input: DurableCashOrderCommit): void {
  const { command, intent } = input;
  const { order } = command;
  if (
    !order.lines.length ||
    order.lines.some((line) => line.kind !== "return")
  ) {
    throw new Error(
      "Durable cash return must contain only return lines.",
    );
  }

  const orderOriginalGuid = order.originalOrderGuid;
  if (
    orderOriginalGuid === null ||
    !orderOriginalGuid.trim() ||
    orderOriginalGuid !== orderOriginalGuid.trim()
  ) {
    throw new Error("Durable cash return metadata is invalid.");
  }

  let referencesOrderOriginal = false;
  for (const line of order.lines) {
    const quantity = Number(line.quantity);
    if (
      !line.returnSourceKey ||
      line.returnSourceKey !== line.returnSourceKey.trim() ||
      !line.originalOrderGuid ||
      line.originalOrderGuid !== line.originalOrderGuid.trim() ||
      (line.originalOrderDetailGuid !== null &&
        (!line.originalOrderDetailGuid.trim() ||
          line.originalOrderDetailGuid !==
            line.originalOrderDetailGuid.trim())) ||
      !Number.isSafeInteger(quantity) ||
      quantity <= 0
    ) {
      throw new Error("Durable cash return metadata is invalid.");
    }
    if (line.originalOrderGuid === orderOriginalGuid) {
      referencesOrderOriginal = true;
    }
    if (
      line.unitPrice.currency !== "AUD" ||
      line.discount.currency !== "AUD" ||
      line.actualAmount.currency !== "AUD" ||
      line.unitPrice.cents <= 0 ||
      line.actualAmount.cents >= 0
    ) {
      throw new Error("Durable cash return has invalid signed amounts.");
    }
  }
  if (!referencesOrderOriginal) {
    throw new Error("Durable cash return metadata is invalid.");
  }

  if (
    order.total.cents >= 0 ||
    order.actualAmount.cents >= 0 ||
    order.tenders.length !== 1 ||
    order.tenders.some((tender) => tender.amount.cents >= 0)
  ) {
    throw new Error("Durable cash return has invalid signed amounts.");
  }

  if (command.auditEvents.length !== 1) {
    throw new Error(
      "Durable cash return requires exactly one completion audit.",
    );
  }
  const completionAudit = command.auditEvents[0];
  if (
    !completionAudit ||
    completionAudit.eventType !== "RETURN_REFUND_COMPLETE" ||
    completionAudit.orderGuid !== order.orderGuid ||
    completionAudit.correlationId !== order.orderGuid
  ) {
    throw new Error("Durable cash return completion audit is invalid.");
  }
  const auditCashDueCents = integerCents(
    completionAudit.payload.cashDueCents,
    "return audit cash due",
  );
  const auditChangeCents = integerCents(
    completionAudit.payload.changeCents,
    "return audit change",
  );
  if (
    completionAudit.payload.checkoutIntentId !== intent.checkoutIntentId ||
    completionAudit.payload.localSequence !== order.localSequence ||
    auditCashDueCents !== intent.cashDueCents ||
    auditChangeCents !== intent.changeCents
  ) {
    throw new Error("Durable cash return audit cash settlement mismatch.");
  }
  if (
    intent.cashDueCents !== roundCashCents(order.actualAmount.cents) ||
    intent.changeCents !== 0
  ) {
    throw new Error("Durable cash return refund cash settlement mismatch.");
  }
  if (
    command.outbox.kind !== "order-sync" ||
    command.outbox.aggregateId !== order.orderGuid
  ) {
    throw new Error(
      "Durable cash return must enqueue its own order-sync outbox message.",
    );
  }
}

function integerCents(value: unknown, label: string): number {
  if (typeof value !== "number" || !Number.isSafeInteger(value)) {
    throw new Error(`${label} must use integer cents.`);
  }
  return value;
}

function checkedCentsAdd(left: number, right: number, label: string): number {
  return integerCents(left + right, label);
}

function roundCashCents(value: number): number {
  const cents = integerCents(value, "cash amount");
  const sign = Math.sign(cents);
  const absolute = Math.abs(cents);
  return integerCents(
    sign *
      (Math.floor(absolute / 5) + (absolute % 5 >= 3 ? 1 : 0)) *
      5,
    "rounded cash amount",
  );
}

function validateRecallCompletionAudit(
  event: RecalledHoldCompletion["recallAudit"],
  orderGuidInput: string,
  holdId: string,
  recalledAtIso: string,
): RecalledHoldCompletion["recallAudit"] {
  const orderGuid = strictIdentifier(orderGuidInput, "cash order guid");
  if (
    event.eventType !== "ORDER_RECALL" ||
    event.orderGuid !== orderGuid ||
    strictIdentifier(event.correlationId, "recall audit correlation") !== holdId
  ) {
    throw new Error(
      "Recall audit type, order guid, or correlation id is invalid.",
    );
  }
  const occurredAtIso = canonicalIso(
    event.occurredAtIso,
    "recall audit time",
  );
  if (occurredAtIso !== recalledAtIso) {
    throw new Error("Recall audit time must match recall completion time.");
  }
  assertSafeAuditPayload(event.payload);
  return {
    eventId: strictIdentifier(event.eventId, "recall audit event id"),
    eventType: "ORDER_RECALL",
    occurredAtIso,
    orderGuid,
    correlationId: holdId,
    payload: event.payload,
  };
}

async function readTerminalCartFenceForCheckout(
  transaction: SqliteConnectionPort,
  storeCodeInput: string,
  deviceCodeInput: string,
): Promise<TerminalCartFenceCheckoutState | null> {
  const storeCode = strictIdentifier(storeCodeInput, "cash order store code");
  const deviceCode = strictIdentifier(
    deviceCodeInput,
    "cash order device code",
  );
  const row = await transaction.getFirst<TerminalCartFenceCheckoutRow>(
    `SELECT fence.store_code, fence.device_code, fence.kind, fence.hold_id,
      fence.recall_attempt_id, fence.bound_order_guid, fence.created_at_iso,
      held.status AS held_status, held.store_code AS held_store_code,
      held.device_code AS held_device_code,
      held.recall_attempt_id AS held_recall_attempt_id
     FROM terminal_cart_fences fence
     INNER JOIN held_order_records held ON held.hold_id = fence.hold_id
     WHERE fence.store_code = ? AND fence.device_code = ?`,
    [storeCode, deviceCode],
  );
  if (!row) return null;

  const kind = intentText(row.kind);
  const recallAttemptId = nullableIntentText(row.recall_attempt_id);
  const boundOrderGuid = nullableIntentText(row.bound_order_guid);
  if (
    (kind === "HoldClear" &&
      (recallAttemptId !== null || boundOrderGuid !== null)) ||
    (kind === "RecallActive" && recallAttemptId === null)
  ) {
    throw new Error("Terminal cart fence has an invalid persisted state.");
  }
  if (kind !== "HoldClear" && kind !== "RecallActive") {
    throw new Error("Terminal cart fence has an invalid kind.");
  }
  return {
    fence: {
      scope: {
        storeCode: strictIdentifier(
          row.store_code,
          "terminal fence store code",
        ),
        deviceCode: strictIdentifier(
          row.device_code,
          "terminal fence device code",
        ),
      },
      kind,
      holdId: strictIdentifier(row.hold_id, "terminal fence hold id"),
      recallAttemptId,
      boundOrderGuid,
      createdAtIso: canonicalIso(
        row.created_at_iso,
        "terminal fence creation time",
      ),
    },
    heldStatus: intentText(row.held_status),
    heldStoreCode: strictIdentifier(
      row.held_store_code,
      "held order store code",
    ),
    heldDeviceCode: strictIdentifier(
      row.held_device_code,
      "held order device code",
    ),
    heldRecallAttemptId: nullableIntentText(row.held_recall_attempt_id),
  };
}

function validateTerminalFenceForCheckout(
  input: DurableCashOrderCommit,
  completion: RecalledHoldCompletion | null,
  state: TerminalCartFenceCheckoutState | null,
): void {
  if (completion === null) {
    if (state !== null) {
      throw new Error(
        "Ordinary cash checkout is blocked by an active terminal cart fence.",
      );
    }
    return;
  }
  if (!state) {
    throw new Error("Recalled cash checkout has no active terminal cart fence.");
  }
  const { binding } = completion;
  const { fence } = state;
  if (
    fence.kind !== "RecallActive" ||
    fence.scope.storeCode !== binding.scope.storeCode ||
    fence.scope.deviceCode !== binding.scope.deviceCode ||
    fence.holdId !== binding.holdId ||
    fence.recallAttemptId !== binding.recallAttemptId ||
    fence.boundOrderGuid !== null ||
    state.heldStatus !== "Recalling" ||
    state.heldStoreCode !== binding.scope.storeCode ||
    state.heldDeviceCode !== binding.scope.deviceCode ||
    state.heldRecallAttemptId !== binding.recallAttemptId
  ) {
    throw new Error(
      "Recalled cash checkout does not match the active recall fence.",
    );
  }
  if (
    input.terminalContext.kind !== "recalled" ||
    input.terminalContext.holdId !== binding.holdId ||
    input.terminalContext.recallAttemptId !== binding.recallAttemptId
  ) {
    throw new Error(
      "Recalled cash checkout terminal binding changed during validation.",
    );
  }
}

function validateApprovedPaymentRecallCompletion(
  input: ApprovedPaymentOrderCommit,
  order: ApprovedPaymentOrderRow,
): RecalledHoldCompletion | null {
  const completion = input.recalledHoldCompletion;
  if (completion === null) return null;
  const binding = completion.binding;
  if (binding.kind !== "recalled") {
    throw new Error("Approved payment recall binding is invalid.");
  }
  const scope = {
    storeCode: strictIdentifier(
      binding.scope.storeCode,
      "approved recall store code",
    ),
    deviceCode: strictIdentifier(
      binding.scope.deviceCode,
      "approved recall device code",
    ),
  };
  if (
    scope.storeCode !==
      strictIdentifier(order.store_code, "approved payment order store code") ||
    scope.deviceCode !==
      strictIdentifier(order.device_code, "approved payment order device code")
  ) {
    throw new Error("Approved recalled payment belongs to a different terminal.");
  }
  const holdId = strictIdentifier(binding.holdId, "approved recall hold id");
  const recallAttemptId = strictIdentifier(
    binding.recallAttemptId,
    "approved recall attempt id",
  );
  const recalledAtIso = canonicalIso(
    completion.recalledAtIso,
    "approved recall completion time",
  );
  return {
    binding: {
      kind: "recalled",
      scope,
      holdId,
      recallAttemptId,
    },
    recalledAtIso,
    recallAudit: validateRecallCompletionAudit(
      completion.recallAudit,
      input.orderGuid,
      holdId,
      recalledAtIso,
    ),
  };
}

function validateApprovedPaymentRecallFence(
  completion: RecalledHoldCompletion | null,
  state: TerminalCartFenceCheckoutState | null,
): void {
  if (completion === null) {
    if (state !== null) {
      throw new Error(
        "Ordinary approved payment is blocked by an active terminal cart fence.",
      );
    }
    return;
  }
  if (!state) {
    throw new Error(
      "Recalled approved payment has no active terminal cart fence.",
    );
  }
  const { binding } = completion;
  const { fence } = state;
  if (
    fence.kind !== "RecallActive" ||
    fence.scope.storeCode !== binding.scope.storeCode ||
    fence.scope.deviceCode !== binding.scope.deviceCode ||
    fence.holdId !== binding.holdId ||
    fence.recallAttemptId !== binding.recallAttemptId ||
    fence.boundOrderGuid !== null ||
    state.heldStatus !== "Recalling" ||
    state.heldStoreCode !== binding.scope.storeCode ||
    state.heldDeviceCode !== binding.scope.deviceCode ||
    state.heldRecallAttemptId !== binding.recallAttemptId
  ) {
    throw new Error(
      "Recalled approved payment does not match the active recall fence.",
    );
  }
}

async function completeRecalledHoldInApprovedPaymentTransaction(
  transaction: SqliteConnectionPort,
  orderGuidInput: string,
  completion: RecalledHoldCompletion,
): Promise<void> {
  const orderGuid = strictIdentifier(
    orderGuidInput,
    "approved recalled payment order guid",
  );
  const { binding } = completion;
  // 在线支付先把 fence 精确绑定到当前订单，再在同一事务内完成并删除。
  const bound = await transaction.run(
    `UPDATE terminal_cart_fences
     SET bound_order_guid = ?
     WHERE store_code = ? AND device_code = ?
       AND kind = 'RecallActive' AND hold_id = ?
       AND recall_attempt_id = ? AND bound_order_guid IS NULL`,
    [
      orderGuid,
      binding.scope.storeCode,
      binding.scope.deviceCode,
      binding.holdId,
      binding.recallAttemptId,
    ],
  );
  if (bound.changes !== 1) {
    throw new Error("Recall fence changed before approved payment completion.");
  }
  // 绑定后同一事务写不可变订单来源；RemoteClaim 且已 Active 的本地 claim 绑定并 Completed。
  await persistRecalledHoldOrderSourceAndClaim(transaction, {
    orderGuid,
    holdId: binding.holdId,
    recallAttemptId: binding.recallAttemptId,
    recalledAtIso: completion.recalledAtIso,
  });
  const changed = await transaction.run(
    `UPDATE held_order_records
     SET status = 'Recalled', recalled_at_iso = ?, updated_at_iso = ?
     WHERE hold_id = ? AND store_code = ? AND device_code = ?
       AND recall_attempt_id = ? AND status = 'Recalling'`,
    [
      completion.recalledAtIso,
      completion.recalledAtIso,
      binding.holdId,
      binding.scope.storeCode,
      binding.scope.deviceCode,
      binding.recallAttemptId,
    ],
  );
  if (changed.changes !== 1) {
    throw new Error("Recalled hold changed before approved payment completion.");
  }
  await appendScopedAuditEvent(
    transaction,
    completion.recallAudit,
    freezeAuditScope(binding.scope),
    completion.recalledAtIso,
  );
  const deleted = await transaction.run(
    `DELETE FROM terminal_cart_fences
     WHERE store_code = ? AND device_code = ?
       AND kind = 'RecallActive' AND hold_id = ?
       AND recall_attempt_id = ? AND bound_order_guid = ?`,
    [
      binding.scope.storeCode,
      binding.scope.deviceCode,
      binding.holdId,
      binding.recallAttemptId,
      orderGuid,
    ],
  );
  if (deleted.changes !== 1) {
    throw new Error("Recall fence changed before approved payment completion.");
  }
  if (completion.recallAudit.orderGuid !== orderGuid) {
    throw new Error("Recall audit order changed during approved payment completion.");
  }
}

async function completeRecalledHoldInCashTransaction(
  transaction: SqliteConnectionPort,
  input: DurableCashOrderCommit,
  completion: RecalledHoldCompletion,
): Promise<void> {
  const { binding } = completion;
  const changed = await transaction.run(
    `UPDATE held_order_records
     SET status = 'Recalled', recalled_at_iso = ?, updated_at_iso = ?
     WHERE hold_id = ? AND store_code = ? AND device_code = ?
       AND recall_attempt_id = ? AND status = 'Recalling'`,
    [
      completion.recalledAtIso,
      completion.recalledAtIso,
      binding.holdId,
      binding.scope.storeCode,
      binding.scope.deviceCode,
      binding.recallAttemptId,
    ],
  );
  if (changed.changes !== 1) {
    throw new Error("Recalled hold changed before cash completion.");
  }
  // 现金路径以 held CAS 精确绑定；随后同事务写不可变来源并完成 RemoteClaim。
  await persistRecalledHoldOrderSourceAndClaim(transaction, {
    orderGuid: input.command.order.orderGuid,
    holdId: binding.holdId,
    recallAttemptId: binding.recallAttemptId,
    recalledAtIso: completion.recalledAtIso,
  });
  const audit = completion.recallAudit;
  await appendScopedAuditEvent(
    transaction,
    audit,
    freezeAuditScope(binding.scope),
    completion.recalledAtIso,
  );
  const deleted = await transaction.run(
    `DELETE FROM terminal_cart_fences
     WHERE store_code = ? AND device_code = ?
       AND kind = 'RecallActive' AND hold_id = ?
       AND recall_attempt_id = ? AND bound_order_guid IS NULL`,
    [
      binding.scope.storeCode,
      binding.scope.deviceCode,
      binding.holdId,
      binding.recallAttemptId,
    ],
  );
  if (deleted.changes !== 1) {
    throw new Error("Recall fence changed before cash completion.");
  }
  if (audit.orderGuid !== input.command.order.orderGuid) {
    // 前置校验与数据库写之间不应出现可变输入；若调用方破坏只读约束则回滚。
    throw new Error("Recall audit order changed during cash completion.");
  }
}

function strictIdentifier(value: unknown, label: string): string {
  if (typeof value !== "string") throw new Error(`Invalid ${label}.`);
  const normalized = value.trim();
  if (!normalized || normalized !== value) {
    throw new Error(`Invalid ${label}.`);
  }
  return normalized;
}

function canonicalIso(value: unknown, label: string): string {
  const raw = strictIdentifier(value, label);
  const milliseconds = Date.parse(raw);
  if (!Number.isFinite(milliseconds)) throw new Error(`Invalid ${label}.`);
  return new Date(milliseconds).toISOString();
}

function nullableIntentText(value: unknown): string | null {
  if (value === null || value === undefined) return null;
  return strictIdentifier(value, "persisted terminal cart fence value");
}

class PosDatabaseTransaction implements DatabaseTransactionPort {
  public constructor(
    private readonly transaction: SqliteConnectionPort,
    private readonly nowIso: () => string,
  ) {}

  public async completeCashOrder(command: CompleteCashOrderCommand): Promise<void> {
    assertCashOrder(command);
    for (const event of command.auditEvents) {
      assertSafeAuditPayload(event.payload);
    }
    const { order } = command;
    const auditScope = auditScopeFromLocalOrder(order);
    const createdAtIso = this.nowIso();

    // 退款容量与订单写入共用同一事务；未知或已耗尽容量时不能离线生成退款单。
    await reserveReturnCapacity(this.transaction, order.lines, createdAtIso);

    await this.transaction.run(
      `INSERT INTO local_orders (
        order_guid, local_sequence, store_code, device_code, cashier_id, cashier_name,
        sold_at_iso, state, total_cents, discount_cents, actual_amount_cents,
        original_order_guid, created_at_iso, updated_at_iso
      ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      [
        order.orderGuid,
        order.localSequence,
        order.storeCode,
        order.deviceCode,
        order.cashierId,
        order.cashierName,
        order.soldAtIso,
        order.state,
        order.total.cents,
        order.discount.cents,
        order.actualAmount.cents,
        order.originalOrderGuid,
        createdAtIso,
        createdAtIso,
      ],
    );

    for (const [index, line] of order.lines.entries()) {
      const syncProvenance = normalizeLineSyncProvenance(
        line.syncProvenance,
      );
      await this.transaction.run(
        `INSERT INTO local_order_lines (
          line_id, order_guid, line_sequence, product_code, item_number, lookup_code,
          display_name, quantity, unit_price_cents, discount_cents, actual_amount_cents,
          price_source, line_kind, return_source_key, original_order_guid,
          original_order_detail_guid, reference_code, sync_price_source
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
        [
          line.lineId,
          order.orderGuid,
          index + 1,
          line.productCode,
          line.itemNumber,
          line.lookupCode,
          line.displayName,
          line.quantity,
          line.unitPrice.cents,
          line.discount.cents,
          line.actualAmount.cents,
          line.priceSource,
          line.kind,
          line.returnSourceKey,
          line.originalOrderGuid,
          line.originalOrderDetailGuid,
          syncProvenance.referenceCode,
          syncProvenance.priceSource,
        ],
      );
    }

    for (const tender of order.tenders) {
      await this.transaction.run(
        `INSERT INTO order_tenders (
          tender_guid, order_guid, method, amount_cents, payment_attempt_id, created_at_iso
        ) VALUES (?, ?, ?, ?, NULL, ?)`,
        [tender.tenderGuid, order.orderGuid, tender.method, tender.amount.cents, createdAtIso],
      );
    }

    for (const event of command.auditEvents) {
      await appendScopedAuditEvent(
        this.transaction,
        event,
        auditScope,
        createdAtIso,
      );
    }

    await this.transaction.run(
      `INSERT INTO outbox_messages (
        message_id, aggregate_id, kind, payload_json, state, attempt_count, next_attempt_at_iso,
        lease_id, lease_expires_at_iso, last_error_code, created_at_iso, updated_at_iso
      ) VALUES (?, ?, ?, ?, 'pending', 0, ?, NULL, NULL, NULL, ?, ?)`,
      [
        command.outbox.messageId,
        command.outbox.aggregateId,
        command.outbox.kind,
        command.outbox.payloadJson,
        command.outbox.nextAttemptAtIso,
        createdAtIso,
        createdAtIso,
      ],
    );
  }
}

function assertAtomicFulfilment(
  command: CompleteCashOrderCommand,
  fulfilment: AtomicCashFulfilmentDraft,
): void {
  const orderGuid = command.order.orderGuid;
  if (command.printPolicy === "automatic" && fulfilment.print === null) {
    throw new Error("Automatic receipt policy requires a persisted print job.");
  }
  if (command.printPolicy === "never" && fulfilment.print !== null) {
    throw new Error("Never-print policy cannot persist a print job.");
  }
  if (command.requiresDrawer !== (fulfilment.drawer !== null)) {
    throw new Error("Cash drawer requirement does not match the fulfilment draft.");
  }
  if (fulfilment.print) {
    if (
      fulfilment.print.orderGuid !== orderGuid ||
      !fulfilment.print.jobId.trim() ||
      !fulfilment.print.printerId.trim() ||
      fulfilment.print.receiptBytes.length === 0 ||
      fulfilment.print.isReprint
    ) {
      throw new Error("Cash receipt print job is invalid.");
    }
  }
  if (fulfilment.drawer) {
    if (
      fulfilment.drawer.orderGuid !== orderGuid ||
      !fulfilment.drawer.eventId.trim() ||
      !fulfilment.drawer.printerId.trim() ||
      !fulfilment.drawer.reason.trim()
    ) {
      throw new Error("Cash drawer event is invalid.");
    }
    const expectedPrintJobId = fulfilment.print?.jobId ?? null;
    if (fulfilment.drawer.printJobId !== expectedPrintJobId) {
      throw new Error("Cash drawer event references an unexpected print job.");
    }
    if (fulfilment.print && fulfilment.drawer.printerId !== fulfilment.print.printerId) {
      throw new Error("Cash drawer printer does not match its linked print job.");
    }
  }
}

function assertCashOrder(command: CompleteCashOrderCommand): void {
  const { order } = command;
  if (!order.lines.length || (order.actualAmount.cents !== 0 && !order.tenders.length)) {
    throw new Error("Cash order must contain lines and a tender unless it is a zero order.");
  }
  if (order.actualAmount.cents === 0 && order.tenders.length !== 0) {
    throw new Error("Zero cash order cannot contain tenders.");
  }
  if (order.total.currency !== "AUD" || order.discount.currency !== "AUD" || order.actualAmount.currency !== "AUD") {
    throw new Error("Cash order must use AUD integer cents.");
  }
  integerCents(order.total.cents, "cash order total");
  integerCents(order.discount.cents, "cash order discount");
  integerCents(order.actualAmount.cents, "cash order actual amount");
  let lineActualTotal = 0;
  for (const line of order.lines) {
    normalizeLineSyncProvenance(line.syncProvenance);
    if (
      line.unitPrice.currency !== "AUD" ||
      line.discount.currency !== "AUD" ||
      line.actualAmount.currency !== "AUD"
    ) {
      throw new Error("Cash order lines must use AUD integer cents.");
    }
    integerCents(line.unitPrice.cents, "cash line unit price");
    integerCents(line.discount.cents, "cash line discount");
    lineActualTotal = checkedCentsAdd(
      lineActualTotal,
      integerCents(line.actualAmount.cents, "cash line actual amount"),
      "cash line actual amount total",
    );
  }
  if (lineActualTotal !== order.actualAmount.cents) {
    throw new Error("Cash order and line actual amounts mismatch.");
  }
  let tenderTotal = 0;
  for (const tender of order.tenders) {
    tenderTotal = checkedCentsAdd(
      tenderTotal,
      integerCents(tender.amount.cents, "cash tender amount"),
      "cash tender total",
    );
  }
  if (tenderTotal !== order.actualAmount.cents) {
    throw new Error("Cash tender total must equal the actual order amount.");
  }
  for (const tender of order.tenders) {
    if (
      tender.method !== "cash" ||
      tender.amount.currency !== "AUD" ||
      tender.reference !== null ||
      tender.reservationToken !== null
    ) {
      // 离线现金账本禁止把卡号、券码或授权资料写进普通列。
      throw new Error("Offline cash orders may only persist cash tenders without payment references.");
    }
  }
}

async function reserveReturnCapacity(transaction: SqliteConnectionPort, lines: LocalOrder["lines"], nowIso: string): Promise<void> {
  for (const line of lines) {
    if (line.kind !== "return") continue;
    if (!line.returnSourceKey || !line.originalOrderGuid) throw new Error("Offline return requires an original local return capacity.");
    const quantity = Number(line.quantity);
    if (!Number.isSafeInteger(quantity) || quantity <= 0) throw new Error("Offline return quantity must be a positive integer.");
    const changed = await transaction.run(
      `UPDATE return_capacity
       SET remaining_quantity = CAST(remaining_quantity AS INTEGER) - ?, updated_at_iso = ?
       WHERE return_source_key = ? AND original_order_guid = ?
         AND original_order_detail_guid IS ?
         AND CAST(remaining_quantity AS INTEGER) >= ?`,
      [
        quantity,
        nowIso,
        line.returnSourceKey,
        line.originalOrderGuid,
        line.originalOrderDetailGuid,
        quantity,
      ],
    );
    if (changed.changes !== 1) throw new Error("Offline return capacity is unknown or exhausted.");
  }
}

const sensitiveAuditKey = /^(?:authorization(?:code|token)?|auth(?:code|token)|token|card.*|pan|cvv|voucher.*|secret.*|checkoutid|paymentid|paymentreference|sessionid|txnref|transaction(?:id|ref)|rfn|reservationtoken|reference)$/;

function assertSafeAuditPayload(value: unknown, path = "$", visited = new Set<object>()): void {
  if (value === null || typeof value === "boolean" || typeof value === "number" || typeof value === "string") {
    return;
  }
  if (Array.isArray(value)) {
    value.forEach((entry, index) => assertSafeAuditPayload(entry, `${path}[${index}]`, visited));
    return;
  }
  if (typeof value !== "object") {
    throw new Error(`Audit payload at ${path} is not JSON serializable.`);
  }
  if (visited.has(value)) {
    throw new Error(`Audit payload at ${path} must not contain circular references.`);
  }
  visited.add(value);
  for (const [key, entry] of Object.entries(value)) {
    const normalizedKey = key.replaceAll(/[^a-z0-9]/gi, "").toLowerCase();
    if (sensitiveAuditKey.test(normalizedKey)) {
      throw new Error(`Sensitive audit payload key is not allowed: ${path}.${key}`);
    }
    assertSafeAuditPayload(entry, `${path}.${key}`, visited);
  }
  visited.delete(value);
}
