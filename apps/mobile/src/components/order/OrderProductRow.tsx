import { memo } from "react";
import { ActivityIndicator, Pressable, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Text } from "react-native-paper";
import type { StoreOrderProductItem } from "@/modules/shop/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";
import { OrderStepper } from "./OrderStepper";
import { GradeTag, OrderThumbnail } from "./OrderTags";
import { ORDER_MONO_FONT } from "./order-fonts";
import { ORDER_COLORS, formatOrderMoney, resolveOrderStep } from "./order-ui";

interface OrderProductRowProps {
  product: StoreOrderProductItem;
  cartQuantity: number;
  disabled?: boolean;
  isUpdating?: boolean;
  compact?: boolean;
  onAddToCart: (product: StoreOrderProductItem) => void;
  onDecreaseCartQuantity: (product: StoreOrderProductItem, currentQuantity: number) => void;
  onEditCartQuantity: (product: StoreOrderProductItem, currentQuantity: number) => void;
  onIncreaseCartQuantity: (product: StoreOrderProductItem) => void;
}

/** 订货页商品行：名称整行、货号等宽、进货价加粗；未加购只给一个加购按钮，已加购才出现步进器。 */
export const OrderProductRow = memo(function OrderProductRow({
  product,
  cartQuantity,
  disabled = false,
  isUpdating = false,
  compact = false,
  onAddToCart,
  onDecreaseCartQuantity,
  onEditCartQuantity,
  onIncreaseCartQuantity,
}: OrderProductRowProps) {
  const { t } = useAppTranslation("common");
  const step = resolveOrderStep(product.minOrderQuantity);
  const inCart = cartQuantity > 0;
  const name = product.productName || product.productCode;

  return (
    <View style={[styles.row, inCart ? styles.rowInCart : null]}>
      <OrderThumbnail uri={product.productImage} size={compact ? 48 : 56} />
      <View style={styles.body}>
        <Text numberOfLines={2} style={styles.name}>
          {name}
        </Text>
        <View style={styles.bottomRow}>
          <View style={styles.info}>
            <View style={styles.metaRow}>
              <GradeTag grade={product.grade} />
              <Text numberOfLines={1} style={styles.itemNumber}>
                {product.itemNumber || product.productCode}
              </Text>
              {step > 1 ? (
                <Text numberOfLines={1} style={styles.metaMuted}>
                  · {t("orderRow.minOrder", { quantity: step })}
                </Text>
              ) : null}
            </View>
            <Text numberOfLines={1} style={styles.priceLine}>
              <Text style={styles.priceLabel}>{t("orderRow.importPrice")} </Text>
              <Text style={styles.priceMain}>{formatOrderMoney(product.importPrice)}</Text>
              <Text style={styles.priceLabel}>{"  "}{t("orderRow.retailPrice")} </Text>
              <Text style={styles.priceSub}>{formatOrderMoney(product.oemPrice)}</Text>
            </Text>
          </View>
          {inCart ? (
            <OrderStepper
              quantity={cartQuantity}
              step={step}
              compact={compact}
              disabled={disabled}
              busy={isUpdating}
              accessibilityLabel={t("labels.editCartQuantity", { quantity: cartQuantity })}
              decreaseLabel={t("orderRow.decrease")}
              increaseLabel={t("orderRow.increase")}
              removeLabel={t("orderRow.remove")}
              onDecrease={() => onDecreaseCartQuantity(product, cartQuantity)}
              onIncrease={() => onIncreaseCartQuantity(product)}
              onEdit={() => onEditCartQuantity(product, cartQuantity)}
            />
          ) : (
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={t("orderRow.addAccessibility", { name, quantity: step })}
              disabled={disabled || isUpdating}
              onPress={() => onAddToCart(product)}
              style={({ pressed }) => [
                styles.addButton,
                compact ? styles.addButtonCompact : null,
                disabled ? styles.addButtonDisabled : null,
                pressed ? styles.addButtonPressed : null,
              ]}
            >
              {isUpdating ? (
                <ActivityIndicator size="small" color={HB_COLORS.action} />
              ) : (
                <>
                  <MaterialCommunityIcons
                    name="plus"
                    size={18}
                    color={disabled ? ORDER_COLORS.placeholderIcon : HB_COLORS.action}
                  />
                  <Text style={[styles.addText, disabled ? styles.addTextDisabled : null]}>
                    {step > 1 ? t("orderRow.addWithStep", { step }) : t("orderRow.add")}
                  </Text>
                </>
              )}
            </Pressable>
          )}
        </View>
      </View>
    </View>
  );
});

const styles = StyleSheet.create({
  row: {
    flex: 1,
    flexDirection: "row",
    gap: 12,
    paddingHorizontal: 12,
    paddingVertical: 10,
    backgroundColor: HB_COLORS.white,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outline,
  },
  rowInCart: {
    backgroundColor: ORDER_COLORS.inCartRow,
  },
  body: {
    flex: 1,
    minWidth: 0,
    gap: 6,
  },
  name: {
    color: HB_COLORS.textPrimary,
    fontSize: 14,
    lineHeight: 18,
    fontWeight: "600",
  },
  bottomRow: {
    flexDirection: "row",
    alignItems: "flex-end",
    gap: 8,
  },
  info: {
    flex: 1,
    minWidth: 0,
    gap: 4,
  },
  metaRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
  },
  itemNumber: {
    flexShrink: 1,
    color: HB_COLORS.textSecondary,
    fontSize: 12,
    lineHeight: 16,
    fontFamily: ORDER_MONO_FONT,
  },
  metaMuted: {
    flexShrink: 0,
    color: ORDER_COLORS.subtleText,
    fontSize: 12,
    lineHeight: 16,
  },
  priceLine: {
    fontVariant: ["tabular-nums"],
  },
  priceLabel: {
    color: ORDER_COLORS.subtleText,
    fontSize: 11,
  },
  priceMain: {
    color: HB_COLORS.textPrimary,
    fontSize: 15,
    fontWeight: "700",
  },
  priceSub: {
    color: HB_COLORS.textSecondary,
    fontSize: 12,
  },
  addButton: {
    width: 132,
    height: 44,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 4,
    borderRadius: 8,
    borderWidth: 1,
    borderColor: ORDER_COLORS.stepperBorder,
    backgroundColor: ORDER_COLORS.tonalBackground,
  },
  addButtonCompact: {
    width: 124,
  },
  addButtonDisabled: {
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  addButtonPressed: {
    backgroundColor: "#D1E0FF",
  },
  addText: {
    color: HB_COLORS.action,
    fontSize: 14,
    fontWeight: "600",
  },
  addTextDisabled: {
    color: ORDER_COLORS.placeholderIcon,
  },
});
