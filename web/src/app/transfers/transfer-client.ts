import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { fromBase64Url, toBase64Url } from '../crypto/base64url';
import {
  CryptoHeader,
  DecryptionError,
  FileMetadata,
  FormatError,
  TransferKeys,
  chunkCountFor,
  ciphertextSizeFor,
  createHeader,
  decodeHeader,
  decryptChunk,
  decryptMetadata,
  deriveKeys,
  encodeHeader,
  encryptFile,
  encryptMetadata,
  generateFileSecret,
} from '../crypto/file-crypto';
import { TransferApi, TransferInfo } from './transfer-api';

export interface SendOptions {
  readonly expiresInHours: number;
  readonly maxDownloads: number | null;
  readonly password: string | null;
}

export interface SentTransfer {
  readonly id: string;
  /** The file secret, base64url-encoded. It goes after the # in the link and is never sent to the server. */
  readonly secret: string;
  /** Proves ownership for revoking later. Keep it on this device only. */
  readonly ownerToken: string;
  readonly expiresAt: Date;
}

/** A transfer whose metadata has been decrypted, ready to download. */
export interface OpenedTransfer {
  readonly info: TransferInfo;
  readonly metadata: FileMetadata;
  readonly keys: TransferKeys;
  readonly header: CryptoHeader;
}

/** Reports progress as a fraction from 0 to 1. */
export type ProgressFn = (fraction: number) => void;

/** Largest file the first version accepts. The browser holds a downloaded file in memory before saving it. */
export const MAX_FILE_BYTES = 200 * 1024 * 1024;

/** Runs the encrypt-and-upload and download-and-decrypt flows. Keys never leave this class's callers. */
@Injectable({ providedIn: 'root' })
export class TransferClient {
  private readonly api = inject(TransferApi);

  async send(file: File, options: SendOptions, onProgress: ProgressFn = () => {}): Promise<SentTransfer> {
    if (file.size > MAX_FILE_BYTES) {
      throw new FormatError('This file is larger than the 200 MB limit.');
    }

    const secret = generateFileSecret();
    const keys = await deriveKeys(secret, options.password);
    const header = createHeader();
    const chunkCount = chunkCountFor(file.size, header.chunkSize);

    const created = await firstValueFrom(
      this.api.create({
        cryptoHeader: toBase64Url(encodeHeader(header)),
        encryptedMetadata: toBase64Url(
          await encryptMetadata(keys.metadata, { name: file.name, type: file.type, size: file.size }),
        ),
        chunkCount,
        ciphertextBytes: ciphertextSizeFor(file.size, header.chunkSize),
        expiresInHours: options.expiresInHours,
        maxDownloads: options.maxDownloads,
        passwordProtected: !!options.password,
      }),
    );

    for await (const chunk of encryptFile(file, keys, header)) {
      await firstValueFrom(this.api.uploadChunk(created.id, created.ownerToken, chunk.index, chunk.data));
      onProgress((chunk.index + 1) / chunkCount);
    }

    await firstValueFrom(this.api.complete(created.id, created.ownerToken));

    return {
      id: created.id,
      secret: toBase64Url(secret),
      ownerToken: created.ownerToken,
      expiresAt: new Date(created.expiresAt),
    };
  }

  /** Fetches a transfer and decrypts its file details with the secret from the link. */
  async open(id: string, secretText: string, password?: string | null): Promise<OpenedTransfer> {
    let secret: Uint8Array<ArrayBuffer>;
    try {
      secret = fromBase64Url(secretText);
    } catch {
      throw new FormatError('The key in this link is damaged.');
    }

    const info = await firstValueFrom(this.api.get(id));

    // If password-protected, the caller must supply a password to derive the correct keys.
    if (info.passwordProtected && !password) {
      throw new PasswordRequiredError();
    }

    const keys = await deriveKeys(secret, password);
    const header = decodeHeader(fromBase64Url(info.cryptoHeader));
    const metadata = await decryptMetadata(keys.metadata, fromBase64Url(info.encryptedMetadata));

    return { info, metadata, keys, header };
  }

  /** Downloads and decrypts every chunk. Counts as one download against the transfer's limit. */
  async download(opened: OpenedTransfer, onProgress: ProgressFn = () => {}): Promise<Blob> {
    const { info, keys, header, metadata } = opened;
    const grant = await firstValueFrom(this.api.startDownload(info.id));

    const parts: ArrayBuffer[] = [];
    let total = 0;
    for (let index = 0; index < info.chunkCount; index++) {
      const ciphertext = await firstValueFrom(this.api.downloadChunk(info.id, grant.downloadToken, index));
      const plaintext = await decryptChunk(keys.content, header, index, index === info.chunkCount - 1, ciphertext);
      parts.push(plaintext);
      total += plaintext.byteLength;
      onProgress((index + 1) / info.chunkCount);
    }

    if (total !== metadata.size) {
      throw new DecryptionError('The downloaded file is not the size the sender recorded.');
    }

    return new Blob(parts, { type: metadata.type || 'application/octet-stream' });
  }
}

/** Thrown when a transfer requires a password but none was provided. */
export class PasswordRequiredError extends Error {
  constructor() {
    super('This transfer is protected with a password.');
    this.name = 'PasswordRequiredError';
  }
}
