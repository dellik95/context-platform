import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../../core/api.service';
import { ProjectSettingsDto } from '../../core/models';
import { ProjectContextService } from '../projects/project-context.service';
import { SettingsPanelComponent } from './settings-panel.component';

/**
 * BACKLOG-09 / SEC-5 coverage: the cloud data-use disclosure must render when (and only when) a
 * non-local provider (openai/anthropic) is selected in either provider dropdown, and stay hidden
 * for ollama/noop. `ApiService` and `ProjectContextService` are stubbed so this exercises only the
 * component's own rendering logic, not the real HTTP/SignalR wiring `ProjectContextService`
 * normally brings with it.
 */
describe('SettingsPanelComponent cloud provider disclosure', () => {
  let fixture: ComponentFixture<SettingsPanelComponent>;

  const baseSettings: ProjectSettingsDto = {
    embeddingProvider: 'noop',
    llmProvider: 'noop',
    ollamaBaseUrl: null,
    ollamaEmbeddingModel: null,
    ollamaChatModel: null,
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
    memoryCompactionIntervalMinutes: 0,
    lastMemoryCompactionScheduledAt: null,
    memoryCompactionSimilarityThreshold: 0.9,
    memoryConsolidationSimilarityThreshold: 0.92,
    memoryConsolidationLowSimilarityThreshold: 0.8,
    memoryConsolidationPromotionReinforcementThreshold: 2,
    memoryConsolidationTopKForDedup: 3,
  };

  function fakeSettingsResource(settings: ProjectSettingsDto) {
    return {
      isLoading: () => false,
      hasValue: () => true,
      value: () => settings,
      error: () => undefined,
    };
  }

  async function createWith(settings: ProjectSettingsDto): Promise<void> {
    const apiServiceStub: Partial<ApiService> = {
      settingsResource: () => fakeSettingsResource(settings) as unknown as ReturnType<ApiService['settingsResource']>,
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
});
