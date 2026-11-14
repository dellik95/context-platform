import { SchemaPathTree, form, max, min } from '@angular/forms/signals';
import { WritableSignal } from '@angular/core';
import { ProjectSettingsDto } from '../../core/models';

export interface SettingsFormModel {
  embeddingProvider: string;
  llmProvider: string;
  ollamaBaseUrl: string;
  ollamaEmbeddingModel: string;
  ollamaChatModel: string;
  foundryBaseUrl: string;
  foundryEmbeddingModel: string;
  foundryChatModel: string;
  embeddingBatchSize: number;
  reindexEnabled: boolean;
  reindexIntervalMinutes: number;
  sessionSummaryEnabled: boolean;
  sessionSummaryIntervalMinutes: number;
  minEventsForSessionSummary: number;
  maxMemoryNotesPerSection: number;
  minEventsForMemoryExtraction: number;
  explicitRememberEnabled: boolean;
  staleSessionCleanupEnabled: boolean;
  staleSessionTimeoutMinutes: number;
  deleteSessionEventsAfterSummary: boolean;
  hookInvocationRetentionEnabled: boolean;
  hookInvocationRetentionDays: number;
  memoryReconsolidationEnabled: boolean;
  memoryReconsolidationIntervalMinutes: number;
  onlineMergeEnabled: boolean;
  onlineMergeMaxSources: number;
  mergedMemoryMaxBodyChars: number;
  mergedMemoryMaxFacts: number;
  reconsolidationMaxGroupSize: number;
  reconsolidationClusterMinSimilarity: number;
  reconsolidationSourceExcerptChars: number;
  llmTimeoutSeconds: number;
  llmRetryEnabled: boolean;
  llmMaxRetries: number;
  llmRetryBaseDelaySeconds: number;
  rateLimitCooldownMinutes: number;
  rateLimitMaxCooldownMinutes: number;
}

export type BooleanSettingKey = {
  [K in keyof SettingsFormModel]: SettingsFormModel[K] extends boolean ? K : never;
}[keyof SettingsFormModel];

export function createDefaultSettingsModel(): SettingsFormModel {
  return {
    embeddingProvider: 'noop',
    llmProvider: 'noop',
    ollamaBaseUrl: '',
    ollamaEmbeddingModel: '',
    ollamaChatModel: '',
    foundryBaseUrl: '',
    foundryEmbeddingModel: '',
    foundryChatModel: '',
    embeddingBatchSize: 0,
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
    memoryReconsolidationIntervalMinutes: 360,
    onlineMergeEnabled: true,
    onlineMergeMaxSources: 5,
    mergedMemoryMaxBodyChars: 8000,
    mergedMemoryMaxFacts: 40,
    reconsolidationMaxGroupSize: 12,
    reconsolidationClusterMinSimilarity: 0.6,
    reconsolidationSourceExcerptChars: 3000,
    llmTimeoutSeconds: 1200,
    llmRetryEnabled: false,
    llmMaxRetries: 2,
    llmRetryBaseDelaySeconds: 30,
    rateLimitCooldownMinutes: 5,
    rateLimitMaxCooldownMinutes: 60,
  };
}

export function createSettingsForm(model: WritableSignal<SettingsFormModel>) {
  return form(model, (path: SchemaPathTree<SettingsFormModel>) => {
    min(path.embeddingBatchSize, 0);
    max(path.embeddingBatchSize, 256);
    min(path.reindexIntervalMinutes, 1);
    min(path.sessionSummaryIntervalMinutes, 1);
    min(path.minEventsForSessionSummary, 1);
    min(path.maxMemoryNotesPerSection, 1);
    min(path.minEventsForMemoryExtraction, 1);
    min(path.staleSessionTimeoutMinutes, 1);
    min(path.hookInvocationRetentionDays, 1);
    min(path.memoryReconsolidationIntervalMinutes, 1);
    min(path.onlineMergeMaxSources, 2);
    max(path.onlineMergeMaxSources, 10);
    min(path.mergedMemoryMaxBodyChars, 1000);
    max(path.mergedMemoryMaxBodyChars, 32000);
    min(path.mergedMemoryMaxFacts, 5);
    max(path.mergedMemoryMaxFacts, 100);
    min(path.reconsolidationMaxGroupSize, 2);
    max(path.reconsolidationMaxGroupSize, 50);
    min(path.reconsolidationClusterMinSimilarity, 0.3);
    max(path.reconsolidationClusterMinSimilarity, 0.95);
    min(path.reconsolidationSourceExcerptChars, 500);
    max(path.reconsolidationSourceExcerptChars, 16000);
    min(path.llmTimeoutSeconds, 1);
    min(path.llmMaxRetries, 1);
    min(path.llmRetryBaseDelaySeconds, 1);
    min(path.rateLimitCooldownMinutes, 1);
    min(path.rateLimitMaxCooldownMinutes, ({ valueOf }) => valueOf(path.rateLimitCooldownMinutes));
  });
}

export type SettingsFieldTree = ReturnType<typeof createSettingsForm>;

export function mapDtoToFormModel(settings: ProjectSettingsDto): SettingsFormModel {
  return {
    embeddingProvider: settings.embeddingProvider,
    llmProvider: settings.llmProvider,
    ollamaBaseUrl: settings.ollamaBaseUrl ?? 'http://localhost:11434',
    ollamaEmbeddingModel: settings.ollamaEmbeddingModel ?? 'embeddinggemma',
    ollamaChatModel: settings.ollamaChatModel ?? 'qwen2.5-coder:1.5b',
    foundryBaseUrl: settings.foundryBaseUrl ?? '',
    foundryEmbeddingModel: settings.foundryEmbeddingModel ?? '',
    foundryChatModel: settings.foundryChatModel ?? '',
    embeddingBatchSize: settings.embeddingBatchSize,
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
        : 360,
    onlineMergeEnabled: settings.onlineMergeEnabled,
    onlineMergeMaxSources: settings.onlineMergeMaxSources,
    mergedMemoryMaxBodyChars: settings.mergedMemoryMaxBodyChars,
    mergedMemoryMaxFacts: settings.mergedMemoryMaxFacts,
    reconsolidationMaxGroupSize: settings.reconsolidationMaxGroupSize,
    reconsolidationClusterMinSimilarity: settings.reconsolidationClusterMinSimilarity,
    reconsolidationSourceExcerptChars: settings.reconsolidationSourceExcerptChars,
    llmTimeoutSeconds: settings.llmTimeoutSeconds,
    llmRetryEnabled: settings.llmMaxRetries > 0,
    llmMaxRetries: settings.llmMaxRetries > 0 ? settings.llmMaxRetries : 2,
    llmRetryBaseDelaySeconds: settings.llmRetryBaseDelaySeconds,
    rateLimitCooldownMinutes: settings.rateLimitCooldownMinutes,
    rateLimitMaxCooldownMinutes: settings.rateLimitMaxCooldownMinutes,
  };
}

export function mapFormModelToDto(
  raw: SettingsFormModel,
  lastReindexScheduledAt: string | null,
  lastMemoryReconsolidationScheduledAt: string | null,
): ProjectSettingsDto {
  return {
    embeddingProvider: raw.embeddingProvider,
    llmProvider: raw.llmProvider,
    ollamaBaseUrl: raw.ollamaBaseUrl || null,
    ollamaEmbeddingModel: raw.ollamaEmbeddingModel || null,
    ollamaChatModel: raw.ollamaChatModel || null,
    foundryBaseUrl: raw.foundryBaseUrl || null,
    foundryEmbeddingModel: raw.foundryEmbeddingModel || null,
    foundryChatModel: raw.foundryChatModel || null,
    embeddingBatchSize: raw.embeddingBatchSize,
    reindexEnabled: raw.reindexEnabled,
    reindexIntervalMinutes: raw.reindexIntervalMinutes,
    lastReindexScheduledAt,
    sessionSummaryIntervalMinutes: raw.sessionSummaryEnabled ? raw.sessionSummaryIntervalMinutes : 0,
    minEventsForSessionSummary: raw.minEventsForSessionSummary,
    maxMemoryNotesPerSection: raw.maxMemoryNotesPerSection,
    minEventsForMemoryExtraction: raw.minEventsForMemoryExtraction,
    explicitRememberEnabled: raw.explicitRememberEnabled,
    staleSessionTimeoutMinutes: raw.staleSessionCleanupEnabled ? raw.staleSessionTimeoutMinutes : 0,
    deleteSessionEventsAfterSummary: raw.deleteSessionEventsAfterSummary,
    hookInvocationRetentionDays: raw.hookInvocationRetentionEnabled
      ? raw.hookInvocationRetentionDays
      : 0,
    memoryReconsolidationIntervalMinutes: raw.memoryReconsolidationEnabled
      ? raw.memoryReconsolidationIntervalMinutes
      : 0,
    lastMemoryReconsolidationScheduledAt,
    onlineMergeEnabled: raw.onlineMergeEnabled,
    onlineMergeMaxSources: raw.onlineMergeMaxSources,
    mergedMemoryMaxBodyChars: raw.mergedMemoryMaxBodyChars,
    mergedMemoryMaxFacts: raw.mergedMemoryMaxFacts,
    reconsolidationMaxGroupSize: raw.reconsolidationMaxGroupSize,
    reconsolidationClusterMinSimilarity: raw.reconsolidationClusterMinSimilarity,
    reconsolidationSourceExcerptChars: raw.reconsolidationSourceExcerptChars,
    llmTimeoutSeconds: raw.llmTimeoutSeconds,
    llmMaxRetries: raw.llmRetryEnabled ? raw.llmMaxRetries : 0,
    llmRetryBaseDelaySeconds: raw.llmRetryBaseDelaySeconds,
    rateLimitCooldownMinutes: raw.rateLimitCooldownMinutes,
    rateLimitMaxCooldownMinutes: raw.rateLimitMaxCooldownMinutes,
  };
}
