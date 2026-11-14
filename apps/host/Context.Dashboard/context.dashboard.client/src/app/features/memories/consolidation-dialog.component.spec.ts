import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Signal, signal } from '@angular/core';
import { Subject, of } from 'rxjs';
import { ApiService } from '../../core/api.service';
import {
  ConsolidationReportStatus,
  ReconsolidationReportDto,
  ReconsolidationReportSummaryDto,
} from '../../core/models';
import { SignalrService } from '../../core/signalr.service';
import { ConfirmDialogService } from '../../shared/confirm-dialog/confirm-dialog.service';
import { ToastService } from '../../shared/ui/toast.service';
import { ProjectContextService } from '../projects/project-context.service';
import { ConsolidationDialogComponent } from './consolidation-dialog.component';

describe('ConsolidationDialogComponent', () => {
  let fixture: ComponentFixture<ConsolidationDialogComponent>;
  let reports: ReturnType<typeof signal<ReconsolidationReportSummaryDto[]>>;
  let reloadReports: ReturnType<typeof vi.fn>;
  let deleteReport: ReturnType<typeof vi.fn>;
  let cleanupReports: ReturnType<typeof vi.fn>;
  let toastMock: {
    success: ReturnType<typeof vi.fn>;
    error: ReturnType<typeof vi.fn>;
    warning: ReturnType<typeof vi.fn>;
    show: ReturnType<typeof vi.fn>;
  };
  let confirmOpen: ReturnType<typeof vi.fn>;

  function summary(id: string, status: ConsolidationReportStatus): ReconsolidationReportSummaryDto {
    return { id, createdAt: '2026-01-01T00:00:00Z', status, appliedAt: null, stats: null };
  }

  function detail(id: string, status: ConsolidationReportStatus): ReconsolidationReportDto {
    return {
      id,
      createdAt: '2026-01-01T00:00:00Z',
      status,
      appliedAt: null,
      report: {
        groups: [],
        retiredChangeLogs: [],
        untouched: 0,
        stats: {
          liveKnowledge: 7,
          reembedded: 0,
          groups: 0,
          ambiguousGroups: 0,
          canonicals: 0,
          sourcesMerged: 0,
          contradictions: 0,
          retiredChangeLogs: 0,
          usedModel: true,
        },
        outcome: null,
      },
    };
  }

  async function create(initial: ReconsolidationReportSummaryDto[]): Promise<void> {
    reports = signal(initial);
    reloadReports = vi.fn();
    deleteReport = vi.fn(() => of(undefined));
    cleanupReports = vi.fn(() => of({ removed: 3 }));
    toastMock = {
      success: vi.fn(),
      error: vi.fn(),
      warning: vi.fn(),
      show: vi.fn(),
    };
    confirmOpen = vi.fn().mockResolvedValue(true);

    const apiServiceStub: Partial<ApiService> = {
      reconsolidationReportsResource: () =>
        ({
          isLoading: () => false,
          hasValue: () => true,
          value: reports,
          error: () => undefined,
          reload: reloadReports,
        }) as unknown as ReturnType<ApiService['reconsolidationReportsResource']>,
      reconsolidationReportResource: (reportId: Signal<string | null>) =>
        ({
          isLoading: () => false,
          hasValue: () => reportId() !== null,
          value: () => {
            const id = reportId();
            return id ? detail(id, reports().find((r) => r.id === id)!.status) : undefined;
          },
          error: () => undefined,
          reload: vi.fn(),
        }) as unknown as ReturnType<ApiService['reconsolidationReportResource']>,
      deleteReconsolidationReport: deleteReport as unknown as ApiService['deleteReconsolidationReport'],
      cleanupReconsolidationReports: cleanupReports as unknown as ApiService['cleanupReconsolidationReports'],
    };

    await TestBed.configureTestingModule({
      imports: [ConsolidationDialogComponent],
      providers: [
        { provide: ApiService, useValue: apiServiceStub },
        { provide: SignalrService, useValue: { jobCountsChanged$: new Subject<void>() } },
        { provide: ProjectContextService, useValue: { projectId: () => 'test-project' } },
        { provide: ToastService, useValue: toastMock },
        { provide: ConfirmDialogService, useValue: { open: confirmOpen } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(ConsolidationDialogComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  const root = () => fixture.nativeElement as HTMLElement;
  const items = () => [...root().querySelectorAll<HTMLElement>('.report-item')];
  const viewButton = (index: number) => items()[index].querySelector<HTMLButtonElement>('.report-row button')!;
  const deleteButton = (index: number) =>
    items()[index].querySelector<HTMLButtonElement>('button[data-action="delete-report"]')!;
  const clearButton = () =>
    [...root().querySelectorAll<HTMLButtonElement>('.toolbar button')].find((b) =>
      b.textContent?.includes('Clear finished'),
    )!;

  async function click(button: HTMLButtonElement): Promise<void> {
    button.click();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  beforeEach(() => {
    Element.prototype.scrollIntoView = vi.fn();
  });

  afterEach(() => vi.restoreAllMocks());

  it('expands the details directly under the clicked row, one report at a time', async () => {
    await create([summary('a', 'Pending'), summary('b', 'Applied'), summary('c', 'Failed')]);
    const hasDetail = () => items().map((item) => item.querySelector('.report-detail') !== null);

    expect(hasDetail()).toEqual([false, false, false]);

    await click(viewButton(1));
    expect(hasDetail()).toEqual([false, true, false]);

    await click(viewButton(0));
    expect(hasDetail()).toEqual([true, false, false]);

    await click(viewButton(0));
    expect(hasDetail()).toEqual([false, false, false]);
  });

  it('deletes a report, collapses it when expanded and refreshes the list', async () => {
    await create([summary('a', 'Pending'), summary('b', 'Applied')]);

    await click(viewButton(1));
    await click(deleteButton(1));

    expect(confirmOpen).toHaveBeenCalled();
    expect(deleteReport).toHaveBeenCalledWith('b');
    expect(root().querySelector('.report-detail')).toBeNull();
    expect(reloadReports).toHaveBeenCalled();
    expect(toastMock.success).toHaveBeenCalledWith('Report deleted.', 3000);
  });

  it('keeps the expanded report when another one is deleted', async () => {
    await create([summary('a', 'Pending'), summary('b', 'Applied')]);

    await click(viewButton(0));
    await click(deleteButton(1));

    expect(items()[0].querySelector('.report-detail')).not.toBeNull();
  });

  it('cannot delete a report that is applying', async () => {
    await create([summary('a', 'Applying'), summary('b', 'Applied')]);

    expect(deleteButton(0).disabled).toBe(true);
    expect(deleteButton(1).disabled).toBe(false);
  });

  it('disables Clear finished until a finished report exists', async () => {
    await create([summary('a', 'Pending'), summary('b', 'Applying')]);

    expect(clearButton().disabled).toBe(true);

    reports.set([summary('a', 'Pending'), summary('b', 'Discarded')]);
    fixture.detectChanges();

    expect(clearButton().disabled).toBe(false);
  });

  it('clears the finished reports and reports how many were removed', async () => {
    await create([summary('a', 'Pending'), summary('b', 'Applied'), summary('c', 'Failed')]);

    await click(viewButton(1));
    await click(clearButton());

    expect(confirmOpen).toHaveBeenCalled();
    expect(cleanupReports).toHaveBeenCalledWith();
    expect(root().querySelector('.report-detail')).toBeNull();
    expect(reloadReports).toHaveBeenCalled();
    expect(toastMock.success).toHaveBeenCalledWith('Removed 3 reports.', 3000);
  });
});
