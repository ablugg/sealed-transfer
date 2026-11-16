/**
 * Client-side file validation before encryption.
 *
 * Reads the first bytes of a file and compares them against known magic byte
 * signatures. Dangerous types (executables, scripts, archives that auto-run)
 * are blocked outright; everything else is allowed through.
 *
 * This is a best-effort defence-in-depth layer. The server never sees plaintext,
 * so it cannot scan files — catching obvious mistakes here is all we can do.
 */

/** A file type detected by its magic bytes. */
export interface DetectedType {
  readonly mime: string;
  readonly description: string;
}

interface MagicSignature {
  readonly offset: number;
  readonly bytes: number[];
  readonly mime: string;
  readonly description: string;
}

const SIGNATURES: MagicSignature[] = [
  // Windows executables
  { offset: 0, bytes: [0x4d, 0x5a], mime: 'application/x-msdownload', description: 'Windows executable (.exe/.dll)' },
  // ELF (Linux executables, shared objects)
  { offset: 0, bytes: [0x7f, 0x45, 0x4c, 0x46], mime: 'application/x-elf', description: 'Linux executable (ELF)' },
  // Mach-O (macOS executables)
  { offset: 0, bytes: [0xfe, 0xed, 0xfa, 0xce], mime: 'application/x-mach-binary', description: 'macOS executable (Mach-O)' },
  { offset: 0, bytes: [0xfe, 0xed, 0xfa, 0xcf], mime: 'application/x-mach-binary', description: 'macOS executable (Mach-O 64-bit)' },
  { offset: 0, bytes: [0xce, 0xfa, 0xed, 0xfe], mime: 'application/x-mach-binary', description: 'macOS executable (Mach-O, reversed)' },
  { offset: 0, bytes: [0xcf, 0xfa, 0xed, 0xfe], mime: 'application/x-mach-binary', description: 'macOS executable (Mach-O 64-bit, reversed)' },
  // Java class files
  { offset: 0, bytes: [0xca, 0xfe, 0xba, 0xbe], mime: 'application/java-vm', description: 'Java class file' },
  // Windows shortcut (.lnk)
  { offset: 0, bytes: [0x4c, 0x00, 0x00, 0x00], mime: 'application/x-ms-shortcut', description: 'Windows shortcut (.lnk)' },
  // Microsoft Installer (.msi) — OLE Compound File with MSI content
  { offset: 0, bytes: [0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1], mime: 'application/x-ole-storage', description: 'OLE compound file (.msi/.doc)' },
];

/** MIME types that are blocked from upload. */
const BLOCKED_MIMES = new Set([
  'application/x-msdownload',
  'application/x-elf',
  'application/x-mach-binary',
  'application/java-vm',
  'application/x-ms-shortcut',
]);

/** File extensions that are blocked regardless of magic bytes (script files have no reliable signature). */
const BLOCKED_EXTENSIONS = new Set([
  '.exe', '.dll', '.scr', '.com', '.pif',  // Windows PE
  '.bat', '.cmd', '.ps1', '.vbs', '.vbe',  // Windows scripts
  '.wsf', '.wsh', '.msi', '.msp',          // Windows installer/scripts
  '.app', '.command',                        // macOS
  '.sh', '.csh', '.bash',                   // Unix shell
  '.jar', '.class',                          // Java
  '.lnk', '.inf', '.reg',                   // Windows system
]);

/** How many bytes we need to read for all signatures. */
const HEADER_SIZE = 16;

/** Detects the file type from magic bytes. Returns null if no known signature matches. */
export async function detectType(file: File): Promise<DetectedType | null> {
  if (file.size === 0) return null;

  const slice = file.slice(0, HEADER_SIZE);
  const header = new Uint8Array(await slice.arrayBuffer());

  for (const sig of SIGNATURES) {
    if (header.byteLength < sig.offset + sig.bytes.length) continue;
    if (sig.bytes.every((b, i) => header[sig.offset + i] === b)) {
      return { mime: sig.mime, description: sig.description };
    }
  }
  return null;
}

/** Returns the file extension in lowercase, including the dot. */
function extensionOf(name: string): string {
  const dot = name.lastIndexOf('.');
  return dot === -1 ? '' : name.slice(dot).toLowerCase();
}

export interface FileCheckResult {
  readonly allowed: boolean;
  readonly reason?: string;
}

/**
 * Validates a file before encryption. Checks both magic bytes and the file extension.
 * Returns `{ allowed: false, reason }` if the file should be blocked.
 */
export async function checkFile(file: File): Promise<FileCheckResult> {
  const ext = extensionOf(file.name);
  if (BLOCKED_EXTENSIONS.has(ext)) {
    return { allowed: false, reason: `${ext} files are not allowed. Executable and script files cannot be shared.` };
  }

  const detected = await detectType(file);
  if (detected && BLOCKED_MIMES.has(detected.mime)) {
    return { allowed: false, reason: `${detected.description} files are not allowed.` };
  }

  return { allowed: true };
}
