import type { ReceiptPrinterSettings } from "../db/pos-settings-repository";

export type TrustedReceiptStorePresentation = Readonly<{
  deviceCode: string;
  storeCode: string;
  storeName: string;
  terminalName: string;
}>;

export type TrustedReceiptStorePresentationSource =
  () => Promise<TrustedReceiptStorePresentation>;

export type TrustedReceiptSettingsPersister =
  (settings: ReceiptPrinterSettings) => Promise<void>;

const CONTROL_CHARACTERS = /[\u0000-\u001f\u007f-\u009f]/u;
const MAX_STORE_NAME_CHARACTERS = 120;

/**
 * 当前分店本机保存资料优先，设备展示名仅在本机为空时兜底。
 * scope 不匹配时只清资料、保留 peripheral/开关，并把当前店号持久化绑定，
 * 避免旧店资料在下一次读取时被再次当作 legacy 采用。
 * 换店清空资料时同时清空总部下发版本与已回执版本，下一轮同步会按新店重新拉取。
 */
export async function resolveTrustedReceiptPrinterSettings(
  current: ReceiptPrinterSettings,
  expectedStoreCode: string,
  readPresentation?: TrustedReceiptStorePresentationSource,
  save?: TrustedReceiptSettingsPersister,
): Promise<ReceiptPrinterSettings> {
  const storeCode = expectedStoreCode.trim();
  const savedStoreCode = current.profileStoreCode.trim();
  const savedStoreName = safeStoreName(current.storeName);
  const deviceStoreName = await readTrustedStoreName(
    readPresentation,
    storeCode,
  );

  // scope 不匹配：旧店资料整体作废，安全回退当前店名/店号并保留硬件。
  if (savedStoreCode !== "" && savedStoreCode !== storeCode) {
    // 落盘只绑定当前店并清空资料；临时兜底店名不能当作本机资料持久化，
    // 否则下次读取会锁定兜底值并阻止设备展示名兜底。
    const persisted = Object.freeze({
      ...current,
      profileStoreCode: storeCode,
      brandName: "",
      storeName: "",
      address: "",
      phone: "",
      abn: "",
      returnPolicy: "",
      voucherTerms: "",
      installmentTerms: "",
      profileVersion: 0,
      profileAckedVersion: 0,
    });
    await persist(save, persisted);
    return Object.freeze({
      ...persisted,
      storeName: deviceStoreName ?? storeCode,
    });
  }

  // 当前店本机保存值优先；本机店名为空时才用设备展示名兜底。
  const storeName = savedStoreName ?? deviceStoreName ?? storeCode;

  // 旧有资料（含任何非空字段）首次读取当前分店时持久化绑定店号。
  if (savedStoreCode === "" && hasProfileData(current)) {
    const bound = Object.freeze({ ...current, profileStoreCode: storeCode });
    if (!(await persist(save, bound))) {
      // 绑定落盘失败时不能继续采用无 scope 的旧店资料：本次只返回
      // 保留硬件的安全 fallback，下一次读取仍会安全重试绑定。
      const cleared = Object.freeze({
        ...current,
        profileStoreCode: storeCode,
        brandName: "",
        storeName: "",
        address: "",
        phone: "",
        abn: "",
        returnPolicy: "",
        voucherTerms: "",
        installmentTerms: "",
        profileVersion: 0,
        profileAckedVersion: 0,
      });
      return Object.freeze({
        ...cleared,
        storeName: deviceStoreName ?? storeCode,
      });
    }
    return Object.freeze({ ...current, profileStoreCode: storeCode, storeName });
  }

  return Object.freeze({
    ...current,
    storeName,
  });
}

function hasProfileData(settings: ReceiptPrinterSettings): boolean {
  return (
    settings.brandName.trim() !== "" ||
    settings.storeName.trim() !== "" ||
    settings.address.trim() !== "" ||
    settings.phone.trim() !== "" ||
    settings.abn.trim() !== "" ||
    settings.returnPolicy.trim() !== "" ||
    settings.voucherTerms.trim() !== "" ||
    settings.installmentTerms.trim() !== ""
  );
}

async function readTrustedStoreName(
  readPresentation: TrustedReceiptStorePresentationSource | undefined,
  storeCode: string,
): Promise<string | null> {
  if (!readPresentation) return null;
  try {
    const presentation = await readPresentation();
    if (presentation.storeCode.trim() === storeCode) {
      return safeStoreName(presentation.storeName);
    }
  } catch {
    // 中文注释：展示缓存是可选增强，失败时继续使用本机设置或可信门店编码。
  }
  return null;
}

async function persist(
  save: TrustedReceiptSettingsPersister | undefined,
  settings: ReceiptPrinterSettings,
): Promise<boolean> {
  if (!save) return true;
  try {
    await save(settings);
    return true;
  } catch {
    // 中文注释：绑定/清理落盘失败不阻断打印，下一次读取会安全重试。
    return false;
  }
}

function safeStoreName(value: string): string | null {
  const trimmed = value.trim();
  if (!trimmed || CONTROL_CHARACTERS.test(trimmed)) return null;
  return [...trimmed].slice(0, MAX_STORE_NAME_CHARACTERS).join("");
}
