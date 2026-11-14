import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ToastService } from './toast.service';

describe('ToastService', () => {
  let service: ToastService;

  beforeEach(() => {
    vi.useFakeTimers();
    service = new ToastService();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('adds typed toasts and auto-dismisses after duration', () => {
    const id = service.success('Saved cleanly.', 2000);
    service.info('Background sync running.', 0);

    expect(service.items()).toHaveLength(2);
    expect(service.items()[0]).toEqual({ id, message: 'Saved cleanly.', tone: 'success' });

    vi.advanceTimersByTime(2000);

    expect(service.items()).toHaveLength(1);
    expect(service.items()[0].tone).toBe('info');
  });

  it('dismisses manually and clears pending timer', () => {
    const id = service.error('Something failed.', 5000);
    service.dismiss(id);

    expect(service.items()).toEqual([]);
  });
});
