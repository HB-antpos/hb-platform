import enResource from "@/locales/en/accessPermissions.json";
import zhResource from "@/locales/zh/accessPermissions.json";
import type { AppLanguage } from "@/shared/i18n/types";
import type { AccessPermission } from "./access-management-types";
import type { PosTerminalPermissionOption } from "./types";

interface PermissionText {
  name: string;
  description: string;
}

interface AccessPermissionPresentationResource {
  genericDescription: string;
  genericRoleDescription: string;
  unknownCategory: string;
  categories: Record<string, string>;
  permissions: Record<string, PermissionText>;
  roles: Record<string, string>;
}

const resources: Record<AppLanguage, AccessPermissionPresentationResource> = {
  en: enResource,
  zh: zhResource,
};

function containsChinese(value: string | undefined) {
  return Boolean(value && /[\u3400-\u9fff]/u.test(value));
}

function findCaseInsensitive(values: Record<string, string>, key: string) {
  const normalizedKey = key.trim().toLowerCase();
  const matchedKey = Object.keys(values).find(
    (candidate) => candidate.toLowerCase() === normalizedKey,
  );
  return matchedKey ? values[matchedKey] : undefined;
}

export function humanizePermissionCode(code: string) {
  const segments = code.trim().split(".").filter(Boolean);
  const action = segments.at(-1);
  if (!action) return code.trim();

  const splitWords = (value: string) =>
    value
      .replace(/([A-Z]+)([A-Z][a-z])/g, "$1 $2")
      .replace(/([a-z\d])([A-Z])/g, "$1 $2")
      .replace(/([A-Za-z])(\d)/g, "$1 $2")
      .replace(/(\d)([A-Za-z])/g, "$1 $2")
      .trim()
      .toLowerCase();
  const actionWords = splitWords(action);
  const simpleActions = new Set([
    "view",
    "create",
    "edit",
    "delete",
    "manage",
    "sync",
    "export",
    "import",
    "approve",
    "review",
  ]);
  const subject = segments.at(-2);
  const phrase =
    subject && simpleActions.has(actionWords)
      ? `${actionWords} ${splitWords(subject)}`
      : actionWords;
  return phrase ? `${phrase[0]?.toUpperCase()}${phrase.slice(1)}` : code.trim();
}

export function localizeAccessPermissionCategory(
  category: string,
  language: AppLanguage,
) {
  const resource = resources[language];
  const localized = resource.categories[category.trim()];
  if (localized) return localized;
  if (language === "en" && containsChinese(category)) {
    return resource.unknownCategory;
  }
  return category.trim() || resource.unknownCategory;
}

export function localizeAccessPermission(
  permission: AccessPermission,
  language: AppLanguage,
): PermissionText {
  const resource = resources[language];
  const known = resource.permissions[permission.name];
  if (known) return known;

  const fallbackName = humanizePermissionCode(permission.name);
  const name =
    language === "en" && containsChinese(permission.displayName)
      ? fallbackName
      : permission.displayName?.trim() || fallbackName;
  const backendDescription = permission.description?.trim();
  const description =
    !backendDescription ||
    (language === "en" && containsChinese(backendDescription))
      ? resource.genericDescription.replace("{{name}}", name)
      : backendDescription;

  return { name, description };
}

/**
 * 收银端权限选项：后端 name / description 只有中文，非中文界面按权限码取语言包里的名称与说明
 * （与账号授权页同一份 accessPermissions.json）；中文界面保持后端原文不变。
 */
export function localizePosTerminalPermissionOptions(
  options: readonly PosTerminalPermissionOption[],
  language: AppLanguage,
): PosTerminalPermissionOption[] {
  if (language === "zh") return [...options];
  return options.map((option) => {
    const text = localizeAccessPermission(
      {
        name: option.code,
        displayName: option.name,
        description: option.description,
        category: option.group,
        isSystemPermission: false,
      },
      language,
    );
    return { ...option, name: text.name, description: text.description };
  });
}

export function localizeAccessRoleName(
  roleName: string,
  language: AppLanguage,
) {
  return (
    findCaseInsensitive(resources[language].roles, roleName) ?? roleName.trim()
  );
}

export function localizeAccessRoleDescription(
  roleName: string,
  description: string | undefined,
  language: AppLanguage,
) {
  const localizedName = localizeAccessRoleName(roleName, language);
  const backendDescription = description?.trim();
  if (
    backendDescription &&
    !(language === "en" && containsChinese(backendDescription))
  ) {
    return backendDescription;
  }
  return resources[language].genericRoleDescription.replace(
    "{{name}}",
    localizedName,
  );
}
