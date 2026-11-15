import { HttpErrorResponse } from '@angular/common/http';
import { DecryptionError, FormatError } from '../crypto/file-crypto';

const UNITS = ['bytes', 'KB', 'MB', 'GB'] as const;

/** Formats a byte count for display, for example "1.5 MB". */
export function formatBytes(bytes: number): string {
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < UNITS.length - 1) {
    value /= 1024;
    unit++;
  }
  return unit === 0 ? `${value} ${UNITS[unit]}` : `${value.toFixed(value < 10 ? 1 : 0)} ${UNITS[unit]}`;
}

/** Turns anything thrown by the transfer flows into a sentence a person can act on. */
export function describeError(error: unknown): string {
  if (error instanceof DecryptionError || error instanceof FormatError) {
    return error.message;
  }

  if (error instanceof HttpErrorResponse) {
    switch (error.status) {
      case 0:
        return 'The server could not be reached. Check your connection and try again.';
      case 404:
        return 'This transfer does not exist. Check that the link is complete.';
      case 410:
        return 'This transfer is no longer available. It expired, was revoked, or reached its download limit.';
      case 413:
        return 'This file is larger than the server accepts.';
      case 429:
        return 'Too many transfers were created from this network. Wait a few minutes and try again.';
      default:
        return 'The server refused the request. Try again in a moment.';
    }
  }

  return 'Something went wrong. Try again.';
}
