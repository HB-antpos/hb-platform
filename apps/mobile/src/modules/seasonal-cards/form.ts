import type {
  SeasonalCardCatalogItem,
  SeasonalCardPriceOption,
  SeasonalCardType,
} from "@/modules/seasonal-cards/types";

export interface SeasonalCardCatalogGroup {
  cardType: SeasonalCardType;
  cardTypeName: string;
  options: SeasonalCardCatalogItem[];
}

const SEASONAL_CARD_TYPE_FALLBACK_LABELS: Record<SeasonalCardType, string> = {
  1: "Christmas",
  2: "Valentines Day",
  3: "Mother's Day",
  4: "Easter",
  5: "Father's Day",
};
const SEASONAL_CARD_TYPE_VALUES: SeasonalCardType[] = [1, 2, 3, 4, 5];

const SEASONAL_CARD_PRICE_OPTION_LABELS: Record<Exclude<SeasonalCardPriceOption, 4>, string> = {
  1: "$1",
  2: "$2",
  3: "$3",
};

function isSeasonalCardType(value: number): value is SeasonalCardType {
  return value >= 1 && value <= 5;
}

export function getSeasonalCardTypeFallbackLabel(cardType: SeasonalCardType | null) {
  if (cardType == null) {
    return "";
  }
  return SEASONAL_CARD_TYPE_FALLBACK_LABELS[cardType];
}

export function getSeasonalCardHistoryCardTypeOptions() {
  return SEASONAL_CARD_TYPE_VALUES.map((value) => ({
    key: String(value),
    label: SEASONAL_CARD_TYPE_FALLBACK_LABELS[value],
  }));
}

export function getSeasonalCardDisplayName(
  cardType: SeasonalCardType | null,
  cardTypeName?: string | null
) {
  const trimmedName = cardTypeName?.trim();
  return trimmedName || getSeasonalCardTypeFallbackLabel(cardType);
}

export function getSeasonalCardLocalizedTypeLabel(
  cardType: SeasonalCardType | null,
  cardTypeName: string | null | undefined,
  translate: (key: `cardTypes.${SeasonalCardType}`) => string
) {
  if (cardType != null) {
    return translate(`cardTypes.${cardType}`);
  }

  const trimmedName = cardTypeName?.trim();
  return trimmedName || "";
}

export function isSeasonalCardCustomPriceOption(
  option: SeasonalCardCatalogItem | null
) {
  if (!option) {
    return false;
  }

  return option.allowsCustomUnitPrice || option.priceOption === 4;
}

function sortCatalogOptions(left: SeasonalCardCatalogItem, right: SeasonalCardCatalogItem) {
  const leftOrder = left.sortOrder ?? Number.MAX_SAFE_INTEGER;
  const rightOrder = right.sortOrder ?? Number.MAX_SAFE_INTEGER;
  if (leftOrder !== rightOrder) {
    return leftOrder - rightOrder;
  }

  return left.catalogGuid.localeCompare(right.catalogGuid, undefined, {
    sensitivity: "base",
  });
}

export function getSeasonalCardCatalogGroups(
  catalog: SeasonalCardCatalogItem[]
): SeasonalCardCatalogGroup[] {
  const groups = new Map<SeasonalCardType, SeasonalCardCatalogItem[]>();
  const groupNames = new Map<SeasonalCardType, string>();

  catalog.forEach((item) => {
    if (!item.isEnabled || item.cardType == null || !isSeasonalCardType(item.cardType)) {
      return;
    }

    const current = groups.get(item.cardType) ?? [];
    current.push(item);
    groups.set(item.cardType, current);
    groupNames.set(
      item.cardType,
      getSeasonalCardDisplayName(item.cardType, item.cardTypeName)
    );
  });

  return Array.from(groups.entries())
    .map(([cardType, options]) => ({
      cardType,
      cardTypeName: groupNames.get(cardType) ?? getSeasonalCardTypeFallbackLabel(cardType),
      options: options.slice().sort(sortCatalogOptions),
    }))
    .sort((left, right) => left.cardType - right.cardType);
}

export function formatSeasonalCardPriceOptionLabel(
  option: SeasonalCardCatalogItem,
  otherLabel: string
) {
  const backendLabel = option.priceLabel.trim() || option.priceOptionName.trim();
  if (backendLabel) {
    return backendLabel;
  }

  if (option.priceOption === 4 || option.allowsCustomUnitPrice) {
    return otherLabel;
  }

  if (option.priceOption && option.priceOption in SEASONAL_CARD_PRICE_OPTION_LABELS) {
    return SEASONAL_CARD_PRICE_OPTION_LABELS[
      option.priceOption as Exclude<SeasonalCardPriceOption, 4>
    ];
  }

  if (option.fixedUnitPrice != null) {
    return `$${option.fixedUnitPrice.toFixed(Number.isInteger(option.fixedUnitPrice) ? 0 : 2)}`;
  }

  return otherLabel;
}

export function buildSeasonalCardYearOptions(
  baseYear = new Date().getFullYear(),
  yearsBefore = 1,
  yearsAfter = 3
) {
  const years: number[] = [];
  for (let year = baseYear - yearsBefore; year <= baseYear + yearsAfter; year += 1) {
    years.push(year);
  }
  return years;
}
