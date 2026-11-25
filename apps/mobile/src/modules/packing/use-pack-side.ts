import { useMemo } from "react";
import { usePathname } from "expo-router";
import { packRoutes, packSideOfPath, type PackSide } from "./pack-routes";

/** 当前页面属于哪个打包入口（仓库打包 / 分店打包），按路径判断，不需要层层传参。 */
export function usePackSide(): PackSide {
  return packSideOfPath(usePathname());
}

/** 当前打包入口的页面路由：跳转一律用它，保证留在同一边的路由栈里。 */
export function usePackRoutes() {
  const side = usePackSide();
  return useMemo(() => packRoutes(side), [side]);
}
