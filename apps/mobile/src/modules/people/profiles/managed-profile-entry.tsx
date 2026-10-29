// 人员详情里的「个人信息」入口行（样式同 ActionRow）。
// 是否显示由 detail-rules 的 canOpenProfile 决定（调用方 `permissions.canOpenProfile ? <… /> : null`，不满足整行隐藏而不是置灰）；
// 这里额外要求 account 会话（设备模式 / iOS 审核模式没有该接口），并按需取一次详情，在副标题里显示 待补全 / 已完善。
// 取详情失败只是不显示副标题，不影响入口；详情 query 与编辑页共用同一个 key，进编辑页时直接命中缓存。
import { useCallback } from "react";
import { useRouter } from "expo-router";
import { useQuery } from "@tanstack/react-query";
import { useIdentitySession } from "@/modules/identity-admin/user-hooks";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { PERMISSIONS } from "@/shared/utils/access";
import { buildProfileRoute } from "../detail/detail-navigation";
import { ActionRow, InfoCard } from "../ui";
import { fetchManagedProfileDetail } from "./managed-profile-api";
import { resolveDetailMissing } from "./managed-profile-logic";

export function ManagedProfileEntry({ userGuid }: { userGuid: string }) {
  const router = useRouter();
  const { t } = useAppTranslation("peopleProfiles");
  const { allowed, actorKey } = useIdentitySession(PERMISSIONS.EmployeeProfiles.ViewManagedStore);

  const detailQuery = useQuery({
    queryKey: ["managed-profiles", actorKey, "detail", userGuid],
    enabled: allowed && Boolean(userGuid),
    retry: false,
    staleTime: 30_000,
    queryFn: ({ signal }) => fetchManagedProfileDetail(userGuid, actorKey, signal),
  });
  const open = useCallback(() => {
    router.push(buildProfileRoute({ userGuid }) as unknown as Parameters<typeof router.push>[0]);
  }, [router, userGuid]);

  if (!allowed) return null;

  const detail = detailQuery.data;
  const missing = detail ? resolveDetailMissing(detail) : null;
  const subtitle = missing
    ? missing.length === 0
      ? t("entry.complete")
      : [t("entry.pending"), ...missing.map((key) => t(`missing.${key}`))].join(" · ")
    : undefined;

  return (
    <InfoCard>
      <ActionRow
        first
        icon="card-account-details-outline"
        tone="violet"
        title={t("entry.title")}
        subtitle={subtitle}
        onPress={open}
      />
    </InfoCard>
  );
}
