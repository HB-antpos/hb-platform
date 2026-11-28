import * as Crypto from "expo-crypto";

import type { OrderingCrypto } from "./ordering-token-source";

/** 订货助手 PKCE 用的随机数与 SHA-256（expo-crypto，原生包里早已带着）。 */
export const expoOrderingCrypto: OrderingCrypto = Object.freeze({
  randomBytes: (length: number) => Crypto.getRandomBytes(length),
  sha256Base64: (text: string) =>
    Crypto.digestStringAsync(Crypto.CryptoDigestAlgorithm.SHA256, text, {
      encoding: Crypto.CryptoEncoding.BASE64,
    }),
});
