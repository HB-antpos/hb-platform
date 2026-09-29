export type ContainerNewProductBasis = "actual" | "estimated";

export interface ContainerNewProductItem {
  productCode: string;
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
