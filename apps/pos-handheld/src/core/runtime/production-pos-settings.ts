import { auditActorPayload } from "@hb/pos-domain/core/contracts/audit-actor";
import type { AuditActorInput } from "@hb/pos-domain/core/contracts/audit-actor";
import type { AuditEventDraft } from "@hb/pos-domain/core/contracts/order";
import {
  POS_SETTING_KEYS,
  POS_SETTING_OFFLINE_OVERRIDE_AUDIT,
  type PosSettingBinding,
  type PosSettingsAuditPort,
  type PosSettingsService,
} from "@hb/pos-sync/features/pos-settings/pos-settings-service";

import type { PaymentMethodSettings } from "../../features/settings/payment-method-settings";
import { POS_SETTING_MANAGED_ERROR_CODE } from "../../features/settings/settings-presenter";
import type { PosSettingsSyncRepository } from "../db/pos-settings-sync-repository";

// 设置页据此错误码显示「该项由总部管理，无法修改」并把草稿恢复为生效值；常量定义在 presenter 侧。
export { POS_SETTING_MANAGED_ERROR_CODE };

type CashMovementStore = Pick<
  PosSettingsSyncRepository,
  "readCashMovementEnabled" | "writeCashMovementEnabled"
>;

/** 原始（未加守卫）的付款方式存取：save 必须同时刷新运行时内存里的付款方式快照。 */
export type RawPaymentMethodsPort = Readonly<{
  load(): Promise<PaymentMethodSettings>;
  save(settings: PaymentMethodSettings): Promise<void>;
}>;

/**
 * 手持适用的两个键：
 * - cashMovement.enabled：本期只同步 / 存 / 回执，没有功能与界面；
 * - payment.manualCard：写穿到原有付款方式设置（useManualCard）。手持没有礼券 / 代金券开关。
 * 没有付款方式存取（旧组合 / 测试）时只放 cashMovement 一个 binding。
 */
export function createPosSettingBindings(input: Readonly<{
  store: CashMovementStore;
  paymentMethods?: RawPaymentMethodsPort | undefined;
}>): readonly PosSettingBinding[] {
  const bindings: PosSettingBinding[] = [
    {
      key: POS_SETTING_KEYS.cashMovementEnabled,
      read: () => input.store.readCashMovementEnabled(),
      write: (value) => input.store.writeCashMovementEnabled(value),
    },
  ];
  const paymentMethods = input.paymentMethods;
  if (paymentMethods) {
    bindings.push({
      key: POS_SETTING_KEYS.manualCard,
      read: async () => (await paymentMethods.load()).useManualCard === true,
      // 必须走原始 save：它在落盘后同步刷新支付 bootstrap 读取的内存快照，直接写仓库会漏掉这一步。
      write: (value) => paymentMethods.save({ useManualCard: value }),
    });
  }
  return Object.freeze(bindings);
}

/**
 * 离线临时修改审计：键名放在 action 的「值」里（审计 payload 的键不允许含 payment 等敏感字样），
 * 并按现有惯例带上当前收银员 actor 三字段。取不到收银员时省略 actor，审计照记。
 */
export function createPosSettingsAudit(input: Readonly<{
  append(events: readonly AuditEventDraft[]): Promise<void>;
  nowIso(): string;
  createId(): string;
  readActor(): AuditActorInput | null;
}>): PosSettingsAuditPort {
  return {
    recordOfflineOverride: async (entry) => {
      let actor: AuditActorInput | null = null;
      try {
        actor = input.readActor();
      } catch {
        actor = null;
      }
      await input.append([
        {
          eventId: input.createId(),
          eventType: POS_SETTING_OFFLINE_OVERRIDE_AUDIT,
          occurredAtIso: input.nowIso(),
          orderGuid: null,
          correlationId: input.createId(),
          payload: {
            source: "pos-settings",
            action: entry.key,
            status: entry.current ? "on" : "off",
            reason: "OFFLINE",
            ...(actor ? auditActorPayload(actor) : {}),
          },
        },
      ]);
    },
  };
}

/**
 * 付款方式保存的「守卫包装」：所有用户写入都先经过总部策略，界面灰显只是体验层。
 * - 无变更：直接放行到原始 save；
 * - 有变更：交给 service.trySet（内部走 bindings 的原始写，不会递归回守卫）；
 *   locked → 抛 POS_SETTING_MANAGED 且不写任何内容；failed → 抛通用错误；applied → 已由 binding 写穿，不再 save。
 */
export function guardPaymentMethodsSave(input: Readonly<{
  service: Pick<PosSettingsService, "trySet">;
  load(): Promise<PaymentMethodSettings>;
  save(settings: PaymentMethodSettings): Promise<void>;
}>): (settings: PaymentMethodSettings) => Promise<void> {
  return async (settings) => {
    const current = await input.load();
    const changes: { key: string; value: boolean }[] = [];
    if (settings.useManualCard !== current.useManualCard) {
      changes.push({
        key: POS_SETTING_KEYS.manualCard,
        value: settings.useManualCard,
      });
    }
    if (changes.length === 0) {
      await input.save(settings);
      return;
    }
    const result = await input.service.trySet(changes);
    switch (result.outcome) {
      case "applied":
        return;
      case "locked":
        throw Object.assign(
          new Error("This setting is managed by headquarters."),
          { code: POS_SETTING_MANAGED_ERROR_CODE },
        );
      default:
        throw new Error("POS_SETTING_WRITE_FAILED");
    }
  };
}
