/**
 * Client-side file encryption, built only from standard WebCrypto primitives.
 *
 *   file secret (32 random bytes, travels in the share link)
 *     └─ HKDF-SHA-256 ─┬─ content key  (AES-256-GCM, encrypts the file chunks)
 *                      └─ metadata key (AES-256-GCM, encrypts name, type and size)
 *
 * A file is encrypted in fixed-size chunks so it never has to fit in one WebCrypto call. Each chunk
 * gets its own nonce:
 *
 *   nonce (12 bytes) = random prefix (7) | chunk index, big-endian (4) | last-chunk flag (1)
 *
 * Putting the index and the flag in the nonce means a chunk decrypts only at its original position,
 * and only the true final chunk decrypts as final. Reordered, duplicated or dropped chunks fail the
 * AES-GCM authentication check. This is the STREAM construction (Hoang, Reyhanitabar, Rogaway, Vizár).
 */

export const FORMAT_VERSION = 1;

/** Plaintext bytes per chunk. */
export const DEFAULT_CHUNK_SIZE = 1024 * 1024;

/** Bytes AES-GCM appends to every chunk. */
export const TAG_BYTES = 16;

export const SECRET_BYTES = 32;

const NONCE_PREFIX_BYTES = 7;
const NONCE_BYTES = 12;
const HEADER_BYTES = 1 + NONCE_PREFIX_BYTES + 4;
const MAX_CHUNKS = 0xffffffff;

const encoder = new TextEncoder();
const decoder = new TextDecoder('utf-8', { fatal: true });

// Distinct labels keep the two derived keys, and the two kinds of ciphertext, from ever being interchangeable.
const CONTENT_KEY_INFO = encoder.encode('sealed-transfer/v1/content-key');
const METADATA_KEY_INFO = encoder.encode('sealed-transfer/v1/metadata-key');
const CHUNK_LABEL = encoder.encode('sealed-transfer/v1/chunk');
const METADATA_LABEL = encoder.encode('sealed-transfer/v1/metadata');

/** Raised when ciphertext fails authentication: wrong key, or the data was altered. */
export class DecryptionError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'DecryptionError';
  }
}

/** Raised when a header, secret or metadata blob is not in the expected format. */
export class FormatError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'FormatError';
  }
}

export interface TransferKeys {
  readonly content: CryptoKey;
  readonly metadata: CryptoKey;
}

/** Public parameters stored next to the ciphertext. None of it is secret. */
export interface CryptoHeader {
  readonly version: number;
  readonly noncePrefix: Uint8Array<ArrayBuffer>;
  readonly chunkSize: number;
}

export interface FileMetadata {
  readonly name: string;
  readonly type: string;
  readonly size: number;
}

export interface EncryptedChunk {
  readonly index: number;
  readonly data: ArrayBuffer;
}

export function generateFileSecret(): Uint8Array<ArrayBuffer> {
  return crypto.getRandomValues(new Uint8Array(SECRET_BYTES));
}

/**
 * Derives the two AES keys from the file secret. The keys are non-extractable.
 *
 * When a password is provided it is UTF-8 encoded and used as the HKDF salt,
 * which means the same secret + different password = completely different keys.
 * Without a password the salt is empty (backward-compatible with v1 transfers).
 */
export async function deriveKeys(secret: Uint8Array<ArrayBuffer>, password?: string | null): Promise<TransferKeys> {
  if (secret.byteLength !== SECRET_BYTES) {
    throw new FormatError('The key in this link is not the right length.');
  }

  const salt = password ? encoder.encode(password) : new Uint8Array(0);

  const material = await crypto.subtle.importKey('raw', secret, 'HKDF', false, ['deriveKey']);
  const derive = (info: Uint8Array<ArrayBuffer>) =>
    crypto.subtle.deriveKey(
      { name: 'HKDF', hash: 'SHA-256', salt, info },
      material,
      { name: 'AES-GCM', length: 256 },
      false,
      ['encrypt', 'decrypt'],
    );

  const [content, metadata] = await Promise.all([derive(CONTENT_KEY_INFO), derive(METADATA_KEY_INFO)]);
  return { content, metadata };
}

export function createHeader(chunkSize: number = DEFAULT_CHUNK_SIZE): CryptoHeader {
  assertChunkSize(chunkSize);
  return {
    version: FORMAT_VERSION,
    noncePrefix: crypto.getRandomValues(new Uint8Array(NONCE_PREFIX_BYTES)),
    chunkSize,
  };
}

/** Layout: version (1 byte) | nonce prefix (7 bytes) | chunk size, big-endian (4 bytes). */
export function encodeHeader(header: CryptoHeader): Uint8Array<ArrayBuffer> {
  const bytes = new Uint8Array(HEADER_BYTES);
  bytes[0] = header.version;
  bytes.set(header.noncePrefix, 1);
  new DataView(bytes.buffer).setUint32(1 + NONCE_PREFIX_BYTES, header.chunkSize);
  return bytes;
}

export function decodeHeader(bytes: Uint8Array<ArrayBuffer>): CryptoHeader {
  if (bytes.byteLength !== HEADER_BYTES) {
    throw new FormatError('This transfer has an unreadable header.');
  }
  if (bytes[0] !== FORMAT_VERSION) {
    throw new FormatError('This transfer uses a format this version of the app cannot read.');
  }

  const chunkSize = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getUint32(1 + NONCE_PREFIX_BYTES);
  assertChunkSize(chunkSize);
  return { version: FORMAT_VERSION, noncePrefix: bytes.slice(1, 1 + NONCE_PREFIX_BYTES), chunkSize };
}

/** An empty file still produces one chunk, so there is always a final chunk to authenticate. */
export function chunkCountFor(size: number, chunkSize: number): number {
  return Math.max(1, Math.ceil(size / chunkSize));
}

export function ciphertextSizeFor(size: number, chunkSize: number): number {
  return size + chunkCountFor(size, chunkSize) * TAG_BYTES;
}

export async function encryptChunk(
  key: CryptoKey,
  header: CryptoHeader,
  index: number,
  isLast: boolean,
  plaintext: ArrayBuffer,
): Promise<ArrayBuffer> {
  return crypto.subtle.encrypt(chunkParams(header, index, isLast), key, plaintext);
}

export async function decryptChunk(
  key: CryptoKey,
  header: CryptoHeader,
  index: number,
  isLast: boolean,
  ciphertext: ArrayBuffer,
): Promise<ArrayBuffer> {
  try {
    return await crypto.subtle.decrypt(chunkParams(header, index, isLast), key, ciphertext);
  } catch {
    throw new DecryptionError(`Part ${index + 1} of the file could not be decrypted. The key is wrong or the data was altered.`);
  }
}

/** Encrypts a file chunk by chunk, reading only one chunk into memory at a time. */
export async function* encryptFile(file: Blob, keys: TransferKeys, header: CryptoHeader): AsyncGenerator<EncryptedChunk> {
  const count = chunkCountFor(file.size, header.chunkSize);
  for (let index = 0; index < count; index++) {
    const start = index * header.chunkSize;
    const plaintext = await file.slice(start, start + header.chunkSize).arrayBuffer();
    yield { index, data: await encryptChunk(keys.content, header, index, index === count - 1, plaintext) };
  }
}

/** Returns a random nonce followed by the AES-GCM ciphertext of the JSON-encoded metadata. */
export async function encryptMetadata(key: CryptoKey, metadata: FileMetadata): Promise<Uint8Array<ArrayBuffer>> {
  const nonce = crypto.getRandomValues(new Uint8Array(NONCE_BYTES));
  const ciphertext = await crypto.subtle.encrypt(
    { name: 'AES-GCM', iv: nonce, additionalData: METADATA_LABEL },
    key,
    encoder.encode(JSON.stringify({ name: metadata.name, type: metadata.type, size: metadata.size })),
  );

  const result = new Uint8Array(NONCE_BYTES + ciphertext.byteLength);
  result.set(nonce, 0);
  result.set(new Uint8Array(ciphertext), NONCE_BYTES);
  return result;
}

export async function decryptMetadata(key: CryptoKey, bytes: Uint8Array<ArrayBuffer>): Promise<FileMetadata> {
  if (bytes.byteLength < NONCE_BYTES + TAG_BYTES) {
    throw new FormatError('This transfer has unreadable file details.');
  }

  let plaintext: ArrayBuffer;
  try {
    plaintext = await crypto.subtle.decrypt(
      { name: 'AES-GCM', iv: bytes.slice(0, NONCE_BYTES), additionalData: METADATA_LABEL },
      key,
      bytes.slice(NONCE_BYTES),
    );
  } catch {
    throw new DecryptionError('The key in this link does not match this transfer.');
  }

  // The contents are authenticated, but they were written by the sender's browser, so still check the shape.
  const value: unknown = JSON.parse(decoder.decode(plaintext));
  if (!isFileMetadata(value)) {
    throw new FormatError('This transfer has unreadable file details.');
  }
  return { name: value.name, type: value.type, size: value.size };
}

function isFileMetadata(value: unknown): value is FileMetadata {
  if (typeof value !== 'object' || value === null) {
    return false;
  }
  const candidate = value as Record<string, unknown>;
  return (
    typeof candidate['name'] === 'string' &&
    typeof candidate['type'] === 'string' &&
    typeof candidate['size'] === 'number' &&
    Number.isSafeInteger(candidate['size']) &&
    candidate['size'] >= 0
  );
}

function chunkParams(header: CryptoHeader, index: number, isLast: boolean): AesGcmParams {
  if (!Number.isInteger(index) || index < 0 || index >= MAX_CHUNKS) {
    throw new FormatError('Chunk index is out of range.');
  }

  const nonce = new Uint8Array(NONCE_BYTES);
  nonce.set(header.noncePrefix, 0);
  new DataView(nonce.buffer).setUint32(NONCE_PREFIX_BYTES, index);
  nonce[NONCE_BYTES - 1] = isLast ? 1 : 0;

  // Binding the encoded header means a chunk cannot be replayed under different parameters.
  const headerBytes = encodeHeader(header);
  const additionalData = new Uint8Array(CHUNK_LABEL.byteLength + headerBytes.byteLength);
  additionalData.set(CHUNK_LABEL, 0);
  additionalData.set(headerBytes, CHUNK_LABEL.byteLength);

  return { name: 'AES-GCM', iv: nonce, additionalData };
}

function assertChunkSize(chunkSize: number): void {
  if (!Number.isInteger(chunkSize) || chunkSize < 1 || chunkSize > 0xffffffff) {
    throw new FormatError('Chunk size is out of range.');
  }
}
