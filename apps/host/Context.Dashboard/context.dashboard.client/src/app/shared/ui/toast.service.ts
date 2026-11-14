import { Injectable, signal } from '@angular/core';

export type ToastTone = 'info' | 'success' | 'warning' | 'error';

export interface ToastItem {
  id: number;
  message: string;
  tone: ToastTone;
}

@Injectable({ providedIn: 'root' })
export class ToastService {
  private nextId = 1;
  private readonly timers = new Map<number, ReturnType<typeof setTimeout>>();

  readonly items = signal<ToastItem[]>([]);

  show(message: string, tone: ToastTone = 'info', durationMs = 5000): number {
    const id = this.nextId++;
    this.items.update((list) => [...list, { id, message, tone }]);
    if (durationMs > 0) {
      this.timers.set(
        id,
        setTimeout(() => this.dismiss(id), durationMs),
      );
    }
    return id;
  }

  info(message: string, durationMs = 4500): number {
    return this.show(message, 'info', durationMs);
  }

  success(message: string, durationMs = 4500): number {
    return this.show(message, 'success', durationMs);
  }

  error(message: string, durationMs = 6000): number {
    return this.show(message, 'error', durationMs);
  }

  warning(message: string, durationMs = 5500): number {
    return this.show(message, 'warning', durationMs);
  }

  dismiss(id: number): void {
    const timer = this.timers.get(id);
    if (timer) {
      clearTimeout(timer);
      this.timers.delete(id);
    }
    this.items.update((list) => list.filter((item) => item.id !== id));
  }
}
