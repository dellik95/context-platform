import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Signal, signal } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Subject, of } from 'rxjs';
import { ApiService } from '../../core/api.service';
import {
  ConsolidationReportStatus,
  ReconsolidationReportDto,
  ReconsolidationReportSummaryDto,
} from '../../core/models';
import { SignalrService } from '../../core/signalr.service';
import { ProjectContextService } from '../projects/project-context.service';
import { ConsolidationDialogComponent } from './consolidation-dialog.component';

/**
 * Coverage: a report's details expand inline under its own row (one at a time), and deleting
 * reports collapses/refreshes the list. `ApiService` resources are stubbed with signals so the
 * test drives only the dialog's own state handling.
 */
describe('ConsolidationDialogComponent', () => {
  let fixture: ComponentFixture<ConsolidationDialogComponent>;
  let reports: ReturnType<typeof signal<ReconsolidationReportSummaryDto[]>>;
  let reloadReports: ReturnType<typeof vi.fn>;
  let deleteReport: ReturnType<typeof vi.fn>;
  let cleanupReports: ReturnType<typeof vi.fn>;
  let snackBarOpen: ReturnType<typeof vi.fn>;

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
    snackBarOpen = vi.fn();

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
        { provide: MatSnackBar, useValue: { open: snackBarOpen } },
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
  const deleteButton = (index: number) => items()[index].querySelector<HTMLButtonElement>('button[mat-icon-button]')!;
  const clearButton = () =>
    [...root().querySelectorAll<HTMLButtonElement>('.toolbar button')].find((b) => b.textContent?.includes('Clear finished'))!;

  function click(button: HTMLButtonElement): void {
    button.click();
    fixture.detectChanges();
  }

  beforeEach(() => {
    Element.prototype.scrollIntoView = vi.fn();
    vi.spyOn(window, 'confirm').mockReturnValue(true);
  });

  afterEach(() => vi.restoreAllMocks());

  it('expands the details directly under the clicked row, one report at a time', async () => {
    await create([summary('a', 'Pending'), summary('b', 'Applied'), summary('c', 'Failed')]);
    const hasDetail = () => items().map((item) => item.querySelector('.report-detail') !== null);

    expect(hasDetail()).toEqual([false, false, false]);

    click(viewButton(1));
    expect(hasDetail()).toEqual([false, true, false]);

    click(viewButton(0));
    expect(hasDetail()).toEqual([true, false, false]);

    click(viewButton(0));
    expect(hasDetail()).toEqual([false, false, false]);
  });

  it('deletes a report, collapses it when expanded and refreshes the list', async () => {
    await create([summary('a', 'Pending'), summary('b', 'Applied')]);

    click(viewButton(1));
    click(deleteButton(1));

    expect(deleteReport).toHaveBeenCalledWith('b');
    expect(root().querySelector('.report-detail')).toBeNull();
    expect(reloadReports).toHaveBeenCalled();
    expect(snackBarOpen).toHaveBeenCalledWith('Report deleted.', 'Dismiss', expect.anything());
  });

  it('keeps the expanded report when another one is deleted', async () => {
    await create([summary('a', 'Pending'), summary('b', 'Applied')]);

    click(viewButton(0));
    click(deleteButton(1));

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

    click(viewButton(1));
    click(clearButton());

    expect(cleanupReports).toHaveBeenCalledWith();
    expect(root().querySelector('.report-detail')).toBeNull();
    expect(reloadReports).toHaveBeenCalled();
    expect(snackBarOpen).toHaveBeenCalledWith('Removed 3 reports.', 'Dismiss', expect.anything());
  });
});
