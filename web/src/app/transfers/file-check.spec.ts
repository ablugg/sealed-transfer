import { describe, expect, it } from 'vitest';
import { checkFile, detectType } from './file-check';

function fakeFile(name: string, bytes: number[] = []): File {
  return new File([new Uint8Array(bytes)], name);
}

describe('detectType', () => {
  it('detects Windows executables by MZ header', async () => {
    const file = fakeFile('test.exe', [0x4d, 0x5a, 0x90, 0x00]);
    const result = await detectType(file);
    expect(result).not.toBeNull();
    expect(result!.mime).toBe('application/x-msdownload');
  });

  it('detects ELF binaries', async () => {
    const file = fakeFile('program', [0x7f, 0x45, 0x4c, 0x46, 0x02]);
    const result = await detectType(file);
    expect(result).not.toBeNull();
    expect(result!.mime).toBe('application/x-elf');
  });

  it('returns null for a plain text file', async () => {
    const file = fakeFile('readme.txt', [0x48, 0x65, 0x6c, 0x6c, 0x6f]);
    expect(await detectType(file)).toBeNull();
  });

  it('returns null for an empty file', async () => {
    expect(await detectType(fakeFile('empty.txt'))).toBeNull();
  });
});

describe('checkFile', () => {
  it('blocks .exe by extension', async () => {
    const result = await checkFile(fakeFile('malware.exe', [0x00]));
    expect(result.allowed).toBe(false);
    expect(result.reason).toContain('.exe');
  });

  it('blocks .bat by extension', async () => {
    const result = await checkFile(fakeFile('script.bat', [0x40, 0x65]));
    expect(result.allowed).toBe(false);
    expect(result.reason).toContain('.bat');
  });

  it('blocks .ps1 by extension', async () => {
    const result = await checkFile(fakeFile('script.ps1'));
    expect(result.allowed).toBe(false);
  });

  it('blocks .sh by extension', async () => {
    const result = await checkFile(fakeFile('deploy.sh'));
    expect(result.allowed).toBe(false);
  });

  it('blocks a file with MZ magic bytes even with a benign extension', async () => {
    const result = await checkFile(fakeFile('totally-safe.dat', [0x4d, 0x5a, 0x90, 0x00]));
    expect(result.allowed).toBe(false);
    expect(result.reason).toContain('executable');
  });

  it('allows a PNG file', async () => {
    const png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    const result = await checkFile(fakeFile('photo.png', png));
    expect(result.allowed).toBe(true);
  });

  it('allows a PDF file', async () => {
    const pdf = [0x25, 0x50, 0x44, 0x46, 0x2d];
    const result = await checkFile(fakeFile('document.pdf', pdf));
    expect(result.allowed).toBe(true);
  });

  it('allows a regular text file', async () => {
    const result = await checkFile(fakeFile('notes.txt', [0x48, 0x65, 0x6c, 0x6c, 0x6f]));
    expect(result.allowed).toBe(true);
  });

  it('allows a zip file', async () => {
    const zip = [0x50, 0x4b, 0x03, 0x04];
    const result = await checkFile(fakeFile('archive.zip', zip));
    expect(result.allowed).toBe(true);
  });

  it('is case-insensitive on extensions', async () => {
    const result = await checkFile(fakeFile('MALWARE.EXE'));
    expect(result.allowed).toBe(false);
  });
});
