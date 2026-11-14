import { Component, computed, input } from '@angular/core';

type ChangedFiles = { kind: 'list'; items: string[] } | { kind: 'raw'; raw: string };

export function parseChangedFilesJson(json: string | null | undefined): ChangedFiles | null {
  if (!json) {
    return null;
  }
  try {
    const parsed = JSON.parse(json);
    return Array.isArray(parsed) ? { kind: 'list', items: parsed as string[] } : { kind: 'raw', raw: json };
  } catch {
    return { kind: 'raw', raw: json };
  }
}

@Component({
  selector: 'app-session-summary-fields',
  standalone: true,
  template: `
    <div class="space-y-4">
      @if (completed()) {
        <section class="space-y-1">
          <h3 class="text-xs font-semibold tracking-wider text-slate-500 uppercase">Completed</h3>
          <p class="text-sm leading-relaxed whitespace-pre-line text-slate-800">{{ completed() }}</p>
        </section>
      }
      @if (discovered()) {
        <section class="space-y-1">
          <h3 class="text-xs font-semibold tracking-wider text-slate-500 uppercase">Discovered</h3>
          <p class="text-sm leading-relaxed whitespace-pre-line text-slate-800">{{ discovered() }}</p>
        </section>
      }
      @if (decisions()) {
        <section class="space-y-1">
          <h3 class="text-xs font-semibold tracking-wider text-slate-500 uppercase">Decisions</h3>
          <p class="text-sm leading-relaxed whitespace-pre-line text-slate-800">{{ decisions() }}</p>
        </section>
      }
      @if (openIssues()) {
        <section class="space-y-1">
          <h3 class="text-xs font-semibold tracking-wider text-amber-700 uppercase">Open issues</h3>
          <p class="rounded-lg border border-amber-200 bg-amber-50/70 p-3 text-sm leading-relaxed whitespace-pre-line text-amber-950">
            {{ openIssues() }}
          </p>
        </section>
      }
      @if (nextSteps()) {
        <section class="space-y-1">
          <h3 class="text-xs font-semibold tracking-wider text-slate-500 uppercase">Next steps</h3>
          <p class="text-sm leading-relaxed whitespace-pre-line text-slate-800">{{ nextSteps() }}</p>
        </section>
      }
      @if (changedFiles(); as files) {
        <section class="space-y-1.5">
          <h3 class="text-xs font-semibold tracking-wider text-slate-500 uppercase">Changed files</h3>
          @if (files.kind === 'list') {
            @if (files.items.length === 0) {
              <p class="text-xs text-slate-500">No files recorded.</p>
            } @else {
              <ul class="flex flex-wrap gap-1.5">
                @for (file of files.items; track file) {
                  <li class="rounded-lg border border-slate-200 bg-slate-50 px-2.5 py-1 font-mono text-xs text-slate-700">
                    {{ file }}
                  </li>
                }
              </ul>
            }
          } @else {
            <pre class="overflow-x-auto rounded-lg bg-slate-100 p-2.5 font-mono text-xs text-slate-700">{{ files.raw }}</pre>
          }
        </section>
      }
    </div>
  `,
})
export class SessionSummaryFieldsComponent {
  readonly completed = input<string | null>(null);
  readonly discovered = input<string | null>(null);
  readonly decisions = input<string | null>(null);
  readonly openIssues = input<string | null>(null);
  readonly nextSteps = input<string | null>(null);
  readonly changedFilesJson = input<string | null>(null);

  protected readonly changedFiles = computed(() => parseChangedFilesJson(this.changedFilesJson()));
}
