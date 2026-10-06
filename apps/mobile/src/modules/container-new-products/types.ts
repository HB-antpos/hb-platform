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
  /** 预计到店区间起始日（YYYY-MM-DD 开头） */
  estimatedStoreArrivalDate: string;
  /** 预计到店区间结束日（含当天）；旧版后端不返回时为 null，按单一到店日显示 */
  estimatedStoreArrivalDateEnd: string | null;
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
