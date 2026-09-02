import { CommonModule } from '@angular/common';
import { Component, computed, effect, inject, signal } from '@angular/core';
import { FormField, form, min } from '@angular/forms/signals';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule, MatSelectChange } from '@angular/material/select';
import { MatSlideToggleModule, MatSlideToggleChange } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../../core/api.service';
import { ProjectSettingsDto } from '../../core/models';
import { ProjectContextService } from '../projects/project-context.service';

@Component({
  selector: 'app-settings-panel',
  imports: [
    CommonModule,
    FormField,
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
  private readonly snackBar = inject(MatSnackBar);
  protected readonly ctx = inject(ProjectContextService);

  protected readonly saving = signal(false);
  protected readonly installing = signal(false);
  private lastReindexScheduledAt: string | null = null;

  protected readonly settingsModel = signal({
    embeddingProvider: 'noop',
    llmProvider: 'noop',
    ollamaBaseUrl: '',
    ollamaEmbeddingModel: '',
    ollamaChatModel: '',
    reindexEnabled: false,
    reindexIntervalMinutes: 60,
    sessionSummaryEnabled: false,
    sessionSummaryIntervalMinutes: 30,
    minEventsForSessionSummary: 15,
  });

  protected readonly settingsForm = form(this.settingsModel, (path) => {
    min(path.reindexIntervalMinutes, 1);
    min(path.sessionSummaryIntervalMinutes, 1);
    min(path.minEventsForSessionSummary, 1);
  });

  protected readonly showOllamaBaseUrl = computed(() => {
    const m = this.settingsModel();
    return m.embeddingProvider === 'ollama' || m.llmProvider === 'ollama';
  });
  protected readonly showOllamaEmbeddingModel = computed(() => this.settingsModel().embeddingProvider === 'ollama');
  protected readonly showOllamaChatModel = computed(() => this.settingsModel().llmProvider === 'ollama');

  /**
   * BACKLOG-09 / SEC-5: a user picking `openai`/`anthropic` gets no explanation of what leaves
   * the machine when that provider is selected. `noop` and `ollama` never send indexed content
   * off-box, so the disclosure is scoped to exactly the two external providers this Settings tab
   * can select.
   */
  private static readonly CloudProviders = new Set(['openai', 'anthropic']);
  protected readonly showCloudDisclosure = computed(() => {
    const m = this.settingsModel();
    return SettingsPanelComponent.CloudProviders.has(m.embeddingProvider) || SettingsPanelComponent.CloudProviders.has(m.llmProvider);
  });

  private readonly resource = this.api.settingsResource(this.ctx.projectId);
  protected readonly loading = this.resource.isLoading;

  constructor() {
    effect(() => {
      if (!this.resource.hasValue()) {
        return;
      }
      const settings = this.resource.value();
      this.lastReindexScheduledAt = settings.lastReindexScheduledAt;
      this.settingsModel.set({
        embeddingProvider: settings.embeddingProvider,
        llmProvider: settings.llmProvider,
        ollamaBaseUrl: settings.ollamaBaseUrl ?? 'http://localhost:11434',
        ollamaEmbeddingModel: settings.ollamaEmbeddingModel ?? 'embeddinggemma',
        ollamaChatModel: settings.ollamaChatModel ?? 'qwen2.5-coder:1.5b',
        reindexEnabled: settings.reindexEnabled,
        reindexIntervalMinutes: settings.reindexIntervalMinutes,
        sessionSummaryEnabled: settings.sessionSummaryIntervalMinutes > 0,
        sessionSummaryIntervalMinutes: settings.sessionSummaryIntervalMinutes > 0 ? settings.sessionSummaryIntervalMinutes : 30,
        minEventsForSessionSummary: settings.minEventsForSessionSummary,
      });
    });

    effect(() => {
      if (this.resource.error()) {
        this.snackBar.open('Failed to load settings — is Context.Host running?', 'Dismiss');
      }
    });
  }

  protected setEmbeddingProvider(event: MatSelectChange): void {
    this.settingsModel.update((current) => ({ ...current, embeddingProvider: event.value }));
  }

  protected setLlmProvider(event: MatSelectChange): void {
    this.settingsModel.update((current) => ({ ...current, llmProvider: event.value }));
  }

  protected setReindexEnabled(event: MatSlideToggleChange): void {
    this.settingsModel.update((current) => ({ ...current, reindexEnabled: event.checked }));
  }

  protected setSessionSummaryEnabled(event: MatSlideToggleChange): void {
    this.settingsModel.update((current) => ({ ...current, sessionSummaryEnabled: event.checked }));
  }

  protected save(): void {
    if (this.settingsForm().invalid()) {
      return;
    }

    const raw = this.settingsForm().value();
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
      minEventsForSessionSummary: raw.minEventsForSessionSummary,
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
