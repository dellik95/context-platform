import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { of } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { ProjectSettingsDto } from '../../core/models';
import { ProjectContextService } from '../projects/project-context.service';
import { SettingsPanelComponent } from './settings-panel.component';

describe('SettingsPanelComponent', () => {
  let fixture: ComponentFixture<SettingsPanelComponent>;

  const baseSettings: ProjectSettingsDto = {
    embeddingProvider: 'noop',
    llmProvider: 'noop',
    ollamaBaseUrl: null,
    ollamaEmbeddingModel: null,
    ollamaChatModel: null,
    foundryBaseUrl: null,
    foundryEmbeddingModel: null,
    foundryChatModel: null,
    reindexEnabled: false,
    reindexIntervalMinutes: 60,
    lastReindexScheduledAt: null,
    sessionSummaryIntervalMinutes: 0,
    minEventsForSessionSummary: 15,
    maxMemoryNotesPerSection: 20,
    minEventsForMemoryExtraction: 20,
    explicitRememberEnabled: true,
    staleSessionTimeoutMinutes: 240,
    deleteSessionEventsAfterSummary: false,
    hookInvocationRetentionDays: 30,
    memoryReconsolidationIntervalMinutes: 0,
    lastMemoryReconsolidationScheduledAt: null,
    onlineMergeEnabled: true,
    onlineMergeMaxSources: 5,
    mergedMemoryMaxBodyChars: 8000,
    mergedMemoryMaxFacts: 40,
    reconsolidationMaxGroupSize: 12,
    reconsolidationClusterMinSimilarity: 0.6,
    reconsolidationSourceExcerptChars: 3000,
    llmTimeoutSeconds: 1200,
    llmMaxRetries: 0,
    llmRetryBaseDelaySeconds: 30,
    rateLimitCooldownMinutes: 5,
    rateLimitMaxCooldownMinutes: 60,
    embeddingBatchSize: 0,
  };

  function fakeSettingsResource(settings: ProjectSettingsDto) {
    return {
      isLoading: () => false,
      hasValue: () => true,
      value: () => settings,
      error: () => undefined,
    };
  }

  async function createWith(
    settings: ProjectSettingsDto,
    updateSettings: ApiService['updateSettings'] = () => of(undefined),
  ): Promise<void> {
    const apiServiceStub: Partial<ApiService> = {
      settingsResource: () => fakeSettingsResource(settings) as unknown as ReturnType<ApiService['settingsResource']>,
      updateSettings,
    };

    await TestBed.configureTestingModule({
      imports: [SettingsPanelComponent],
      providers: [
        { provide: ApiService, useValue: apiServiceStub },
        { provide: ProjectContextService, useValue: { projectId: () => 'test-project', projectName: () => 'Test Project' } },
        { provide: MatSnackBar, useValue: { open: () => undefined } },
        { provide: Router, useValue: { navigate: () => Promise.resolve(true) } },
        { provide: MatDialog, useValue: { open: () => ({ afterClosed: () => ({ subscribe: () => undefined }) }) } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(SettingsPanelComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  /**
   * Coverage: the cloud data-use disclosure must render when (and only when) a
   * non-local provider (openai/anthropic) is selected in either provider dropdown, and stay hidden
   * for ollama/foundry/noop. `ApiService` and `ProjectContextService` are stubbed so this exercises only the
   * component's own rendering logic, not the real HTTP/SignalR wiring `ProjectContextService`
   * normally brings with it.
   */
  describe('cloud provider disclosure', () => {
    function disclosureElement(): Element | null {
      return (fixture.nativeElement as HTMLElement).querySelector('.provider-note--cloud-disclosure');
    }

    it('shows the disclosure when the embedding provider is openai', async () => {
      await createWith({ ...baseSettings, embeddingProvider: 'openai' });

      expect(disclosureElement()).not.toBeNull();
      expect(disclosureElement()?.textContent).toContain('sent to the selected external provider');
    });

    it('shows the disclosure when the llm provider is anthropic', async () => {
      await createWith({ ...baseSettings, llmProvider: 'anthropic' });

      expect(disclosureElement()).not.toBeNull();
    });

    it('does not show the disclosure when both providers are noop', async () => {
      await createWith(baseSettings);

      expect(disclosureElement()).toBeNull();
    });

    it('does not show the disclosure when both providers are ollama', async () => {
      await createWith({ ...baseSettings, embeddingProvider: 'ollama', llmProvider: 'ollama' });

      expect(disclosureElement()).toBeNull();
    });

    it('does not show the disclosure when both providers are foundry', async () => {
      await createWith({ ...baseSettings, embeddingProvider: 'foundry', llmProvider: 'foundry' });

      expect(disclosureElement()).toBeNull();
    });
  });

  describe('memory merge settings', () => {
    const mergeSettings = {
      onlineMergeEnabled: false,
      onlineMergeMaxSources: 3,
      mergedMemoryMaxBodyChars: 12000,
      mergedMemoryMaxFacts: 60,
      reconsolidationMaxGroupSize: 20,
      reconsolidationClusterMinSimilarity: 0.75,
      reconsolidationSourceExcerptChars: 5000,
    };

    const root = () => fixture.nativeElement as HTMLElement;

    function submit(): void {
      root().querySelector('form')?.dispatchEvent(new Event('submit'));
    }

    function fieldInput(label: string): HTMLInputElement {
      const field = [...root().querySelectorAll('mat-form-field')].find(
        (f) => f.querySelector('mat-label')?.textContent?.trim() === label,
      );
      return field?.querySelector('input') as HTMLInputElement;
    }

    function type(label: string, value: string): void {
      const input = fieldInput(label);
      input.value = value;
      input.dispatchEvent(new Event('input'));
      fixture.detectChanges();
    }

    function toggleSwitch(label: string): HTMLButtonElement {
      const toggle = [...root().querySelectorAll('mat-slide-toggle')].find(
        (t) => t.textContent?.trim() === label,
      );
      return toggle?.querySelector('button[role="switch"]') as HTMLButtonElement;
    }

    it('saves the merge settings it loaded', async () => {
      const saved: ProjectSettingsDto[] = [];
      await createWith({ ...baseSettings, ...mergeSettings }, (_projectId, dto) => {
        saved.push(dto);
        return of(undefined);
      });

      submit();

      expect(saved).toHaveLength(1);
      expect(saved[0]).toMatchObject(mergeSettings);
    });

    it('saves an edited merge setting', async () => {
      const saved: ProjectSettingsDto[] = [];
      await createWith(baseSettings, (_projectId, dto) => {
        saved.push(dto);
        return of(undefined);
      });

      type('Max memories per merge', '2');
      type('Min. cluster similarity', '0.9');
      submit();

      expect(saved[0].onlineMergeMaxSources).toBe(2);
      expect(saved[0].reconsolidationClusterMinSimilarity).toBe(0.9);
    });

    it('refuses to save a merge setting outside its range', async () => {
      const saved: ProjectSettingsDto[] = [];
      await createWith(baseSettings, (_projectId, dto) => {
        saved.push(dto);
        return of(undefined);
      });

      type('Max memories per merge', '11');
      submit();

      const saveButton = root().querySelector('button[type="submit"]') as HTMLButtonElement;
      expect(saveButton.disabled).toBe(true);
      expect(saved).toHaveLength(0);
    });

    it('defaults the reconsolidation interval to 360 minutes when switched on', async () => {
      const saved: ProjectSettingsDto[] = [];
      await createWith(baseSettings, (_projectId, dto) => {
        saved.push(dto);
        return of(undefined);
      });

      toggleSwitch('Enable periodic memory reconsolidation').click();
      fixture.detectChanges();
      submit();

      expect(saved[0].memoryReconsolidationIntervalMinutes).toBe(360);
    });
  });
});
