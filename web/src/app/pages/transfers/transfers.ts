import { DOCUMENT, DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { describeError, formatBytes } from '../../transfers/format';
import { TransferApi } from '../../transfers/transfer-api';
import { StoredTransfer, TransferStore } from '../../transfers/transfer-store';

@Component({
  selector: 'app-transfers',
  imports: [DatePipe, RouterLink],
  templateUrl: './transfers.html',
  styleUrl: './transfers.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Transfers {
  private readonly store = inject(TransferStore);
  private readonly api = inject(TransferApi);
  private readonly document = inject(DOCUMENT);

  protected readonly items = signal<StoredTransfer[]>(this.store.list());
  protected readonly revokingId = signal<string | null>(null);
  protected readonly error = signal<string | null>(null);

  protected readonly hasItems = computed(() => this.items().length > 0);

  protected isExpired(item: StoredTransfer): boolean {
    return new Date(item.expiresAt) <= new Date();
  }

  protected status(item: StoredTransfer): string {
    if (item.revoked) return 'Revoked';
    if (this.isExpired(item)) return 'Expired';
    return 'Active';
  }

  protected fileSize(item: StoredTransfer): string {
    return formatBytes(item.fileSize);
  }

  protected link(item: StoredTransfer): string {
    return `${this.document.location.origin}/t/${item.id}#${item.secret}`;
  }

  protected async copyLink(item: StoredTransfer): Promise<void> {
    await this.document.defaultView?.navigator.clipboard.writeText(this.link(item));
  }

  protected async revoke(item: StoredTransfer): Promise<void> {
    if (this.revokingId()) return;

    this.revokingId.set(item.id);
    this.error.set(null);

    try {
      await firstValueFrom(this.api.revoke(item.id, item.ownerToken));
      this.store.markRevoked(item.id);
      this.items.set(this.store.list());
    } catch (err) {
      this.error.set(describeError(err));
    } finally {
      this.revokingId.set(null);
    }
  }

  protected removeFromList(item: StoredTransfer): void {
    this.store.remove(item.id);
    this.items.set(this.store.list());
  }
}
