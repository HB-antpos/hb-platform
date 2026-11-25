import type { Href } from "expo-router";

// 打包各页的路由（纯函数，不依赖打印 / 原生模块，方便单测）。
//
// 打包拆成两个入口：「分店打包」走 /packing，「仓库打包」走 /warehouse-packing。两套路由目录挂的是同一批页面，
// 页面按当前路径判断自己属于哪一边（packSideOfPath），跳转时用 packRoutes(side) 留在同一边的路由栈里——
// 否则仓库账号从仓库打包点进箱详情会落到 /packing 栈，返回时回到分店打包首页，被菜单可见性纠偏弹走。

export type PackSide = "store" | "warehouse";

const BASE_SEGMENT: Record<PackSide, string> = {
  store: "packing",
  warehouse: "warehouse-packing",
};

/** 当前路径属于哪个打包入口；usePathname 不带 (shell) 分组，路径以 /warehouse-packing 开头即仓库打包。 */
export function packSideOfPath(pathname: string | null | undefined): PackSide {
  const first = (pathname ?? "").split("/").filter(Boolean)[0];
  return first === BASE_SEGMENT.warehouse ? "warehouse" : "store";
}

/** 某个打包入口的全部页面路由。 */
export function packRoutes(side: PackSide) {
  const base = `/${BASE_SEGMENT[side]}`;
  const shell = `/(shell)${base}`;
  return {
    /** 打包入口（首页）。 */
    home: shell as Href,
    locate: `${shell}/locate` as Href,
    /** 批量创建空箱标页 / 最近批次列表页。 */
    batchNew: `${shell}/batch/new` as Href,
    batchList: `${shell}/batch` as Href,
    /**
     * 一批空箱标的连续打印页。autoPrint：刚点「创建并打印」进来，页面加载后自动开始打印（copies 是每个箱号的份数）；
     * duplicate：创建请求被识别为重放（这一批之前已建好）。
     */
    batch(batchGuid: string, options?: { autoPrint?: boolean; copies?: number; duplicate?: boolean }) {
      const path = `${base}/batch/${encodeURIComponent(batchGuid)}`;
      const query: string[] = [];
      if (options?.autoPrint) query.push("autoprint=1");
      if (options?.autoPrint && options.copies && options.copies > 1) query.push(`copies=${Math.trunc(options.copies)}`);
      if (options?.duplicate) query.push("duplicate=1");
      return (query.length ? `${path}?${query.join("&")}` : path) as Href;
    },
    /** activated：刚扫空箱标启用后进入，装箱页顶部提示「已启用这张空箱标」。 */
    carton(cartonGuid: string, options?: { activated?: boolean }) {
      const path = `${base}/carton/${encodeURIComponent(cartonGuid)}`;
      return (options?.activated ? `${path}?activated=1` : path) as Href;
    },
    pallet(palletGuid: string) {
      return `${base}/pallet/${encodeURIComponent(palletGuid)}` as Href;
    },
    /** 签收页；带 code 时进入后直接按这个码查找（首页扫到发给本地点的箱 / 托盘）。 */
    receive(code?: string | null) {
      return (code
        ? { pathname: `${shell}/receive`, params: { code } }
        : (`${shell}/receive` as Href)) as unknown as Href;
    },
    /** 按订单签收页。 */
    receiveOrder(orderGuid: string) {
      return `${base}/receive-order?orderGuid=${encodeURIComponent(orderGuid)}` as Href;
    },
  };
}

export type PackRoutes = ReturnType<typeof packRoutes>;

// 以下是分店打包（/packing）的路由，保留原导出名，给不在打包页面里的调用方与既有单测用。
const STORE_ROUTES = packRoutes("store");

/** 打包入口（首页）。 */
export const PACKING_HOME = STORE_ROUTES.home;
export const PACKING_LOCATE = STORE_ROUTES.locate;
export const PACKING_RECEIVE = STORE_ROUTES.receive();

/** 批量创建空箱标页 / 最近批次列表页。 */
export const PACKING_BATCH_NEW = STORE_ROUTES.batchNew;
export const PACKING_BATCH_LIST = STORE_ROUTES.batchList;

export const batchRoute = STORE_ROUTES.batch;
export const cartonRoute = STORE_ROUTES.carton;
export const palletRoute = STORE_ROUTES.pallet;
export const receiveRoute = STORE_ROUTES.receive;
