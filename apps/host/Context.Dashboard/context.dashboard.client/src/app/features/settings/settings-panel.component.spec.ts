import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { of } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { ProjectSettingsDto } from '../../core/models';
import { ConfirmDialogService } from '../../shared/confirm-dialog/confirm-dialog.service';
import { ToastService } from '../../shared/ui/toast.service';
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
        { provide: ToastService, useValue: { success: vi.fn(), error: vi.fn(), warning: vi.fn(), show: vi.fn() } },
        { provide: Router, useValue: { navigate: () => Promise.resolve(true) } },
        { provide: ConfirmDialogService, useValue: { open: vi.fn().mockResolvedValue(true) } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(SettingsPanelComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

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
      const container = [...root().querySelectorAll('label')].find(
        (l) => l.querySelector('span')?.textContent?.trim() === label,
      );
      return container?.querySelector('input') as HTMLInputElement;
    }

    function type(label: string, value: string): void {
      const input = fieldInput(label);
      input.value = value;
      input.dispatchEvent(new Event('input'));
      fixture.detectChanges();
    }

    function toggleSwitch(label: string): HTMLButtonElement {
      const toggle = [...root().querySelectorAll('.settings-toggle')].find(
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

    it('preserves unsaved local edits when the same project settings resource reloads', async () => {
      const resourceValue = { ...baseSettings };
      let version = 0;
      const versionSignal = (await import('@angular/core')).signal(0);
      const projectSignal = (await import('@angular/core')).signal('proj-1');
      const apiStub: Partial<ApiService> = {
        settingsResource: () =>
          ({
            isLoading: () => false,
            hasValue: () => true,
            value: () => {
              versionSignal();
              return { ...resourceValue, reindexIntervalMinutes: 60 + version };
            },
            error: () => undefined,
          }) as unknown as ReturnType<ApiService['settingsResource']>,
        updateSettings: () => of(undefined),
      };

      await TestBed.configureTestingModule({
        imports: [SettingsPanelComponent],
        providers: [
          { provide: ApiService, useValue: apiStub },
          { provide: ProjectContextService, useValue: { projectId: projectSignal, projectName: () => 'Test' } },
          { provide: ToastService, useValue: { success: vi.fn(), error: vi.fn(), warning: vi.fn(), show: vi.fn() } },
          { provide: Router, useValue: { navigate: () => Promise.resolve(true) } },
          { provide: ConfirmDialogService, useValue: { open: vi.fn().mockResolvedValue(true) } },
        ],
      }).compileComponents();

      fixture = TestBed.createComponent(SettingsPanelComponent);
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();

      type('Max memories per merge', '2');
      version = 1;
      versionSignal.set(1);
      fixture.detectChanges();

      expect(fieldInput('Max memories per merge').value).toBe('2');

      projectSignal.set('proj-2');
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();

      expect(fieldInput('Max memories per merge').value).toBe(String(baseSettings.onlineMergeMaxSources));
    });
  });
});
