import { DOCUMENT, DatePipe, PercentPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormField, disabled, form, max, min } from '@angular/forms/signals';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { describeError, formatBytes } from '../../transfers/format';
import { checkFile } from '../../transfers/file-check';
import { TransferApi } from '../../transfers/transfer-api';
import { MAX_FILE_BYTES, SentTransfer, TransferClient } from '../../transfers/transfer-client';
import { TransferStore } from '../../transfers/transfer-store';

type Stage = 'idle' | 'sending' | 'done' | 'failed';

interface SendOptionsModel {
  expiresInHours: string;
  limitDownloads: boolean;
  maxDownloads: number;
  usePassword: boolean;
  password: string;
}

@Component({
  selector: 'app-send',
  imports: [FormField, DatePipe, PercentPipe, RouterLink],
  templateUrl: './send.html',
  styleUrl: './send.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Send {
  private readonly client = inject(TransferClient);
  private readonly api = inject(TransferApi);
  private readonly store = inject(TransferStore);
  private readonly document = inject(DOCUMENT);

  protected readonly file = signal<File | null>(null);
  protected readonly stage = signal<Stage>('idle');
  protected readonly progress = signal(0);
  protected readonly error = signal<string | null>(null);
  protected readonly sent = signal<SentTransfer | null>(null);
  protected readonly copied = signal(false);
  protected readonly revoking = signal(false);
  protected readonly revoked = signal(false);

  protected readonly options = signal<SendOptionsModel>({
    expiresInHours: '24',
    limitDownloads: false,
    maxDownloads: 1,
    usePassword: false,
    password: '',
  });

  protected readonly optionsForm = form(this.options, (path) => {
    disabled(path.maxDownloads, { when: ({ valueOf }) => !valueOf(path.limitDownloads) });
    min(path.maxDownloads, 1, { message: 'Allow at least 1 download.' });
    max(path.maxDownloads, 100, { message: 'Allow at most 100 downloads.' });
    disabled(path.password, { when: ({ valueOf }) => !valueOf(path.usePassword) });
  });

  protected readonly blocked = signal<string | null>(null);
  protected readonly fileSize = computed(() => formatBytes(this.file()?.size ?? 0));
  protected readonly tooLarge = computed(() => (this.file()?.size ?? 0) > MAX_FILE_BYTES);
  protected readonly canSend = computed(
    () => this.file() !== null && !this.tooLarge() && !this.blocked() && this.stage() !== 'sending' && this.optionsForm().valid(),
  );

  protected readonly link = computed(() => {
    const sent = this.sent();
    return sent ? `${this.document.location.origin}/t/${sent.id}#${sent.secret}` : '';
  });

  protected async onFileSelected(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.item(0) ?? null;
    this.file.set(file);
    this.error.set(null);
    this.blocked.set(null);

    if (file) {
      const result = await checkFile(file);
      if (!result.allowed) {
        this.blocked.set(result.reason ?? 'This file type is not allowed.');
      }
    }
  }

  protected async send(): Promise<void> {
    const file = this.file();
    if (!file || !this.canSend()) {
      return;
    }

    const { expiresInHours, limitDownloads, maxDownloads, usePassword, password } = this.options();
    this.stage.set('sending');
    this.progress.set(0);
    this.error.set(null);

    try {
      const sent = await this.client.send(
        file,
        {
          expiresInHours: Number(expiresInHours),
          maxDownloads: limitDownloads ? maxDownloads : null,
          password: usePassword && password ? password : null,
        },
        (fraction) => this.progress.set(fraction),
      );
      this.sent.set(sent);
      this.store.add({
        id: sent.id,
        fileName: file.name,
        fileSize: file.size,
        ownerToken: sent.ownerToken,
        secret: sent.secret,
        expiresAt: sent.expiresAt.toISOString(),
        createdAt: new Date().toISOString(),
      });
      this.stage.set('done');
    } catch (error) {
      this.error.set(describeError(error));
      this.stage.set('failed');
    }
  }

  protected async copyLink(): Promise<void> {
    await this.document.defaultView?.navigator.clipboard.writeText(this.link());
    this.copied.set(true);
  }

  protected async revoke(): Promise<void> {
    const sent = this.sent();
    if (!sent || this.revoking()) return;

    this.revoking.set(true);
    try {
      await firstValueFrom(this.api.revoke(sent.id, sent.ownerToken));
      this.store.markRevoked(sent.id);
      this.revoked.set(true);
    } catch (error) {
      this.error.set(describeError(error));
    } finally {
      this.revoking.set(false);
    }
  }

  protected reset(): void {
    this.file.set(null);
    this.sent.set(null);
    this.copied.set(false);
    this.error.set(null);
    this.progress.set(0);
    this.stage.set('idle');
    this.revoked.set(false);
    this.revoking.set(false);
    this.blocked.set(null);
  }
}
