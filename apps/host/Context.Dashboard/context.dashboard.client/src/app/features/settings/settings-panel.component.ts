import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormField } from '@angular/forms/signals';
import { Router } from '@angular/router';
import { ApiService } from '../../core/api.service';
import { ConfirmDialogService } from '../../shared/confirm-dialog/confirm-dialog.service';
import { AlertBannerComponent } from '../../shared/ui/alert-banner.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { ProgressBarComponent } from '../../shared/ui/progress-bar.component';
import { SpinnerComponent } from '../../shared/ui/spinner.component';
import { ToastService } from '../../shared/ui/toast.service';
import { ProjectContextService } from '../projects/project-context.service';
import { SettingsActionsCardsComponent } from './settings-actions-cards.component';
import {
  BooleanSettingKey,
  createDefaultSettingsModel,
  createSettingsForm,
  mapDtoToFormModel,
  mapFormModelToDto,
} from './settings-form.model';
import { SettingsProvidersCardComponent } from './settings-providers-card.component';
import { SettingsSectionCardComponent } from './settings-section-card.component';
import { SettingsToggleComponent } from './settings-toggle.component';

@Component({
  selector: 'app-settings-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormField,
    ProgressBarComponent,
    SpinnerComponent,
    IconComponent,
    AlertBannerComponent,
    SettingsProvidersCardComponent,
    SettingsSectionCardComponent,
    SettingsToggleComponent,
    SettingsActionsCardsComponent,
  ],
  templateUrl: './settings-panel.component.html',
})
export class SettingsPanelComponent {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);
  private readonly confirmDialog = inject(ConfirmDialogService);
  protected readonly ctx = inject(ProjectContextService);

  protected readonly saving = signal(false);
  protected readonly installing = signal(false);
  protected readonly deleting = signal(false);
  private lastReindexScheduledAt: string | null = null;
  private lastMemoryReconsolidationScheduledAt: string | null = null;
  private loadedProjectId = '';
  private loadedModelSnapshot = '';

  protected readonly settingsModel = signal(createDefaultSettingsModel());
  protected readonly settingsForm = createSettingsForm(this.settingsModel);

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
    const online = m.onlineMergeEnabled
      ? `Online merge up to ${m.onlineMergeMaxSources} memories`
      : 'Online merge off';
    const periodic = m.memoryReconsolidationEnabled
      ? `reconsolidation every ${m.memoryReconsolidationIntervalMinutes} min`
      : 'reconsolidation off';
    return `${online} · ${periodic}`;
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
      m.staleSessionCleanupEnabled ? `stale sessions ${m.staleSessionTimeoutMinutes} min` : 'stale sessions kept',
      m.deleteSessionEventsAfterSummary ? 'events dropped after summary' : 'events kept',
      m.hookInvocationRetentionEnabled ? `hooks ${m.hookInvocationRetentionDays} d` : 'hooks kept',
    ];
    return parts.join(' · ');
  });

  private readonly resource = this.api.settingsResource(this.ctx.projectId);
  protected readonly loading = computed(() => !this.ctx.projectId() || this.resource.isLoading());

  constructor() {
    effect(() => {
      if (!this.resource.hasValue()) {
        return;
      }
      const settings = this.resource.value();
      const projectId = this.ctx.projectId();
      const isProjectSwitch = projectId !== this.loadedProjectId;
      const hasUnsavedChanges =
        !isProjectSwitch && JSON.stringify(untracked(this.settingsModel)) !== this.loadedModelSnapshot;

      this.lastReindexScheduledAt = settings.lastReindexScheduledAt;
      this.lastMemoryReconsolidationScheduledAt = settings.lastMemoryReconsolidationScheduledAt;
      this.loadedProjectId = projectId;

      if (!hasUnsavedChanges) {
        const nextModel = mapDtoToFormModel(settings);
        this.settingsModel.set(nextModel);
        this.loadedModelSnapshot = JSON.stringify(nextModel);
      }
    });

    effect(() => {
      if (this.resource.error()) {
        this.toast.error('Failed to load settings — is Context Dashboard running?');
      }
    });
  }

  protected setProvider(change: { key: 'embeddingProvider' | 'llmProvider'; value: string }): void {
    this.settingsModel.update((current) => ({ ...current, [change.key]: change.value }));
  }

  protected setFlag(key: BooleanSettingKey, checked: boolean): void {
    this.settingsModel.update((current) => ({ ...current, [key]: checked }));
  }

  protected save(): void {
    if (this.settingsForm().invalid()) {
      return;
    }

    const dto = mapFormModelToDto(
      this.settingsForm().value(),
      this.lastReindexScheduledAt,
      this.lastMemoryReconsolidationScheduledAt,
    );

    this.saving.set(true);
    this.api.updateSettings(this.ctx.projectId(), dto).subscribe({
      next: () => {
        this.saving.set(false);
        this.loadedModelSnapshot = JSON.stringify(this.settingsModel());
        this.toast.success('Settings saved.');
      },
      error: () => {
        this.saving.set(false);
        this.toast.error('Failed to save settings.');
      },
    });
  }

  protected installClaude(): void {
    this.installing.set(true);
    this.api.installClaude(this.ctx.projectId()).subscribe({
      next: () => {
        this.installing.set(false);
        this.toast.success('Claude Code integration registered.');
      },
      error: () => {
        this.installing.set(false);
        this.toast.error('Failed to register Claude Code integration.');
      },
    });
  }

  protected async deleteProject(): Promise<void> {
    const projectId = this.ctx.projectId();
    const projectName = this.ctx.projectName();

    const confirmed = await this.confirmDialog.open({
      title: 'Delete project',
      message: `Delete "${projectName}"? This permanently removes its index, memories, sessions, and settings from Context Platform. Files on disk are never touched.`,
      confirmLabel: 'Delete project',
      confirmTone: 'danger',
    });
    if (!confirmed) {
      return;
    }

    this.deleting.set(true);
    this.api.deleteProject(projectId).subscribe({
      next: () => {
        this.toast.success('Project deleted.', 4000);
        this.router.navigate(['/projects']);
      },
      error: () => {
        this.deleting.set(false);
        this.toast.error('Failed to delete project.');
      },
    });
  }
}
