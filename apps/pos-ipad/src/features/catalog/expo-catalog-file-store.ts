import type { File as ExpoFile } from "expo-file-system";

import type { CatalogFileStorePort } from "./catalog-file-sync";

type ExpoFileSystemModule = typeof import("expo-file-system");

function expoFileSystem(): ExpoFileSystemModule {
  // 同步 require 让 Metro 将原生文件系统放入主 bundle，同时避免 Node / Jest 测试在未调用时解析原生入口。
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  return require("expo-file-system") as ExpoFileSystemModule;
}

/**
 * 整文件目录下载的缓存目录（位于系统缓存目录下）：只按文件名在同一目录下读写，
 * 追加与分块读取都走 FileHandle，内存只占一个分段或一个读取块。
 */
export class ExpoCatalogFileStore implements CatalogFileStorePort {
  public constructor(private readonly directoryName: string) {}

  public async size(name: string): Promise<number | null> {
    const file = this.file(name);
    return file.exists ? file.size : null;
  }

  public async append(name: string, bytes: Uint8Array): Promise<void> {
    const file = this.file(name);
    if (!file.exists) file.create();
    const handle = file.open();
    try {
      handle.offset = handle.size ?? file.size;
      handle.writeBytes(bytes);
    } finally {
      handle.close();
    }
  }

  public async truncate(name: string): Promise<void> {
    const file = this.file(name);
    if (file.exists) file.delete();
    file.create();
  }

  public async *readChunks(name: string, chunkSize: number): AsyncIterable<Uint8Array> {
    const handle = this.file(name).open();
    try {
      while (true) {
        const bytes = handle.readBytes(chunkSize);
        if (bytes.length === 0) return;
        yield bytes;
      }
    } finally {
      handle.close();
    }
  }

  public async readAll(name: string): Promise<Uint8Array<ArrayBuffer>> {
    return this.file(name).bytes();
  }

  public async rename(from: string, to: string): Promise<void> {
    const destination = this.file(to);
    if (destination.exists) destination.delete();
    this.file(from).move(destination);
  }

  public async delete(name: string): Promise<void> {
    const file = this.file(name);
    if (file.exists) file.delete();
  }

  public async list(): Promise<readonly string[]> {
    const { Directory, File, Paths } = expoFileSystem();
    const directory = new Directory(Paths.cache, this.directoryName);
    if (!directory.exists) return [];
    return directory
      .list()
      .filter((entry): entry is ExpoFile => entry instanceof File)
      .map((file) => file.name);
  }

  private file(name: string): ExpoFile {
    const { Directory, File, Paths } = expoFileSystem();
    const directory = new Directory(Paths.cache, this.directoryName);
    directory.create({ idempotent: true, intermediates: true });
    return new File(directory, name);
  }
}
