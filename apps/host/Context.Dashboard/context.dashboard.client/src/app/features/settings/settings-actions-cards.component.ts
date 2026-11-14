import { Component, input, output } from '@angular/core';
import { IconComponent } from '../../shared/ui/icon.component';
import { SpinnerComponent } from '../../shared/ui/spinner.component';

@Component({
  selector: 'app-settings-actions-cards',
  standalone: true,
  imports: [IconComponent, SpinnerComponent],
  template: `
    <div class="space-y-4">
      <section class="rounded-xl border border-slate-200 bg-white p-5 shadow-xs">
        <h2 class="text-base font-semibold text-slate-900">Claude Code integration</h2>
        <p class="mt-1 text-sm text-slate-500">
          Writes the hooks, MCP server registration, and skill document that let Claude Code use this project's
          context automatically.
        </p>
        <div class="mt-4">
          <button
            type="button"
            class="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-3.5 py-2 text-sm font-medium text-slate-700 shadow-xs transition-colors hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
            [disabled]="installing()"
            (click)="installClaude.emit()"
          >
            @if (installing()) {
              <app-spinner sizeClass="w-4 h-4 text-indigo-600" />
            } @else {
              <app-icon name="terminal" sizeClass="w-4 h-4 text-indigo-600" />
            }
            Register Claude Code integration
          </button>
        </div>
      </section>

      <section class="rounded-xl border border-rose-200 bg-rose-50/40 p-5 shadow-xs">
        <h2 class="text-base font-semibold text-rose-900">Danger zone</h2>
        <p class="mt-1 text-sm text-rose-700">
          Permanently removes this project's index, memories, sessions, and settings from Context Platform. Files on
          disk are never touched.
        </p>
        <div class="mt-4">
          <button
            type="button"
            class="inline-flex items-center gap-2 rounded-lg border border-rose-300 bg-white px-3.5 py-2 text-sm font-medium text-rose-700 shadow-xs transition-colors hover:bg-rose-50 disabled:cursor-not-allowed disabled:opacity-50"
            [disabled]="deleting()"
            (click)="deleteProject.emit()"
          >
            @if (deleting()) {
              <app-spinner sizeClass="w-4 h-4 text-rose-700" />
            } @else {
              <app-icon name="delete-forever" sizeClass="w-4 h-4" />
            }
            Delete project
          </button>
        </div>
      </section>
    </div>
  `,
})
export class SettingsActionsCardsComponent {
  readonly installing = input(false);
  readonly deleting = input(false);

  readonly installClaude = output<void>();
  readonly deleteProject = output<void>();
}
