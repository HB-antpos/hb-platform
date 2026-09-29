export const CONTAINER_NEW_PRODUCTS_PERMISSION = "Container.MobileNewProductsView";

export function canViewContainerNewProducts(
  isAuthenticated: boolean,
  hasPermission: (permission: string) => boolean,
  isReview: boolean,
) {
  return isAuthenticated && !isReview && hasPermission(CONTAINER_NEW_PRODUCTS_PERMISSION);
}
