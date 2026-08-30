import { CommonModule } from '@angular/common';
import { Component, computed, effect, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../../core/api.service';
import { ProjectSettingsDto } from '../../core/models';
import { ProjectContextService } from '../projects/project-context.service';

@Component({
  selector: 'app-settings-panel',
  imports: [
    CommonModule,
    ReactiveFormsModule,
    MatCardModule,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatSlideToggleModule,
    MatProgressBarModule,
  ],
  templateUrl: './settings-panel.component.html',
  styleUrl: './settings-panel.component.scss',
})
export class SettingsPanelComponent {
  private readonly api = inject(ApiService);
  private readonly fb = inject(FormBuilder);
  private readonly snackBar = inject(MatSnackBar);
  protected readonly ctx = inject(ProjectContextService);

  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly installing = signal(false);
  private lastReindexScheduledAt: string | null = null;
  private lastLoadedProjectId = '';

  protected readonly form = this.fb.nonNullable.group({
    embeddingProvider: ['noop'],
    llmProvider: ['noop'],
    ollamaBaseUrl: [''],
    ollamaEmbeddingModel: [''],
    ollamaChatModel: [''],
    reindexEnabled: [false],
    reindexIntervalMinutes: [60],
    sessionSummaryEnabled: [false],
    sessionSummaryIntervalMinutes: [30],
  });

  private readonly formValue = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });

  protected readonly showOllamaBaseUrl = computed(() => {
    const v = this.formValue();
    return v.embeddingProvider === 'ollama' || v.llmProvider === 'ollama';
  });
  protected readonly showOllamaEmbeddingModel = computed(() => this.formValue().embeddingProvider === 'ollama');
  protected readonly showOllamaChatModel = computed(() => this.formValue().llmProvider === 'ollama');

  constructor() {
    // Same guard pattern as MemoriesPanelComponent: ProjectContextService.init(id) fires from an
    // effect in ProjectDetailComponent, so projectId() can still be '' on first construction, and
    // re-runs the load if the user switches projects while this tab stays mounted.
    effect(() => {
      const id = this.ctx.projectId();
      if (id && id !== this.lastLoadedProjectId) {
        this.lastLoadedProjectId = id;
        this.loadSettings(id);
      }
    });
  }

  private loadSettings(projectId: string): void {
    this.loading.set(true);
    this.api.getSettings(projectId).subscribe({
      next: (settings) => {
        this.lastReindexScheduledAt = settings.lastReindexScheduledAt;
        this.form.reset({
          embeddingProvider: settings.embeddingProvider,
          llmProvider: settings.llmProvider,
          ollamaBaseUrl: settings.ollamaBaseUrl ?? '',
          ollamaEmbeddingModel: settings.ollamaEmbeddingModel ?? '',
          ollamaChatModel: settings.ollamaChatModel ?? '',
          reindexEnabled: settings.reindexEnabled,
          reindexIntervalMinutes: settings.reindexIntervalMinutes,
          sessionSummaryEnabled: settings.sessionSummaryIntervalMinutes > 0,
          sessionSummaryIntervalMinutes: settings.sessionSummaryIntervalMinutes > 0 ? settings.sessionSummaryIntervalMinutes : 30,
        });
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.snackBar.open('Failed to load settings — is Context.Host running?', 'Dismiss');
      },
    });
  }

  protected save(): void {
    const raw = this.form.getRawValue();
    const dto: ProjectSettingsDto = {
      embeddingProvider: raw.embeddingProvider,
      llmProvider: raw.llmProvider,
      ollamaBaseUrl: raw.ollamaBaseUrl || null,
      ollamaEmbeddingModel: raw.ollamaEmbeddingModel || null,
      ollamaChatModel: raw.ollamaChatModel || null,
      reindexEnabled: raw.reindexEnabled,
      reindexIntervalMinutes: raw.reindexIntervalMinutes,
      lastReindexScheduledAt: this.lastReindexScheduledAt,
      sessionSummaryIntervalMinutes: raw.sessionSummaryEnabled ? raw.sessionSummaryIntervalMinutes : 0,
    };

    this.saving.set(true);
    this.api.updateSettings(this.ctx.projectId(), dto).subscribe({
      next: () => {
        this.saving.set(false);
        this.snackBar.open('Settings saved.', 'Dismiss');
      },
      error: () => {
        this.saving.set(false);
        this.snackBar.open('Failed to save settings.', 'Dismiss');
      },
    });
  }

  protected installClaude(): void {
    this.installing.set(true);
    this.api.installClaude(this.ctx.projectId()).subscribe({
      next: () => {
        this.installing.set(false);
        this.snackBar.open('Claude Code integration registered.', 'Dismiss');
      },
      error: () => {
        this.installing.set(false);
        this.snackBar.open('Failed to register Claude Code integration.', 'Dismiss');
      },
    });
  }
}
