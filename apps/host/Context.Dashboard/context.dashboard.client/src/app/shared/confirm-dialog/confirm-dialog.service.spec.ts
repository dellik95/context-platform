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
});
