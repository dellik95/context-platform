import { Injectable, signal } from '@angular/core';

export interface ConfirmDialogData {
  title: string;
  message: string;
  confirmLabel?: string;
  cancelLabel?: string;
  confirmTone?: 'primary' | 'danger' | 'warning';
}

interface PendingConfirm {
  data: ConfirmDialogData;
  resolve: (confirmed: boolean) => void;
}

@Injectable({ providedIn: 'root' })
export class ConfirmDialogService {
  readonly pending = signal<PendingConfirm | null>(null);
  private readonly queue: PendingConfirm[] = [];

  open(data: ConfirmDialogData): Promise<boolean> {
    return new Promise<boolean>((resolve) => {
      const request: PendingConfirm = { data, resolve };
      if (!this.pending()) {
        this.pending.set(request);
      } else {
        this.queue.push(request);
      }
    });
  }

  respond(confirmed: boolean): void {
    const current = this.pending();
    if (!current) {
      return;
    }
    this.pending.set(this.queue.shift() ?? null);
    current.resolve(confirmed);
  }
}
