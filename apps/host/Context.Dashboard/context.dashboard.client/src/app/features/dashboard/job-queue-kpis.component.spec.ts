import { TestBed } from '@angular/core/testing';
import { SystemStatusDto } from '../../core/models';
import { JobQueueKpisComponent } from './job-queue-kpis.component';

describe('JobQueueKpisComponent', () => {
  const sampleStatus: SystemStatusDto = {
    databaseReachable: true,
    jobDispatcherAlive: true,
    mcpAlive: true,
    embeddingProvider: 'fake',
    llmProvider: 'fake',
    jobsPending: 3,
    jobsRunning: 1,
    jobsFailed: 2,
    jobsDone: 14,
    uptimeSeconds: 120,
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [JobQueueKpisComponent],
    }).compileComponents();
  });

  it('renders job counters and emits cleanup/force actions when clicked', () => {
    const fixture = TestBed.createComponent(JobQueueKpisComponent);
    fixture.componentRef.setInput('status', sampleStatus);
    fixture.detectChanges();

    let pendingClicks = 0;
    let forceClicks = 0;
    let completedClicks = 0;
    fixture.componentInstance.cleanupPending.subscribe(() => pendingClicks++);
    fixture.componentInstance.forceAllRunning.subscribe(() => forceClicks++);
    fixture.componentInstance.cleanupCompleted.subscribe(() => completedClicks++);

    const host = fixture.nativeElement as HTMLElement;
    expect(host.textContent).toContain('3');
    expect(host.textContent).toContain('14');

    const pendingBtn = host.querySelector('button[aria-label="Delete pending non-user-data jobs"]') as HTMLButtonElement;
    const forceBtn = host.querySelector('button[aria-label="Force restart all running jobs"]') as HTMLButtonElement;
    const doneBtn = host.querySelector('button[aria-label="Remove finished jobs from queue"]') as HTMLButtonElement;

    pendingBtn.click();
    forceBtn.click();
    doneBtn.click();

    expect(pendingClicks).toBe(1);
    expect(forceClicks).toBe(1);
    expect(completedClicks).toBe(1);
  });
});
