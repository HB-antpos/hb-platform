import { useCallback, useEffect, useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { useAuthStore } from "@/store/auth-store";
import { fetchPackScope } from "./api";
import { ownerCodesOf, resolveActiveOwner, savedOwnerCode } from "./packing-logic";
import { usePackOwnerStore } from "./pack-owner-store";
import { usePackSide } from "./use-pack-side";
import type { PackOwner, PackScope } from "./types";

export interface PackScopeState {
  /** 账号能操作的全部地点；加载完成前为空数组。 */
  scope: PackScope | null;
  owners: PackOwner[];
  ownerCodes: string[];
  /** 当前打包的地点；账号没有任何可操作地点时为 null。 */
  owner: PackOwner | null;
  isManager: boolean;
  isAdmin: boolean;
  isLoading: boolean;
  error: unknown;
  setOwner: (code: string) => void;
  refetch: () => void;
}

/**
 * 当前账号在当前打包入口下的打包范围和本机记住的当前地点。
 * 入口按页面路径判断：仓库打包只拿总仓，分店打包只拿账号所属的分店；两个入口各记各的上次地点。
 */
export function usePackScope(enabled = true): PackScopeState {
  const user = useAuthStore((state) => state.user);
  const userGuid = user?.userGUID || user?.userGuid || null;
  const side = usePackSide();
  // 分店打包沿用原来的记忆键（升级后不丢已选的分店）；仓库打包另用一个键，互不覆盖。
  const selectionKey = userGuid && side === "warehouse" ? `${userGuid}@warehouse` : userGuid;
  const selections = usePackOwnerStore((state) => state.selections);
  const hydrate = usePackOwnerStore((state) => state.hydrate);
  const setSelection = usePackOwnerStore((state) => state.setOwner);

  useEffect(() => {
    void hydrate();
  }, [hydrate]);

  const query = useQuery({
    queryKey: ["packing", "scope", userGuid, side],
    queryFn: ({ signal }) => fetchPackScope(signal, side),
    enabled: enabled && Boolean(userGuid),
    staleTime: 5 * 60_000,
  });

  const scope = query.data ?? null;
  const owners = useMemo(() => scope?.owners ?? [], [scope]);
  const saved = savedOwnerCode(selections, selectionKey);
  const owner = useMemo(() => resolveActiveOwner(owners, saved), [owners, saved]);
  const ownerCodes = useMemo(() => ownerCodesOf(owners), [owners]);
  const setOwner = useCallback(
    (code: string) => {
      if (selectionKey) setSelection(selectionKey, code);
    },
    [setSelection, selectionKey],
  );

  return {
    scope,
    owners,
    ownerCodes,
    owner,
    isManager: scope?.isManager ?? false,
    isAdmin: scope?.isAdmin ?? false,
    isLoading: query.isLoading,
    error: query.error,
    setOwner,
    refetch: () => void query.refetch(),
  };
}
