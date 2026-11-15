import { DOCUMENT } from '@angular/common';
import { Injectable, inject } from '@angular/core';

/** A transfer record kept in localStorage so the sender can manage it later. */
export interface StoredTransfer {
  readonly id: string;
  readonly fileName: string;
  readonly fileSize: number;
  readonly ownerToken: string;
  readonly secret: string;
  readonly expiresAt: string;
  readonly createdAt: string;
  /** Set when the sender revokes the transfer from the UI. */
  revoked?: boolean;
}

const STORAGE_KEY = 'sealed-transfer:my-transfers';

/** Persists sent transfers in localStorage so the sender can list and revoke them. */
@Injectable({ providedIn: 'root' })
export class TransferStore {
  private readonly storage = inject(DOCUMENT).defaultView?.localStorage;

  list(): StoredTransfer[] {
    const raw = this.storage?.getItem(STORAGE_KEY);
    if (!raw) return [];
    try {
      return JSON.parse(raw) as StoredTransfer[];
    } catch {
      return [];
    }
  }

  add(transfer: StoredTransfer): void {
    const items = this.list();
    items.unshift(transfer);
    this.save(items);
  }

  markRevoked(id: string): void {
    const items = this.list();
    const item = items.find((t) => t.id === id);
    if (item) {
      item.revoked = true;
      this.save(items);
    }
  }

  remove(id: string): void {
    const items = this.list().filter((t) => t.id !== id);
    this.save(items);
  }

  find(id: string): StoredTransfer | undefined {
    return this.list().find((t) => t.id === id);
  }

  private save(items: StoredTransfer[]): void {
    this.storage?.setItem(STORAGE_KEY, JSON.stringify(items));
  }
}
