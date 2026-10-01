import { Directory, File } from "expo-file-system";

import type { CatalogFileStorePort } from "./catalog-file-sync";

/**
 * 整文件目录下载的缓存目录：只按文件名在同一目录下读写，追加与分块读取都走 FileHandle，
 * 内存只占一个分段或一个读取块。
 */
export class ExpoCatalogFileStore implements CatalogFileStorePort {
  public constructor(private readonly directoryUri: string) {}

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
    const directory = new Directory(this.directoryUri);
    if (!directory.exists) return [];
    return directory
      .list()
      .filter((entry): entry is File => entry instanceof File)
      .map((file) => file.name);
  }

  private file(name: string): File {
    new Directory(this.directoryUri).create({ idempotent: true, intermediates: true });
    return new File(this.directoryUri, name);
  }
}
