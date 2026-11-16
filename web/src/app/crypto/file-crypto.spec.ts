import { describe, it, expect } from 'vitest';
import { fromBase64Url, toBase64Url } from './base64url';
import {
  DecryptionError,
  EncryptedChunk,
  FormatError,
  TAG_BYTES,
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
} from './file-crypto';

// A tiny chunk size keeps multi-chunk tests fast while exercising the same code paths as 1 MiB chunks.
const CHUNK = 16;

function bytes(length: number): Uint8Array<ArrayBuffer> {
  const out = new Uint8Array(length);
  for (let i = 0; i < length; i++) {
    out[i] = (i * 31 + 7) % 256;
  }
  return out;
}

async function encryptAll(plaintext: Uint8Array<ArrayBuffer>) {
  const secret = generateFileSecret();
  const keys = await deriveKeys(secret);
  const header = createHeader(CHUNK);
  const chunks: EncryptedChunk[] = [];
  for await (const chunk of encryptFile(new Blob([plaintext]), keys, header)) {
    chunks.push(chunk);
  }
  return { secret, keys, header, chunks };
}

async function decryptAll(
  keys: Awaited<ReturnType<typeof deriveKeys>>,
  header: ReturnType<typeof createHeader>,
  chunks: readonly ArrayBuffer[],
): Promise<Uint8Array> {
  const parts: Uint8Array[] = [];
  for (let i = 0; i < chunks.length; i++) {
    parts.push(new Uint8Array(await decryptChunk(keys.content, header, i, i === chunks.length - 1, chunks[i])));
  }
  const out = new Uint8Array(parts.reduce((total, part) => total + part.byteLength, 0));
  let offset = 0;
  for (const part of parts) {
    out.set(part, offset);
    offset += part.byteLength;
  }
  return out;
}

describe('base64url', () => {
  it('round-trips every byte value without padding or URL-unsafe characters', () => {
    const input = new Uint8Array(256).map((_, i) => i);
    const text = toBase64Url(input);

    expect(text).toMatch(/^[A-Za-z0-9_-]+$/);
    expect(fromBase64Url(text)).toEqual(input);
  });

  it('rejects characters outside the alphabet', () => {
    expect(() => fromBase64Url('abc+def')).toThrow();
    expect(() => fromBase64Url('abc=')).toThrow();
  });
});

describe('file encryption', () => {
  it('round-trips a file that spans several chunks', async () => {
    const plaintext = bytes(CHUNK * 3 + 5);
    const { keys, header, chunks } = await encryptAll(plaintext);

    expect(chunks.length).toBe(4);
    expect(await decryptAll(keys, header, chunks.map((c) => c.data))).toEqual(plaintext);
  });

  it('round-trips a file that is an exact multiple of the chunk size', async () => {
    const plaintext = bytes(CHUNK * 2);
    const { keys, header, chunks } = await encryptAll(plaintext);

    expect(chunks.length).toBe(2);
    expect(await decryptAll(keys, header, chunks.map((c) => c.data))).toEqual(plaintext);
  });

  it('encrypts an empty file as one authenticated chunk', async () => {
    const { keys, header, chunks } = await encryptAll(new Uint8Array(0));

    expect(chunks.length).toBe(1);
    expect(chunks[0].data.byteLength).toBe(TAG_BYTES);
    expect((await decryptAll(keys, header, [chunks[0].data])).byteLength).toBe(0);
  });

  it('predicts chunk count and ciphertext size', async () => {
    const plaintext = bytes(CHUNK * 3 + 5);
    const { chunks } = await encryptAll(plaintext);
    const total = chunks.reduce((sum, c) => sum + c.data.byteLength, 0);

    expect(chunkCountFor(plaintext.byteLength, CHUNK)).toBe(chunks.length);
    expect(ciphertextSizeFor(plaintext.byteLength, CHUNK)).toBe(total);
    expect(chunkCountFor(0, CHUNK)).toBe(1);
  });

  it('produces different ciphertext for the same file each time', async () => {
    const plaintext = bytes(CHUNK);
    const first = await encryptAll(plaintext);
    const second = await encryptAll(plaintext);

    expect(new Uint8Array(first.chunks[0].data)).not.toEqual(new Uint8Array(second.chunks[0].data));
  });
});

describe('tampering is detected', () => {
  it('rejects a chunk with one flipped bit', async () => {
    const { keys, header, chunks } = await encryptAll(bytes(CHUNK * 2));
    const altered = new Uint8Array(chunks[0].data.slice(0));
    altered[3] ^= 0x01;

    await expect(decryptChunk(keys.content, header, 0, false, altered.buffer)).rejects.toBeInstanceOf(DecryptionError);
  });

  it('rejects chunks that have been swapped', async () => {
    const { keys, header, chunks } = await encryptAll(bytes(CHUNK * 3));

    await expect(decryptAll(keys, header, [chunks[1].data, chunks[0].data, chunks[2].data])).rejects.toBeInstanceOf(
      DecryptionError,
    );
  });

  it('rejects a file whose last chunk was dropped', async () => {
    const { keys, header, chunks } = await encryptAll(bytes(CHUNK * 3));

    await expect(decryptAll(keys, header, [chunks[0].data, chunks[1].data])).rejects.toBeInstanceOf(DecryptionError);
  });

  it('rejects a file with an extra chunk appended', async () => {
    const { keys, header, chunks } = await encryptAll(bytes(CHUNK * 2));

    await expect(decryptAll(keys, header, [chunks[0].data, chunks[1].data, chunks[1].data])).rejects.toBeInstanceOf(
      DecryptionError,
    );
  });

  it('rejects the wrong key', async () => {
    const { header, chunks } = await encryptAll(bytes(CHUNK));
    const otherKeys = await deriveKeys(generateFileSecret());

    await expect(decryptChunk(otherKeys.content, header, 0, true, chunks[0].data)).rejects.toBeInstanceOf(
      DecryptionError,
    );
  });

  it('rejects a chunk decrypted under a different nonce prefix', async () => {
    const { keys, header, chunks } = await encryptAll(bytes(CHUNK));
    const otherHeader = createHeader(CHUNK);

    await expect(decryptChunk(keys.content, otherHeader, 0, true, chunks[0].data)).rejects.toBeInstanceOf(
      DecryptionError,
    );
    expect((await decryptChunk(keys.content, header, 0, true, chunks[0].data)).byteLength).toBe(CHUNK);
  });

  it('rejects a chunk decrypted under a different declared chunk size', async () => {
    const { keys, header, chunks } = await encryptAll(bytes(CHUNK));
    const altered = { ...header, chunkSize: CHUNK * 2 };

    await expect(decryptChunk(keys.content, altered, 0, true, chunks[0].data)).rejects.toBeInstanceOf(DecryptionError);
  });
});

describe('header', () => {
  it('round-trips through its 12-byte encoding', () => {
    const header = createHeader(4096);
    const encoded = encodeHeader(header);

    expect(encoded.byteLength).toBe(12);
    expect(decodeHeader(encoded)).toEqual(header);
  });

  it('rejects an unknown version or wrong length', () => {
    const encoded = encodeHeader(createHeader());
    const wrongVersion = encoded.slice();
    wrongVersion[0] = 2;

    expect(() => decodeHeader(wrongVersion)).toThrow(FormatError);
    expect(() => decodeHeader(encoded.slice(0, 11))).toThrow(FormatError);
  });

  it('rejects a zero chunk size', () => {
    const encoded = encodeHeader(createHeader());
    encoded.fill(0, 8);

    expect(() => decodeHeader(encoded)).toThrow(FormatError);
  });
});

describe('keys and metadata', () => {
  it('derives the same keys from the same secret', async () => {
    const secret = generateFileSecret();
    const header = createHeader(CHUNK);
    const sender = await deriveKeys(secret);
    const recipient = await deriveKeys(fromBase64Url(toBase64Url(secret)));
    const first = await encryptFile(new Blob([bytes(8)]), sender, header).next();
    const chunk: EncryptedChunk = first.value;

    expect(new Uint8Array(await decryptChunk(recipient.content, header, 0, true, chunk.data))).toEqual(bytes(8));
  });

  it('rejects a secret of the wrong length', async () => {
    await expect(deriveKeys(new Uint8Array(16))).rejects.toBeInstanceOf(FormatError);
  });

  it('round-trips file metadata, including non-ASCII names', async () => {
    const keys = await deriveKeys(generateFileSecret());
    const metadata = { name: 'rapport-été-2026.pdf', type: 'application/pdf', size: 123456 };

    expect(await decryptMetadata(keys.metadata, await encryptMetadata(keys.metadata, metadata))).toEqual(metadata);
  });

  it('rejects metadata under the wrong key or after alteration', async () => {
    const keys = await deriveKeys(generateFileSecret());
    const other = await deriveKeys(generateFileSecret());
    const sealed = await encryptMetadata(keys.metadata, { name: 'a.txt', type: 'text/plain', size: 1 });
    const altered = sealed.slice();
    altered[altered.byteLength - 1] ^= 0x01;

    await expect(decryptMetadata(other.metadata, sealed)).rejects.toBeInstanceOf(DecryptionError);
    await expect(decryptMetadata(keys.metadata, altered)).rejects.toBeInstanceOf(DecryptionError);
  });

  it('keeps the content key and the metadata key separate', async () => {
    const keys = await deriveKeys(generateFileSecret());
    const sealed = await encryptMetadata(keys.metadata, { name: 'a.txt', type: 'text/plain', size: 1 });

    await expect(decryptMetadata(keys.content, sealed)).rejects.toBeInstanceOf(DecryptionError);
  });

  it('derives different keys when a password is provided', async () => {
    const secret = generateFileSecret();
    const noPassword = await deriveKeys(secret);
    const withPassword = await deriveKeys(secret, 'my-secret');

    const metadata = { name: 'a.txt', type: 'text/plain', size: 1 };
    const sealed = await encryptMetadata(withPassword.metadata, metadata);

    // The no-password keys cannot decrypt password-protected metadata.
    await expect(decryptMetadata(noPassword.metadata, sealed)).rejects.toBeInstanceOf(DecryptionError);
    // The correct password decrypts fine.
    expect(await decryptMetadata(withPassword.metadata, sealed)).toEqual(metadata);
  });

  it('derives different keys for different passwords', async () => {
    const secret = generateFileSecret();
    const keys1 = await deriveKeys(secret, 'password1');
    const keys2 = await deriveKeys(secret, 'password2');

    const metadata = { name: 'b.txt', type: 'text/plain', size: 2 };
    const sealed = await encryptMetadata(keys1.metadata, metadata);

    await expect(decryptMetadata(keys2.metadata, sealed)).rejects.toBeInstanceOf(DecryptionError);
    expect(await decryptMetadata(keys1.metadata, sealed)).toEqual(metadata);
  });

  it('derives the same keys with null/undefined/empty password as with no password', async () => {
    const secret = generateFileSecret();
    const noArg = await deriveKeys(secret);
    const nullArg = await deriveKeys(secret, null);
    const emptyArg = await deriveKeys(secret, '');

    const metadata = { name: 'c.txt', type: 'text/plain', size: 3 };
    const sealed = await encryptMetadata(noArg.metadata, metadata);

    expect(await decryptMetadata(nullArg.metadata, sealed)).toEqual(metadata);
    expect(await decryptMetadata(emptyArg.metadata, sealed)).toEqual(metadata);
  });
});
