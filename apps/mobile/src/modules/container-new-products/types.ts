export type ContainerNewProductBasis = "actual" | "estimated";

export interface ContainerNewProductItem {
  productCode: string;
  /** HB 货号；旧版后端不返回时为 null */
  hbProductNo: string | null;
  /** 装柜数量（最小单位）；旧版后端不返回时为 null */
  quantity: number | null;
  imageUrl: string | null;
  /** 商品条码；没有或旧版后端不返回时为 null，不画条码 */
  barcode: string | null;
  /** 零售价（分店价 → 主档 → 货柜明细计划价）；没有或旧版后端不返回时为 null，不显示 */
  retailPrice: number | null;
  containerNumber: string | null;
  containerCode: string;
  /** 预计到店区间起始日（YYYY-MM-DD 开头）。界面不显示；只在旧版后端没有到仓库日期时作为排序、分组的兜底 */
  estimatedStoreArrivalDate: string;
  /** 预计到店区间结束日（含当天）；界面不显示；旧版后端不返回时为 null，兜底分组时按起始日判断 */
  estimatedStoreArrivalDateEnd: string | null;
  /** 货柜到仓库日期（YYYY-MM-DD）：basis = actual 时是实际到货日期，estimated 时是预计到岸日期兜底；列表排序、过去/未来分组都按它。旧版后端不返回时为 null，页面整列不显示 */
  warehouseArrivalDate: string | null;
  basis: ContainerNewProductBasis;
  /** 新商品（仓库里没有或由本柜新建）；false 为补货的已有商品。旧版后端不返回时按新商品处理（旧接口只返回新商品） */
  isNewProduct: boolean;
}

export interface ContainerNewProductsResponse {
  storeCode: string;
  stateCode: string | null;
  /** 门店所在州的本地今天（YYYY-MM-DD）；旧版后端不返回时为 null，前端用设备日期兜底 */
  localToday: string | null;
  items: ContainerNewProductItem[];
}
