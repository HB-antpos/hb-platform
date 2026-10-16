import { useCallback, useMemo } from "react";
import {
  createPosPermissionLabels,
  type PosTranslate,
} from "@/modules/users/pos-terminal-permissions-format";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";

/**
 * 收银权限页统一的取词入口：文案都在 peoplePos 命名空间。
 * `translate` 是不带命名空间前缀的纯字符串版 t，交给 pos-terminal-permissions-format.ts 拼句子；
 * `labels` 给 view 层的纯函数取分组名 / 折扣项名称。
 */
export function usePosPermissionTranslation() {
  const { t } = useAppTranslation(["peoplePos", "common"]);
  const translate = useCallback<PosTranslate>(
    (key, options) => String(t(key, options)),
    [t]
  );
  const labels = useMemo(() => createPosPermissionLabels(translate), [translate]);
  return { t, translate, labels };
}
