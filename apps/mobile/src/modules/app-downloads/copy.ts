import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import en from "@/locales/en/appDownloads.json";
import zh from "@/locales/zh/appDownloads.json";
import type { ConfirmationLabels, PolicyField } from "./logic";

export type Copy = typeof zh;

/** 保存、登记等写操作的结果；失败时 message 直接展示在当前弹层内。 */
export interface SaveResult {
  ok: boolean;
  message: string;
}

export function useLocalCopy() {
  const { language } = useAppTranslation("common");
  return (language === "en" ? en : zh) as Copy;
}

/** 文案里的 {name} 占位符替换；本模块直接读 JSON，不经过 i18next 插值。 */
export function fmt(template: string, values: Record<string, string | number>) {
  return template.replace(/\{(\w+)\}/g, (match, key: string) =>
    key in values ? String(values[key]) : match,
  );
}

export function confirmationLabels(copy: Copy): ConfirmationLabels {
  return {
    enabled: copy.summary.enabled,
    disabled: copy.summary.disabled,
    release: copy.summary.release,
    noRelease: copy.summary.none,
    required: copy.summary.required,
    optional: copy.summary.optional,
    allDevices: copy.summary.allDevices,
    minimumVersion: copy.summary.minimumVersion,
    minimumBuild: copy.summary.minimumBuild,
    notes: copy.summary.notes,
    scope: copy.summary.scope,
    colon: copy.summary.colon,
  };
}

export function fieldLabels(copy: Copy, fields: PolicyField[]) {
  return fields.map((field) => copy.editor.fields[field]);
}

/** 本地时区的 YYYY-MM-DD HH:mm；无法解析时原样返回，空值显示破折号。 */
export function formatDateTime(
  value: string | null | undefined,
  withTime = true,
) {
  if (!value) return "—";
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return value;
  const pad = (n: number) => String(n).padStart(2, "0");
  const day = `${parsed.getFullYear()}-${pad(parsed.getMonth() + 1)}-${pad(parsed.getDate())}`;
  return withTime
    ? `${day} ${pad(parsed.getHours())}:${pad(parsed.getMinutes())}`
    : day;
}
