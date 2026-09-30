import { Component, computed, effect, inject, signal } from '@angular/core';
import { FormField, form, min } from '@angular/forms/signals';
import { Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule, MatSelectChange } from '@angular/material/select';
import { MatSlideToggleModule, MatSlideToggleChange } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../../core/api.service';
import { ProjectSettingsDto } from '../../core/models';
import { ConfirmDialogComponent } from '../../shared/confirm-dialog/confirm-dialog.component';
import { ProjectContextService } from '../projects/project-context.service';

@Component({
  selector: 'app-settings-panel',
  imports: [
    FormField,
    MatCardModule,
    MatButtonModule,
    MatExpansionModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatSlideToggleModule,
    MatProgressBarModule,
    MatProgressSpinnerModule,
    MatDialogModule,
  ],
  templateUrl: './settings-panel.component.html',
  styleUrl: './settings-panel.component.scss',
})
export class SettingsPanelComponent {
  private readonly api = inject(ApiService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);
  protected readonly ctx = inject(ProjectContextService);

  protected readonly saving = signal(false);
  protected readonly installing = signal(false);
  protected readonly deleting = signal(false);
  private lastReindexScheduledAt: string | null = null;
  private lastMemoryReconsolidationScheduledAt: string | null = null;

  protected readonly settingsModel = signal({
    embeddingProvider: 'noop',
    llmProvider: 'noop',
    ollamaBaseUrl: '',
    ollamaEmbeddingModel: '',
    ollamaChatModel: '',
    foundryBaseUrl: '',
    foundryEmbeddingModel: '',
    foundryChatModel: '',
    reindexEnabled: false,
    reindexIntervalMinutes: 60,
    sessionSummaryEnabled: false,
    sessionSummaryIntervalMinutes: 30,
    minEventsForSessionSummary: 15,
    maxMemoryNotesPerSection: 20,
    minEventsForMemoryExtraction: 20,
    explicitRememberEnabled: true,
    staleSessionCleanupEnabled: true,
    staleSessionTimeoutMinutes: 240,
    deleteSessionEventsAfterSummary: false,
    hookInvocationRetentionEnabled: true,
    hookInvocationRetentionDays: 30,
    memoryReconsolidationEnabled: false,
    memoryReconsolidationIntervalMinutes: 60,
    llmTimeoutSeconds: 1200,
    llmRetryEnabled: false,
    llmMaxRetries: 2,
    llmRetryBaseDelaySeconds: 30,
    rateLimitCooldownMinutes: 5,
    rateLimitMaxCooldownMinutes: 60,
  });

  protected readonly settingsForm = form(this.settingsModel, (path) => {
    min(path.reindexIntervalMinutes, 1);
    min(path.sessionSummaryIntervalMinutes, 1);
    min(path.minEventsForSessionSummary, 1);
    min(path.maxMemoryNotesPerSection, 1);
    min(path.minEventsForMemoryExtraction, 1);
    min(path.staleSessionTimeoutMinutes, 1);
    min(path.hookInvocationRetentionDays, 1);
    min(path.memoryReconsolidationIntervalMinutes, 1);
    min(path.llmTimeoutSeconds, 1);
    min(path.llmMaxRetries, 1);
    min(path.llmRetryBaseDelaySeconds, 1);
    min(path.rateLimitCooldownMinutes, 1);
    min(path.rateLimitMaxCooldownMinutes, ({ valueOf }) => valueOf(path.rateLimitCooldownMinutes));
  });

  protected readonly showOllamaBaseUrl = computed(() => {
    const m = this.settingsModel();
    return m.embeddingProvider === 'ollama' || m.llmProvider === 'ollama';
  });
  protected readonly showOllamaEmbeddingModel = computed(
    () => this.settingsModel().embeddingProvider === 'ollama',
  );
  protected readonly showOllamaChatModel = computed(
    () => this.settingsModel().llmProvider === 'ollama',
  );
  protected readonly showFoundryBaseUrl = computed(() => {
    const m = this.settingsModel();
    return m.embeddingProvider === 'foundry' || m.llmProvider === 'foundry';
  });
  protected readonly showFoundryEmbeddingModel = computed(
    () => this.settingsModel().embeddingProvider === 'foundry',
  );
  protected readonly showFoundryChatModel = computed(
    () => this.settingsModel().llmProvider === 'foundry',
  );

  /**
   * A user picking `openai`/`anthropic` gets no explanation of what leaves
   * the machine when that provider is selected. `noop`, `ollama` and `foundry` never send indexed content
   * off-box, so the disclosure is scoped to exactly the two external providers this Settings tab
   * can select.
   */
  private static readonly CloudProviders = new Set(['openai', 'anthropic']);
  protected readonly showCloudDisclosure = computed(() => {
    const m = this.settingsModel();
    return (
      SettingsPanelComponent.CloudProviders.has(m.embeddingProvider) ||
      SettingsPanelComponent.CloudProviders.has(m.llmProvider)
    );
  });

  // Collapsed-panel headers: each section states its current effect so the page can be read
  // top-to-bottom without expanding anything.
  protected readonly reindexSummary = computed(() => {
    const m = this.settingsModel();
    return m.reindexEnabled ? `Every ${m.reindexIntervalMinutes} min` : 'Off';
  });

  protected readonly sessionSummarySummary = computed(() => {
    const m = this.settingsModel();
    return m.sessionSummaryEnabled
      ? `Every ${m.sessionSummaryIntervalMinutes} min after ${m.minEventsForSessionSummary} events`
      : 'Only when a session ends';
  });

  protected readonly memoryRetrievalSummary = computed(() => {
    const m = this.settingsModel();
    const remember = m.explicitRememberEnabled ? 'agent writes on' : 'agent writes off';
    return `Every ${m.minEventsForMemoryExtraction} events · ${m.maxMemoryNotesPerSection} per section · ${remember}`;
  });

  protected readonly memoryMergingSummary = computed(() => {
    const m = this.settingsModel();
    return m.memoryReconsolidationEnabled
      ? `Reconsolidation every ${m.memoryReconsolidationIntervalMinutes} min`
      : 'Reconsolidation off';
  });

  protected readonly resilienceSummary = computed(() => {
    const m = this.settingsModel();
    const retries = m.llmRetryEnabled
      ? `${m.llmMaxRetries} retries from ${m.llmRetryBaseDelaySeconds} s`
      : 'no retries';
    return `Timeout ${m.llmTimeoutSeconds} s · ${retries} · 429 pause ${m.rateLimitCooldownMinutes}–${m.rateLimitMaxCooldownMinutes} min`;
  });

  protected readonly retentionSummary = computed(() => {
    const m = this.settingsModel();
    const parts = [
      m.staleSessionCleanupEnabled
        ? `stale sessions ${m.staleSessionTimeoutMinutes} min`
        : 'stale sessions kept',
      m.deleteSessionEventsAfterSummary ? 'events dropped after summary' : 'events kept',
      m.hookInvocationRetentionEnabled ? `hooks ${m.hookInvocationRetentionDays} d` : 'hooks kept',
    ];
    return parts.join(' · ');
  });

  private readonly resource = this.api.settingsResource(this.ctx.projectId);
  // No projectId yet means ctx.init is still reissuing a token; showing the form then would offer
  // the placeholder defaults above for saving.
  protected readonly loading = computed(() => !this.ctx.projectId() || this.resource.isLoading());

  constructor() {
    effect(() => {
      if (!this.resource.hasValue()) {
        return;
      }
      const settings = this.resource.value();
      this.lastReindexScheduledAt = settings.lastReindexScheduledAt;
      this.lastMemoryReconsolidationScheduledAt = settings.lastMemoryReconsolidationScheduledAt;
      this.settingsModel.set({
        embeddingProvider: settings.embeddingProvider,
        llmProvider: settings.llmProvider,
        ollamaBaseUrl: settings.ollamaBaseUrl ?? 'http://localhost:11434',
        ollamaEmbeddingModel: settings.ollamaEmbeddingModel ?? 'embeddinggemma',
        ollamaChatModel: settings.ollamaChatModel ?? 'qwen2.5-coder:1.5b',
        // Left blank rather than pre-filled: blank defers to CONTEXT_FOUNDRY_*, whose port is machine-specific.
        foundryBaseUrl: settings.foundryBaseUrl ?? '',
        foundryEmbeddingModel: settings.foundryEmbeddingModel ?? '',
        foundryChatModel: settings.foundryChatModel ?? '',
        reindexEnabled: settings.reindexEnabled,
        reindexIntervalMinutes: settings.reindexIntervalMinutes,
        sessionSummaryEnabled: settings.sessionSummaryIntervalMinutes > 0,
        sessionSummaryIntervalMinutes:
          settings.sessionSummaryIntervalMinutes > 0 ? settings.sessionSummaryIntervalMinutes : 30,
        minEventsForSessionSummary: settings.minEventsForSessionSummary,
        maxMemoryNotesPerSection: settings.maxMemoryNotesPerSection,
        minEventsForMemoryExtraction: settings.minEventsForMemoryExtraction,
        explicitRememberEnabled: settings.explicitRememberEnabled,
        staleSessionCleanupEnabled: settings.staleSessionTimeoutMinutes > 0,
        staleSessionTimeoutMinutes:
          settings.staleSessionTimeoutMinutes > 0 ? settings.staleSessionTimeoutMinutes : 240,
        deleteSessionEventsAfterSummary: settings.deleteSessionEventsAfterSummary,
        hookInvocationRetentionEnabled: settings.hookInvocationRetentionDays > 0,
        hookInvocationRetentionDays:
          settings.hookInvocationRetentionDays > 0 ? settings.hookInvocationRetentionDays : 30,
        memoryReconsolidationEnabled: settings.memoryReconsolidationIntervalMinutes > 0,
        memoryReconsolidationIntervalMinutes:
          settings.memoryReconsolidationIntervalMinutes > 0
            ? settings.memoryReconsolidationIntervalMinutes
            : 60,
        llmTimeoutSeconds: settings.llmTimeoutSeconds,
        llmRetryEnabled: settings.llmMaxRetries > 0,
        llmMaxRetries: settings.llmMaxRetries > 0 ? settings.llmMaxRetries : 2,
        llmRetryBaseDelaySeconds: settings.llmRetryBaseDelaySeconds,
        rateLimitCooldownMinutes: settings.rateLimitCooldownMinutes,
        rateLimitMaxCooldownMinutes: settings.rateLimitMaxCooldownMinutes,
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

  protected setExplicitRememberEnabled(event: MatSlideToggleChange): void {
    this.settingsModel.update((current) => ({
      ...current,
      explicitRememberEnabled: event.checked,
    }));
  }

  protected setStaleSessionCleanupEnabled(event: MatSlideToggleChange): void {
    this.settingsModel.update((current) => ({
      ...current,
      staleSessionCleanupEnabled: event.checked,
    }));
  }

  protected setDeleteSessionEventsAfterSummary(event: MatSlideToggleChange): void {
    this.settingsModel.update((current) => ({
      ...current,
      deleteSessionEventsAfterSummary: event.checked,
    }));
  }

  protected setHookInvocationRetentionEnabled(event: MatSlideToggleChange): void {
    this.settingsModel.update((current) => ({
      ...current,
      hookInvocationRetentionEnabled: event.checked,
    }));
  }

  protected setMemoryReconsolidationEnabled(event: MatSlideToggleChange): void {
    this.settingsModel.update((current) => ({
      ...current,
      memoryReconsolidationEnabled: event.checked,
    }));
  }

  protected setLlmRetryEnabled(event: MatSlideToggleChange): void {
    this.settingsModel.update((current) => ({
      ...current,
      llmRetryEnabled: event.checked,
    }));
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
      foundryBaseUrl: raw.foundryBaseUrl || null,
      foundryEmbeddingModel: raw.foundryEmbeddingModel || null,
      foundryChatModel: raw.foundryChatModel || null,
      reindexEnabled: raw.reindexEnabled,
      reindexIntervalMinutes: raw.reindexIntervalMinutes,
      lastReindexScheduledAt: this.lastReindexScheduledAt,
      sessionSummaryIntervalMinutes: raw.sessionSummaryEnabled
        ? raw.sessionSummaryIntervalMinutes
        : 0,
      minEventsForSessionSummary: raw.minEventsForSessionSummary,
      maxMemoryNotesPerSection: raw.maxMemoryNotesPerSection,
      minEventsForMemoryExtraction: raw.minEventsForMemoryExtraction,
      explicitRememberEnabled: raw.explicitRememberEnabled,
      staleSessionTimeoutMinutes: raw.staleSessionCleanupEnabled
        ? raw.staleSessionTimeoutMinutes
        : 0,
      deleteSessionEventsAfterSummary: raw.deleteSessionEventsAfterSummary,
      hookInvocationRetentionDays: raw.hookInvocationRetentionEnabled
        ? raw.hookInvocationRetentionDays
        : 0,
      memoryReconsolidationIntervalMinutes: raw.memoryReconsolidationEnabled
        ? raw.memoryReconsolidationIntervalMinutes
        : 0,
      lastMemoryReconsolidationScheduledAt: this.lastMemoryReconsolidationScheduledAt,
      llmTimeoutSeconds: raw.llmTimeoutSeconds,
      llmMaxRetries: raw.llmRetryEnabled ? raw.llmMaxRetries : 0,
      llmRetryBaseDelaySeconds: raw.llmRetryBaseDelaySeconds,
      rateLimitCooldownMinutes: raw.rateLimitCooldownMinutes,
      rateLimitMaxCooldownMinutes: raw.rateLimitMaxCooldownMinutes,
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

  protected deleteProject(): void {
    const projectId = this.ctx.projectId();
    const projectName = this.ctx.projectName();

    this.dialog
      .open(ConfirmDialogComponent, {
        width: '440px',
        data: {
          title: 'Delete project',
          message: `Delete "${projectName}"? This permanently removes its index, memories, sessions, and settings from Context Platform. Files on disk are never touched.`,
          confirmLabel: 'Delete project',
        },
      })
      .afterClosed()
      .subscribe((confirmed) => {
        if (!confirmed) {
          return;
        }

        this.deleting.set(true);
        this.api.deleteProject(projectId).subscribe({
          next: () => {
            this.snackBar.open('Project deleted.', 'Dismiss', { duration: 4000 });
            this.router.navigate(['/projects']);
          },
          error: () => {
            this.deleting.set(false);
            this.snackBar.open('Failed to delete project.', 'Dismiss');
          },
        });
      });
  }
}
