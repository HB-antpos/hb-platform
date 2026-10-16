import { useMemo } from "react";
import { useLocalSearchParams } from "expo-router";
import { useIdentitySession } from "@/modules/identity-admin/user-hooks";
import { getDeviceBoundStoreCode } from "@/modules/shop/device-bound-store-filter";
import { getManageableStoresForSession, getPosEnabledStores } from "@/modules/shop/store-scope";
import { useStores } from "@/modules/shop/use-stores";
import { useAuthStore } from "@/store/auth-store";
import { AdminPersonDetail } from "./admin-person-detail";
import { resolveDetailVariant } from "./detail-rules";
import { parseDetailRouteParams } from "./person-view";
import { StorePersonDetail, type DetailEnvironment } from "./store-person-detail";

/**
 * 统一人员详情（路由 /users/[userGuid]）。
 *
 * 一个页面、两套数据源：
 * - 店长版：没有全局用户查看权限（或在设备模式 / iOS 审核模式）→ store-users 接口，必须带 storeCode；
 * - 管理员版：account 会话 + Users.View → identity-admin 接口。
 *
 * 页面自己按 userGuid 拉取目标用户；路由参数（userName / username / targetStatus / roleNames）只用于
 * 首屏占位标题，不参与任何权限判断——「本人 / 受保护角色」一律用拉回来的目标数据校验。
 */
export default function PersonDetailScreen() {
  const params = useLocalSearchParams();
  const route = useMemo(() => parseDetailRouteParams(params), [params]);
  const identity = useIdentitySession();
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const { stores, isDeviceMode, selectedStoreCode } = useStores();
  const isAdmin = useAuthStore((state) => state.access.isAdmin);

  const env = useMemo<DetailEnvironment>(() => {
    const deviceBoundStoreCode = getDeviceBoundStoreCode({ isDeviceMode, selectedStoreCode });
    return {
      isDeviceMode,
      stores,
      posEnabledStores: getPosEnabledStores(stores),
      manageableStores: getManageableStoresForSession({
        stores,
        isDeviceMode,
        deviceBoundStore: deviceBoundStoreCode ? stores.find((store) => store.storeCode === deviceBoundStoreCode) ?? null : null,
        isAdmin,
      }),
    };
  }, [isAdmin, isDeviceMode, selectedStoreCode, stores]);

  const variant = resolveDetailVariant({
    identityAllowed: identity.allowed,
    isDeviceMode,
    isIosReview: sessionKind === "iosReview",
  });

  // 换账号 / 换目标 / 换版本时整页重建，避免上一个目标的草稿、弹层状态与缓存串到下一个。
  const key = `${identity.actorKey}:${route.userGuid}:${variant}`;
  return variant === "admin" ? (
    <AdminPersonDetail key={key} userGuid={route.userGuid} storeCode={route.storeCode} prefill={route.prefill} env={env} />
  ) : (
    <StorePersonDetail key={key} userGuid={route.userGuid} storeCode={route.storeCode} prefill={route.prefill} env={env} />
  );
}
