export type ContainerNewProductBasis = "actual" | "estimated";

export interface ContainerNewProductItem {
  productCode: string;
  /** HB 货号；旧版后端不返回时为 null */
  hbProductNo: string | null;
  /** 装柜数量（最小单位）；旧版后端不返回时为 null */
  quantity: number | null;
  imageUrl: string | null;
  containerNumber: string | null;
  containerCode: string;
  estimatedStoreArrivalDate: string;
  basis: ContainerNewProductBasis;
}

export interface ContainerNewProductsResponse {
  storeCode: string;
  stateCode: string | null;
  items: ContainerNewProductItem[];
}
