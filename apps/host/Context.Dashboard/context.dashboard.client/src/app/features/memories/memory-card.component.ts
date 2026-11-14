import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { Memory, MemoryDetail } from '../../core/models';
import { BadgeComponent, BadgeTone } from '../../shared/ui/badge.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { ProgressBarComponent } from '../../shared/ui/progress-bar.component';
import { SpinnerComponent } from '../../shared/ui/spinner.component';

export type MemoryDetailState = MemoryDetail | 'loading' | 'error';
type MemoryTab = 'facts' | 'narrative' | 'details' | 'evidence';

function parseStringArray(json: string | null): string[] | null {
  if (!json) {
    return null;
  }
  try {
    const parsed = JSON.parse(json);
    return Array.isArray(parsed) && parsed.length > 0 ? (parsed as string[]) : null;
  } catch {
    return null;
  }
}

@Component({
  selector: 'app-memory-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule, BadgeComponent, IconComponent, ProgressBarComponent, SpinnerComponent],
  template: `
    <article
      class="rounded-xl border border-slate-200 bg-white p-4 shadow-xs transition-opacity"
      [class.opacity-70]="isSupersededOrObsolete()"
    >
      <div class="flex flex-wrap items-start justify-between gap-3">
        <div class="flex min-w-0 flex-1 flex-wrap items-center gap-2">
          <app-badge [tone]="typeTone()">{{ memory().type }}</app-badge>
          <h2 class="min-w-0 flex-1 text-sm font-semibold text-slate-900 sm:text-base">
            {{ memory().title }}
          </h2>
          @if (memory().status !== 'Active') {
            <app-badge [tone]="memory().status === 'Contested' ? 'warning' : 'neutral'">
              {{ memory().status }}
            </app-badge>
          }
        </div>

        <button
          type="button"
          class="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-lg text-slate-400 transition-colors hover:bg-rose-50 hover:text-rose-600 disabled:cursor-not-allowed disabled:opacity-50"
          title="Delete this memory"
          aria-label="Delete this memory"
          [disabled]="deleting()"
          (click)="deleteRequested.emit(memory())"
        >
          @if (deleting()) {
            <app-spinner sizeClass="w-4 h-4 text-rose-600" />
          } @else {
            <app-icon name="delete" sizeClass="w-4 h-4" />
          }
        </button>
      </div>

      <!-- Tabs -->
      <div class="mt-3 flex flex-wrap gap-1 border-b border-slate-200" role="tablist">
        @if (facts()) {
          <button
            type="button"
            role="tab"
            [attr.aria-selected]="activeTab() === 'facts'"
            (click)="selectTab('facts')"
            [class]="tabClass('facts')"
          >
            Facts
          </button>
        }
        <button
          type="button"
          role="tab"
          [attr.aria-selected]="activeTab() === 'narrative'"
          (click)="selectTab('narrative')"
          [class]="tabClass('narrative')"
        >
          Narrative
        </button>
        @if (hasDetails()) {
          <button
            type="button"
            role="tab"
            [attr.aria-selected]="activeTab() === 'details'"
            (click)="selectTab('details')"
            [class]="tabClass('details')"
          >
            Details
          </button>
        }
        <button
          type="button"
          role="tab"
          [attr.aria-selected]="activeTab() === 'evidence'"
          (click)="selectTab('evidence')"
          [class]="tabClass('evidence')"
        >
          Evidence
        </button>
      </div>

      <!-- Tab content -->
      <div class="pt-3 text-sm text-slate-700">
        @switch (activeTab()) {
          @case ('facts') {
            @if (facts(); as memoryFacts) {
              <ul class="list-disc space-y-1 pl-5 text-sm leading-relaxed text-slate-700">
                @for (fact of memoryFacts; track $index) {
                  <li>{{ fact }}</li>
                }
              </ul>
            }
          }
          @case ('narrative') {
            <p class="whitespace-pre-line text-sm leading-relaxed text-slate-700">{{ memory().body }}</p>
          }
          @case ('details') {
            <div class="space-y-3">
              @if (concepts(); as memoryConcepts) {
                <div>
                  <div class="mb-1.5 text-xs font-medium tracking-wide text-slate-500 uppercase">Concepts</div>
                  <div class="flex flex-wrap gap-1.5">
                    @for (concept of memoryConcepts; track $index) {
                      <span class="rounded-md bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-700">
                        {{ concept }}
                      </span>
                    }
                  </div>
                </div>
              }
              @if (filesModified(); as modified) {
                <div>
                  <div class="mb-1 text-xs font-medium tracking-wide text-slate-500 uppercase">Files modified</div>
                  <ul class="space-y-0.5 font-mono text-xs text-slate-600">
                    @for (file of modified; track $index) {
                      <li class="truncate" [title]="file">{{ file }}</li>
                    }
                  </ul>
                </div>
              }
              @if (filesRead(); as read) {
                <div>
                  <div class="mb-1 text-xs font-medium tracking-wide text-slate-500 uppercase">Files read</div>
                  <ul class="space-y-0.5 font-mono text-xs text-slate-600">
                    @for (file of read; track $index) {
                      <li class="truncate" [title]="file">{{ file }}</li>
                    }
                  </ul>
                </div>
              }
            </div>
          }
          @case ('evidence') {
            @switch (detailState()) {
              @case ('loading') {
                <app-progress-bar height="sm" />
              }
              @case ('error') {
                <p class="text-xs text-rose-600">Failed to load evidence.</p>
              }
              @default {
                @if (detail(); as memoryDetail) {
                  <div class="space-y-3">
                    @if (memoryDetail.entities.length > 0) {
                      <div>
                        <div class="mb-1.5 text-xs font-medium tracking-wide text-slate-500 uppercase">Entities</div>
                        <div class="flex flex-wrap gap-1.5">
                          @for (entity of memoryDetail.entities; track entity.key) {
                            <span
                              class="rounded-md bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-700"
                              [title]="entity.key"
                            >
                              {{ entity.display }}
                            </span>
                          }
                        </div>
                      </div>
                    }
                    <div>
                      <div class="mb-1 text-xs font-medium tracking-wide text-slate-500 uppercase">Evidence</div>
                      @if (memoryDetail.evidence.length === 0) {
                        <p class="text-xs text-slate-500">No evidence recorded.</p>
                      } @else {
                        <ul class="space-y-1 text-xs text-slate-600">
                          @for (ev of memoryDetail.evidence; track ev.id) {
                            <li>
                              <span class="font-semibold text-slate-800">{{ ev.relation }}</span>
                              via {{ ev.source }} · {{ ev.observedAt | date: 'short' }}
                              @if (ev.note) {
                                <span class="text-slate-500"> — {{ ev.note }}</span>
                              }
                            </li>
                          }
                        </ul>
                      }
                    </div>
                  </div>
                }
              }
            }
          }
        }
      </div>

      <!-- Footer metadata -->
      <div class="mt-3 flex flex-wrap items-center gap-2 border-t border-slate-100 pt-2.5 text-xs text-slate-500">
        <app-badge [tone]="memory().tier === 'Working' ? 'sky' : 'neutral'">{{ memory().tier }}</app-badge>
        @if (memory().pinned) {
          <span class="inline-flex items-center gap-1 font-medium text-indigo-600" title="Pinned">
            <app-icon name="bookmark" sizeClass="w-3.5 h-3.5" />
            Pinned
          </span>
        }
        @if (memory().topic) {
          <span class="rounded-md bg-slate-100 px-2 py-0.5 font-medium text-slate-700">{{ memory().topic }}</span>
        }
        <span>{{ memory().confidence | percent: '1.0-0' }} confidence</span>
        <span class="h-1.5 w-14 overflow-hidden rounded-full bg-slate-100" aria-hidden="true">
          <span
            class="block h-full rounded-full bg-indigo-500"
            [style.width.%]="memory().confidence * 100"
          ></span>
        </span>
        <span>· {{ memory().sourceCount }} {{ memory().sourceCount === 1 ? 'source' : 'sources' }}</span>
        @if (memory().reinforcementCount > 0) {
          <span>· reinforced ×{{ memory().reinforcementCount }}</span>
        }
      </div>
    </article>
  `,
})
export class MemoryCardComponent {
  readonly memory = input.required<Memory>();
  readonly detailState = input<MemoryDetailState | undefined>(undefined);
  readonly deleting = input(false);

  readonly deleteRequested = output<Memory>();
  readonly evidenceRequested = output<Memory>();

  private readonly userTab = signal<MemoryTab | null>(null);

  protected readonly facts = computed(() => parseStringArray(this.memory().factsJson));
  protected readonly concepts = computed(() => parseStringArray(this.memory().conceptsJson));
  protected readonly filesRead = computed(() => parseStringArray(this.memory().filesReadJson));
  protected readonly filesModified = computed(() => parseStringArray(this.memory().filesModifiedJson));
  protected readonly hasDetails = computed(
    () => this.concepts() !== null || this.filesRead() !== null || this.filesModified() !== null,
  );
  protected readonly isSupersededOrObsolete = computed(() => {
    const status = this.memory().status;
    return status === 'Superseded' || status === 'Obsolete';
  });

  protected readonly activeTab = computed<MemoryTab>(() => {
    const chosen = this.userTab();
    if (chosen === 'facts' && !this.facts()) {
      return 'narrative';
    }
    if (chosen === 'details' && !this.hasDetails()) {
      return 'narrative';
    }
    if (chosen) {
      return chosen;
    }
    return this.facts() ? 'facts' : 'narrative';
  });

  protected readonly detail = computed<MemoryDetail | null>(() => {
    const state = this.detailState();
    return typeof state === 'object' && state !== null ? state : null;
  });

  protected readonly typeTone = computed<BadgeTone>(() => {
    switch (this.memory().type) {
      case 'Decision':
      case 'Constraint':
      case 'Architecture':
        return 'purple';
      case 'Issue':
        return 'danger';
      case 'Discovery':
        return 'primary';
      default:
        return 'neutral';
    }
  });

  protected selectTab(tab: MemoryTab): void {
    this.userTab.set(tab);
    if (tab === 'evidence') {
      this.evidenceRequested.emit(this.memory());
    }
  }

  protected tabClass(tab: MemoryTab): string {
    const active = this.activeTab() === tab;
    return (
      '-mb-px border-b-2 px-3 py-1.5 text-xs font-medium transition-colors ' +
      (active
        ? 'border-indigo-600 text-indigo-600'
        : 'border-transparent text-slate-500 hover:border-slate-300 hover:text-slate-700')
    );
  }
}
