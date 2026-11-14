import { useCallback, useEffect, useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { useAuthStore } from "@/store/auth-store";
import { fetchPackScope } from "./api";
import { ownerCodesOf, resolveActiveOwner, savedOwnerCode } from "./packing-logic";
import { usePackOwnerStore } from "./pack-owner-store";
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

/** 当前账号的打包范围（分店 / 总仓）和本机记住的当前地点。 */
export function usePackScope(enabled = true): PackScopeState {
  const user = useAuthStore((state) => state.user);
  const userGuid = user?.userGUID || user?.userGuid || null;
  const selections = usePackOwnerStore((state) => state.selections);
  const hydrate = usePackOwnerStore((state) => state.hydrate);
  const setSelection = usePackOwnerStore((state) => state.setOwner);

  useEffect(() => {
    void hydrate();
  }, [hydrate]);

  const query = useQuery({
    queryKey: ["packing", "scope", userGuid],
    queryFn: ({ signal }) => fetchPackScope(signal),
    enabled: enabled && Boolean(userGuid),
    staleTime: 5 * 60_000,
  });

  const scope = query.data ?? null;
  const owners = useMemo(() => scope?.owners ?? [], [scope]);
  const saved = savedOwnerCode(selections, userGuid);
  const owner = useMemo(() => resolveActiveOwner(owners, saved), [owners, saved]);
  const ownerCodes = useMemo(() => ownerCodesOf(owners), [owners]);
  const setOwner = useCallback(
    (code: string) => {
      if (userGuid) setSelection(userGuid, code);
    },
    [setSelection, userGuid],
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
