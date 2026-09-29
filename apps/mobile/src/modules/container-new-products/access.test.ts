import assert from "node:assert/strict";
import { canViewContainerNewProducts, CONTAINER_NEW_PRODUCTS_PERMISSION } from "./access";

assert.equal(CONTAINER_NEW_PRODUCTS_PERMISSION, "Container.MobileNewProductsView");
assert.equal(canViewContainerNewProducts(true, (code) => code === CONTAINER_NEW_PRODUCTS_PERMISSION, false), true);
assert.equal(canViewContainerNewProducts(false, () => true, false), false);
assert.equal(canViewContainerNewProducts(true, () => true, true), false);
assert.equal(canViewContainerNewProducts(true, () => false, false), false);

console.log("container-new-products access tests passed");
