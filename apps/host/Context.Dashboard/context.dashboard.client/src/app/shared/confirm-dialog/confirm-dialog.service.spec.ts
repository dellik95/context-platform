import { describe, expect, it } from 'vitest';
import { ConfirmDialogService } from './confirm-dialog.service';

describe('ConfirmDialogService', () => {
  it('resolves the pending confirmation promise when responded', async () => {
    const service = new ConfirmDialogService();

    const first = service.open({ title: 'Delete item', message: 'Are you sure?' });
    expect(service.pending()?.data.title).toBe('Delete item');

    service.respond(true);
    expect(service.pending()).toBeNull();
    await expect(first).resolves.toBe(true);

    const second = service.open({ title: 'Cancel action', message: 'Abort?' });
    service.respond(false);
    await expect(second).resolves.toBe(false);
  });

  it('queues overlapping confirmations FIFO without orphaning the earlier promise', async () => {
    const service = new ConfirmDialogService();

    const first = service.open({ title: 'First', message: 'Confirm first?' });
    const second = service.open({ title: 'Second', message: 'Confirm second?' });

    expect(service.pending()?.data.title).toBe('First');

    service.respond(true);
    await expect(first).resolves.toBe(true);
    expect(service.pending()?.data.title).toBe('Second');

    service.respond(false);
    await expect(second).resolves.toBe(false);
    expect(service.pending()).toBeNull();
  });
});
