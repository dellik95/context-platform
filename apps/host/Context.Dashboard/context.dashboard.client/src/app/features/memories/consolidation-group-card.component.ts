import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import { ReconsolidationGroup } from '../../core/models';
import { BadgeComponent } from '../../shared/ui/badge.component';
import { IconComponent } from '../../shared/ui/icon.component';

@Component({
  selector: 'app-consolidation-group-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BadgeComponent, IconComponent],
  template: `
    <div class="overflow-hidden rounded-xl border border-slate-200 bg-white">
      <div class="flex items-center justify-between gap-3 px-4 py-2.5 hover:bg-slate-50/80">
        <div class="flex min-w-0 flex-1 items-center gap-3">
          @if (pending()) {
            <input
              type="checkbox"
              class="h-4 w-4 shrink-0 rounded border-slate-300 text-indigo-600 focus:ring-indigo-500 disabled:cursor-not-allowed disabled:opacity-50"
              [checked]="!group().ambiguous && included()"
              [disabled]="group().ambiguous"
              title="Include this group when applying"
              aria-label="Include group when applying"
              (click)="$event.stopPropagation()"
              (change)="onCheckboxChange($event)"
            />
          }

          <button
            type="button"
            class="flex min-w-0 flex-1 flex-wrap items-center gap-2 text-left"
            [attr.aria-expanded]="expanded()"
            (click)="expanded.set(!expanded())"
          >
            <span class="text-sm font-semibold text-slate-900">Group {{ group().index }}</span>
            <span class="text-xs text-slate-500">
              {{ group().sources.length }} sources → {{ group().canonicals.length }} canonical
            </span>
            @if (group().contradictions.length > 0) {
              <app-badge tone="danger">{{ group().contradictions.length }} contradictions</app-badge>
            }
            @if (group().ambiguous) {
              <app-badge tone="warning">ambiguous</app-badge>
            }
          </button>
        </div>

        <button
          type="button"
          class="rounded-md p-1 text-slate-400 transition-colors hover:bg-slate-100 hover:text-slate-700"
          [attr.aria-expanded]="expanded()"
          aria-label="Toggle group details"
          (click)="expanded.set(!expanded())"
        >
          <app-icon [name]="expanded() ? 'chevron-down' : 'chevron-right'" sizeClass="w-4 h-4" />
        </button>
      </div>

      @if (expanded()) {
        <div class="space-y-4 border-t border-slate-100 bg-slate-50/40 px-4 py-3 text-xs">
          <div class="grid gap-4 md:grid-cols-[1fr_auto_1fr] md:items-start">
            <div>
              <div class="mb-1.5 font-semibold tracking-wide text-slate-500 uppercase">Sources</div>
              <ul class="space-y-1.5 text-slate-700">
                @for (source of group().sources; track source.id) {
                  <li [class.text-slate-400]="group().keepSeparate.includes(source.id)">
                    <span class="font-semibold text-slate-800">{{ source.type }}</span>
                    {{ source.title }}
                    @if (source.status !== 'Active') {
                      <span class="text-slate-400">({{ source.status }})</span>
                    }
                    @if (group().keepSeparate.includes(source.id)) {
                      <span class="text-slate-400">· kept separate</span>
                    }
                  </li>
                }
              </ul>
            </div>

            <div class="hidden pt-5 text-slate-400 md:block">
              <app-icon name="arrow-forward" sizeClass="w-4 h-4" />
            </div>

            <div>
              <div class="mb-1.5 font-semibold tracking-wide text-slate-500 uppercase">Canonicals</div>
              <div class="space-y-2.5">
                @for (canonical of group().canonicals; track $index) {
                  <div class="rounded-lg border border-slate-200 bg-white p-3">
                    <div class="font-medium text-slate-900">
                      <span class="font-semibold text-indigo-700">{{ canonical.type }}</span>
                      @if (canonical.topic) {
                        <span class="text-slate-500"> · {{ canonical.topic }}</span>
                      }
                      <span class="ml-1">{{ canonical.title }}</span>
                    </div>
                    <p class="mt-1 whitespace-pre-line text-slate-600">{{ canonical.body }}</p>
                    @if (canonical.facts.length > 0) {
                      <ul class="mt-1.5 list-disc space-y-0.5 pl-4 text-slate-600">
                        @for (fact of canonical.facts; track $index) {
                          <li>{{ fact }}</li>
                        }
                      </ul>
                    }
                    <div class="mt-1.5 text-[11px] text-slate-400">from {{ canonical.sourceIds.length }} sources</div>
                  </div>
                } @empty {
                  <span class="text-slate-400">None</span>
                }
              </div>
            </div>
          </div>

          @if (group().contradictions.length > 0) {
            <div>
              <div class="mb-1 font-semibold tracking-wide text-rose-700 uppercase">Contradictions</div>
              <ul class="space-y-1 text-slate-700">
                @for (contradiction of group().contradictions; track $index) {
                  <li>
                    "{{ titleOf(contradiction.a) }}" vs "{{ titleOf(contradiction.b) }}"
                    @if (contradiction.reason) {
                      <span> — {{ contradiction.reason }}</span>
                    }
                    @if (contradiction.codeCheck; as check) {
                      <span class="text-slate-500">
                        (code: {{ check.a }} / {{ check.b }}{{ check.winnerId ? '; keeps "' + titleOf(check.winnerId) + '"' : '' }})
                      </span>
                    }
                  </li>
                }
              </ul>
            </div>
          }

          @if (group().note) {
            <p class="text-slate-500">{{ group().note }}</p>
          }
        </div>
      }
    </div>
  `,
})
export class ConsolidationGroupCardComponent {
  readonly group = input.required<ReconsolidationGroup>();
  readonly pending = input(false);
  readonly included = input(true);
  readonly titleLookup = input.required<ReadonlyMap<string, string>>();

  readonly includedChange = output<boolean>();

  protected readonly expanded = signal(false);

  protected onCheckboxChange(event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    this.includedChange.emit(checked);
  }

  protected titleOf(id: string): string {
    return this.titleLookup().get(id) ?? id;
  }
}
