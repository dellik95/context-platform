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

  open(data: ConfirmDialogData): Promise<boolean> {
    return new Promise<boolean>((resolve) => {
      this.pending.set({ data, resolve });
    });
  }

  respond(confirmed: boolean): void {
    const current = this.pending();
    if (!current) {
      return;
    }
    this.pending.set(null);
    current.resolve(confirmed);
  }
}
