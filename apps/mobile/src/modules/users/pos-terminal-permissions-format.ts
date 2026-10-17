import {
  type PosDiscountLevel,
  type PosDiscountScope,
} from "./pos-terminal-permission-presentation";
import type {
  PosGroupSummary,
  PosHighRiskWarning,
  PosPermissionLabelResolver,
} from "./pos-terminal-permissions-view";

/**
 * 把 pos-terminal-permissions-view.ts 算出的结构拼成界面文案。
 * 只依赖一个 `t` 函数（页面里传 i18next 的 t，测试里传读 zh/en JSON 的简易实现），
 * 不引入 React / i18next，保证句子拼装（量词、顿号、折扣档位）能被直接断言。
 */

export type PosTranslate = (key: string, options?: Record<string, string | number>) => string;

/** 折扣项名称：数字档位「整单六折」，手工档位「整单手工折扣」。 */
export function formatPosDiscountItemName(
  t: PosTranslate,
  scope: PosDiscountScope,
  level: PosDiscountLevel
): string {
  const scopeLabel = t(`discount.scopes.${scope}`);
  return level === "manual"
    ? t("discount.manualItemName", { scope: scopeLabel })
    : t("discount.itemName", { scope: scopeLabel, level: t(`discount.levels.${level}`) });
}

export function createPosPermissionLabels(t: PosTranslate): PosPermissionLabelResolver {
  return {
    group: (key) => t(`groups.${key}`),
    discountItem: (scope, level) => formatPosDiscountItemName(t, scope, level),
  };
}

export interface PosGroupSummaryText {
  text: string;
  /** 「未开放」用浅灰色，其余正常灰。 */
  muted: boolean;
}

/** 分组行第二行：「开单、挂单、改数量 等 6 项」/「单行九/八/七折、整单九/八/六折」/「未开放」。 */
export function formatPosGroupSummary(summary: PosGroupSummary, t: PosTranslate): PosGroupSummaryText {
  switch (summary.kind) {
    case "empty":
      return { text: t("group.notEnabled"), muted: true };
    case "names": {
      const names = summary.names.join(t("group.nameJoin"));
      return {
        text:
          summary.remainingCount > 0
            ? t("group.previewMore", { names, value: summary.selectedCount })
            : names,
        muted: false,
      };
    }
    case "discount": {
      const parts = summary.parts.map((part) => {
        const scope = t(`discount.scopes.${part.scope}`);
        const levels = part.levels
          .map((level) => t(`discount.levelsShort.${level}`))
          .join(t("discount.summary.levelJoin"));
        if (part.levels.length === 0) return t("discount.summary.manual", { scope });
        return t(part.manual ? "discount.summary.both" : "discount.summary.numeric", { scope, levels });
      });
      return { text: parts.join(t("discount.summary.partJoin")), muted: false };
    }
  }
}

export interface PosHighRiskWarningText {
  /** 「包含 1 项高风险权限：整单六折。」 */
  summary: string;
  /** 「开启后该员工可以在收银机上直接给整单打六折。」；没有可归类的影响时为 null。 */
  impact: string | null;
}

/** 确认弹层红色警示条文案：先点名新开的高风险项，再说开启后对收银的实际影响。 */
export function formatPosHighRiskWarning(
  warning: PosHighRiskWarning,
  t: PosTranslate
): PosHighRiskWarningText {
  const summary = t("confirm.highRisk.summary", {
    value: warning.count,
    names: warning.names.join(t("confirm.highRisk.nameJoin")),
  });
  if (warning.impacts.length === 0) return { summary, impact: null };

  const impacts = warning.impacts
    .map((impact) =>
      t(
        `confirm.highRisk.impacts.${impact.kind}`,
        impact.level ? { level: t(`discount.levels.${impact.level}`) } : undefined
      )
    )
    .join(t("confirm.highRisk.impactJoin"));
  return { summary, impact: t("confirm.highRisk.impact", { impacts }) };
}

export interface PosFooterSummaryText {
  /** 加粗部分：「2 项改动」；没有改动时是「没有改动」。 */
  main: string;
  /** 不加粗的补充：「（含 1 项高风险）」；没有时为空串。 */
  note: string;
}

export function formatPosFooterSummary(
  changedCount: number,
  addedHighRiskCount: number,
  t: PosTranslate
): PosFooterSummaryText {
  if (changedCount === 0) return { main: t("footer.idle"), note: "" };
  return {
    main: t("footer.pending", { value: changedCount }),
    note: addedHighRiskCount > 0 ? t("footer.riskNote", { value: addedHighRiskCount }) : "",
  };
}
