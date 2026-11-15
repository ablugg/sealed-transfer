import { DOCUMENT, DatePipe, PercentPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { describeError, formatBytes } from '../../transfers/format';
import { OpenedTransfer, PasswordRequiredError, TransferClient } from '../../transfers/transfer-client';

type Stage = 'opening' | 'password' | 'ready' | 'downloading' | 'saved' | 'failed';

@Component({
  selector: 'app-receive',
  imports: [DatePipe, PercentPipe, RouterLink, FormsModule],
  templateUrl: './receive.html',
  styleUrl: './receive.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Receive implements OnInit {
  private readonly client = inject(TransferClient);
  private readonly document = inject(DOCUMENT);

  /** Bound from the :id route parameter. */
  readonly id = input.required<string>();

  protected readonly stage = signal<Stage>('opening');
  protected readonly opened = signal<OpenedTransfer | null>(null);
  protected readonly progress = signal(0);
  protected readonly error = signal<string | null>(null);
  protected readonly password = signal('');
  protected readonly unlocking = signal(false);

  private secret = '';

  protected readonly fileSize = computed(() => formatBytes(this.opened()?.metadata.size ?? 0));

  async ngOnInit(): Promise<void> {
    const location = this.document.location;
    this.secret = location.hash.slice(1);

    // Take the key out of the address bar and the history entry straight away.
    this.document.defaultView?.history.replaceState(null, '', location.pathname + location.search);

    if (!this.secret) {
      this.fail('This link has no key. Ask the sender to copy the whole link, including everything after the #.');
      return;
    }

    try {
      this.opened.set(await this.client.open(this.id(), this.secret));
      this.stage.set('ready');
    } catch (error) {
      if (error instanceof PasswordRequiredError) {
        this.stage.set('password');
      } else {
        this.fail(describeError(error));
      }
    }
  }

  protected async unlock(): Promise<void> {
    const pw = this.password();
    if (!pw || this.unlocking()) return;

    this.unlocking.set(true);
    this.error.set(null);

    try {
      this.opened.set(await this.client.open(this.id(), this.secret, pw));
      this.stage.set('ready');
    } catch (error) {
      this.error.set(describeError(error));
    } finally {
      this.unlocking.set(false);
    }
  }

  protected async download(): Promise<void> {
    const opened = this.opened();
    if (!opened || this.stage() === 'downloading') {
      return;
    }

    this.stage.set('downloading');
    this.progress.set(0);
    this.error.set(null);

    try {
      const blob = await this.client.download(opened, (fraction) => this.progress.set(fraction));
      this.save(blob, opened.metadata.name);
      this.stage.set('saved');
    } catch (error) {
      this.fail(describeError(error));
    }
  }

  private save(blob: Blob, name: string): void {
    const url = URL.createObjectURL(blob);
    const anchor = this.document.createElement('a');
    anchor.href = url;
    anchor.download = name;
    anchor.click();
    setTimeout(() => URL.revokeObjectURL(url), 60_000);
  }

  private fail(message: string): void {
    this.error.set(message);
    this.stage.set('failed');
  }
}
