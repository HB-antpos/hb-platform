import { Directory, File } from "expo-file-system";

import type {
  ProductImageCacheFile,
  ProductImageCacheFileSystemPort,
} from "@/features/customer-display";

/**
 * 商品缩略图缓存的 Expo 文件系统适配。与广告缓存适配器的区别：
 * 额外提供文件头读取（用于按魔数校验真实图片格式）和修改时间（用于恢复 LRU 顺序）。
 */
export class ExpoProductImageFileSystem
  implements ProductImageCacheFileSystemPort
{
  public async ensureDirectory(uri: string): Promise<void> {
    new Directory(uri).create({
      idempotent: true,
      intermediates: true,
    });
  }

  public async getSize(uri: string): Promise<number | null> {
    const file = new File(uri);
    return file.exists ? file.size : null;
  }

  public async download(
    remoteUrl: string,
    destinationUri: string,
  ): Promise<void> {
    await File.downloadFileAsync(remoteUrl, new File(destinationUri), {
      idempotent: true,
    });
  }

  public async readHeader(
    uri: string,
    length: number,
  ): Promise<Uint8Array | null> {
    const file = new File(uri);
    if (!file.exists) return null;
    const handle = file.open();
    try {
      return handle.readBytes(length);
    } finally {
      handle.close();
    }
  }

  public async move(
    sourceUri: string,
    destinationUri: string,
  ): Promise<void> {
    const destination = new File(destinationUri);
    if (destination.exists) destination.delete();
    new File(sourceUri).move(destination);
  }

  public async deleteIfExists(uri: string): Promise<void> {
    const file = new File(uri);
    if (file.exists) file.delete();
  }

  public async listFiles(
    rootUri: string,
  ): Promise<readonly ProductImageCacheFile[]> {
    const directory = new Directory(rootUri);
    if (!directory.exists) return [];
    return Object.freeze(
      directory
        .list()
        .filter((entry): entry is File => entry instanceof File)
        .map((file) =>
          Object.freeze({
            uri: file.uri,
            modifiedAtMs: file.modificationTime,
          }),
        ),
    );
  }
}
