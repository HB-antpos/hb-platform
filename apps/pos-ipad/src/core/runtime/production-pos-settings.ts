import {
  POS_SETTING_OFFLINE_OVERRIDE_AUDIT,
  POS_SETTING_KEYS,
  type PosSettingBinding,
  type PosSettingChange,
  type PosSettingsAuditPort,
  type PosSettingsTrySetResult,
} from "@hb/pos-sync/features/pos-settings/pos-settings-service";

import { auditActorPayload, type AuditRepositoryPort } from "@/core/contracts";
import { POS_SETTING_MANAGED_ERROR_CODE } from "@/features/settings/settings-presenter";
import type { PaymentMethodSettings } from "@/features/settings/payment-method-settings";

/**
 * 总部「收银端设置下发」在 iPad 运行时的适配代码：把共享同步服务接到本机原有设置存储、审计与设置页保存路径。
 * 纯函数工厂，不持有状态，便于单测；组合根只负责把真实依赖传进来。
 */

type PaymentMethodsStorePort = Readonly<{
  load(): Promise<PaymentMethodSettings>;
  save(input: PaymentMethodSettings): Promise<unknown>;
}>;

export type PosSettingsBindingsInput = Readonly<{
  /** 现金进出本机开关（本期只存不用）。 */
  cashMovement: Readonly<{
    load(): Promise<boolean>;
    save(enabled: boolean): Promise<void>;
  }>;
  /**
   * 付款方式存储。save 必须是会刷新支付页内存快照的原始回调（expo-pos-runtime 里那个），
   * 不能是守卫包装，也不能绕过它直接写 repository，否则支付页读到的仍是旧快照。
   * 缺省（本构建没有付款方式设置）时只暴露 cashMovement。
   */
  paymentMethods?: PaymentMethodsStorePort | undefined;
}>;

/** 绑定顺序即设置页与回执里的键顺序。 */
export function createPosSettingsBindings(
  input: PosSettingsBindingsInput,
): readonly PosSettingBinding[] {
  const bindings: PosSettingBinding[] = [
    {
      key: POS_SETTING_KEYS.cashMovementEnabled,
      read: () => input.cashMovement.load(),
      write: (value) => input.cashMovement.save(value),
    },
  ];
  const payment = input.paymentMethods;
  if (payment) {
    // 付款方式两个布尔键存在同一个 JSON 里，且 normalize 要求恰好这两个键：
    // 写入一律「读当前 → 合并另一个键 → 整体保存」，不能只写一个键。
    bindings.push(
      {
        key: POS_SETTING_KEYS.manualCard,
        read: async () => (await payment.load()).useManualCard,
        write: async (value) => {
          const current = await payment.load();
          await payment.save({ ...current, useManualCard: value });
        },
      },
      {
        key: POS_SETTING_KEYS.voucher,
        read: async () => (await payment.load()).giftCardEnabled,
        write: async (value) => {
          const current = await payment.load();
          await payment.save({ ...current, giftCardEnabled: value });
        },
      },
    );
  }
  return Object.freeze(bindings);
}

export type PosSettingsAuditInput = Readonly<{
  audit: Pick<AuditRepositoryPort, "append">;
  nowIso(): string;
  createId(): string;
  /** 当前收银员；设置页本来就要求已登录，取不到（已锁屏等）时审计不带身份。 */
  actor(): Readonly<{
    cashierId: string;
    cashierName: string;
    userGuid: string | null;
  }> | null;
}>;

/**
 * 离线临时修改审计。payload 键只用 source / action / status / reason（上传白名单只保留这些），
 * 配置键名放在 action 的值里（本地 assertSafe 只检查键名，不检查值）。
 */
export function createPosSettingsAuditPort(
  input: PosSettingsAuditInput,
): PosSettingsAuditPort {
  return {
    recordOfflineOverride: async (entry) => {
      let actor: ReturnType<typeof auditActorPayload> | null = null;
      try {
        const identity = input.actor();
        actor = identity ? auditActorPayload(identity) : null;
      } catch {
        // 取不到可信收银员身份时仍要留下审计，只是不带身份。
      }
      await input.audit.append([
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
            ...(actor ?? {}),
          },
        },
      ]);
    },
  };
}

/** 用户保存时遇到总部锁定项抛出的错误；设置页据此提示并把草稿恢复为生效值。 */
export class PosSettingManagedError extends Error {
  public readonly code = POS_SETTING_MANAGED_ERROR_CODE;

  public constructor(public readonly key: string) {
    super(POS_SETTING_MANAGED_ERROR_CODE);
    this.name = "PosSettingManagedError";
  }
}

export type GuardedPaymentMethodsSaveInput = Readonly<{
  load(): Promise<PaymentMethodSettings>;
  /** 原始保存（会刷新支付页内存快照）；无变更时直接放行给它。 */
  save(input: PaymentMethodSettings): Promise<unknown>;
  trySet(changes: readonly PosSettingChange[]): Promise<PosSettingsTrySetResult>;
}>;

/**
 * 设置页保存付款方式的守卫：所有用户写入都先过总部策略，界面灰显只是体验层。
 * - 没有变更：放行给原始保存（保持既有行为）。
 * - 有变更：交给服务 trySet；锁定 → 抛 PosSettingManagedError 且不写任何内容；
 *   applied 表示服务已通过 binding 写穿（调用原始保存），这里不能再保存一遍。
 */
export function createGuardedPaymentMethodsSave(
  input: GuardedPaymentMethodsSaveInput,
): (settings: PaymentMethodSettings) => Promise<void> {
  return async (settings) => {
    const current = await input.load();
    const changes: PosSettingChange[] = [];
    if (settings.useManualCard !== current.useManualCard) {
      changes.push({ key: POS_SETTING_KEYS.manualCard, value: settings.useManualCard });
    }
    if (settings.giftCardEnabled !== current.giftCardEnabled) {
      changes.push({ key: POS_SETTING_KEYS.voucher, value: settings.giftCardEnabled });
    }
    if (changes.length === 0) {
      await input.save(settings);
      return;
    }
    const result = await input.trySet(changes);
    switch (result.outcome) {
      case "applied":
        return;
      case "locked":
        throw new PosSettingManagedError(result.key);
      case "unknown-key":
        throw new Error("POS_SETTING_UNKNOWN_KEY");
      default:
        throw new Error("POS_SETTING_WRITE_FAILED");
    }
  };
}
