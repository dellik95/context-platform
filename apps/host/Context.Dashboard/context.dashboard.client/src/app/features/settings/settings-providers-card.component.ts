import { Component, computed, input, output } from '@angular/core';
import { FormField } from '@angular/forms/signals';
import { AlertBannerComponent } from '../../shared/ui/alert-banner.component';
import { SettingsFieldTree, SettingsFormModel } from './settings-form.model';

const CLOUD_PROVIDERS = new Set(['openai', 'anthropic']);

@Component({
  selector: 'app-settings-providers-card',
  standalone: true,
  imports: [FormField, AlertBannerComponent],
  template: `
    <section class="space-y-4 rounded-xl border border-slate-200 bg-white p-5 shadow-xs">
      <div>
        <h2 class="text-base font-semibold text-slate-900">Providers</h2>
      </div>

      <app-alert-banner tone="info">
        OpenAI/Anthropic keys and models are shared across all projects (set via environment variables); the provider
        choice and Ollama's/Foundry Local's own endpoint/models below are specific to this project and take effect
        immediately, no restart needed.
      </app-alert-banner>

      <div class="grid gap-4 sm:grid-cols-2">
        <label class="block">
          <span class="mb-1 block text-xs font-medium text-slate-700">Embedding provider</span>
          <select
            [value]="settingsForm().embeddingProvider().value()"
            (change)="onProviderChange('embeddingProvider', $event)"
            class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 focus:outline-none"
          >
            <option value="noop">noop</option>
            <option value="openai">openai</option>
            <option value="ollama">ollama</option>
            <option value="foundry">foundry (Foundry Local)</option>
          </select>
        </label>

        <label class="block">
          <span class="mb-1 block text-xs font-medium text-slate-700">LLM provider</span>
          <select
            [value]="settingsForm().llmProvider().value()"
            (change)="onProviderChange('llmProvider', $event)"
            class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 focus:outline-none"
          >
            <option value="noop">noop</option>
            <option value="anthropic">anthropic</option>
            <option value="ollama">ollama</option>
            <option value="foundry">foundry (Foundry Local)</option>
          </select>
        </label>
      </div>

      @if (showCloudDisclosure()) {
        <app-alert-banner tone="danger" class="provider-note--cloud-disclosure block">
          Indexed code and content will be sent to the selected external provider (OpenAI/Anthropic) for processing.
          Choose <strong>ollama</strong>, <strong>foundry</strong> or <strong>noop</strong> instead to keep
          everything on this machine.
        </app-alert-banner>
      }

      @if (showOllamaBaseUrl() || showOllamaEmbeddingModel() || showOllamaChatModel()) {
        <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          @if (showOllamaBaseUrl()) {
            <label class="block">
              <span class="mb-1 block text-xs font-medium text-slate-700">Ollama base URL</span>
              <input
                type="text"
                [formField]="settingsForm().ollamaBaseUrl"
                placeholder="http://localhost:11434"
                class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 placeholder:text-slate-400 focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 focus:outline-none"
              />
            </label>
          }
          @if (showOllamaEmbeddingModel()) {
            <label class="block">
              <span class="mb-1 block text-xs font-medium text-slate-700">Ollama embedding model</span>
              <input
                type="text"
                [formField]="settingsForm().ollamaEmbeddingModel"
                placeholder="e.g. embeddinggemma"
                class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 placeholder:text-slate-400 focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 focus:outline-none"
              />
            </label>
          }
          @if (showOllamaChatModel()) {
            <label class="block">
              <span class="mb-1 block text-xs font-medium text-slate-700">Ollama chat model</span>
              <input
                type="text"
                [formField]="settingsForm().ollamaChatModel"
                placeholder="e.g. qwen2.5-coder:1.5b"
                class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 placeholder:text-slate-400 focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 focus:outline-none"
              />
            </label>
          }
        </div>
      }

      @if (showFoundryBaseUrl()) {
        <app-alert-banner tone="info">
          Foundry Local only serves models that are loaded (<code>foundry model load &lt;alias&gt;</code>) and picks a
          new port on every start unless one is pinned (<code>foundry config set port 5273</code>).
        </app-alert-banner>

        <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          <label class="block">
            <span class="mb-1 block text-xs font-medium text-slate-700">Foundry Local base URL</span>
            <input
              type="text"
              [formField]="settingsForm().foundryBaseUrl"
              placeholder="http://127.0.0.1:5273/v1"
              class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 placeholder:text-slate-400 focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 focus:outline-none"
            />
          </label>
          @if (showFoundryEmbeddingModel()) {
            <label class="block">
              <span class="mb-1 block text-xs font-medium text-slate-700">Foundry embedding model</span>
              <input
                type="text"
                [formField]="settingsForm().foundryEmbeddingModel"
                placeholder="e.g. qwen3-embedding-0.6b"
                class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 placeholder:text-slate-400 focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 focus:outline-none"
              />
            </label>
          }
          @if (showFoundryChatModel()) {
            <label class="block">
              <span class="mb-1 block text-xs font-medium text-slate-700">Foundry chat model</span>
              <input
                type="text"
                [formField]="settingsForm().foundryChatModel"
                placeholder="e.g. phi-3.5-mini"
                class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 placeholder:text-slate-400 focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 focus:outline-none"
              />
            </label>
          }
        </div>
      }

      <div class="grid gap-4 sm:grid-cols-2">
        <label class="block">
          <span class="mb-1 block text-xs font-medium text-slate-700">Embedding batch size</span>
          <input
            type="number"
            [formField]="settingsForm().embeddingBatchSize"
            class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 focus:outline-none"
          />
          <span class="mt-1 block text-xs text-slate-500">
            Texts per embedding request: 0 = provider default, 1 = no batching
          </span>
        </label>
      </div>
    </section>
  `,
})
export class SettingsProvidersCardComponent {
  readonly model = input.required<SettingsFormModel>();
  readonly settingsForm = input.required<SettingsFieldTree>();

  readonly providerChanged = output<{ key: 'embeddingProvider' | 'llmProvider'; value: string }>();

  protected readonly showOllamaBaseUrl = computed(() => {
    const m = this.model();
    return m.embeddingProvider === 'ollama' || m.llmProvider === 'ollama';
  });
  protected readonly showOllamaEmbeddingModel = computed(() => this.model().embeddingProvider === 'ollama');
  protected readonly showOllamaChatModel = computed(() => this.model().llmProvider === 'ollama');
  protected readonly showFoundryBaseUrl = computed(() => {
    const m = this.model();
    return m.embeddingProvider === 'foundry' || m.llmProvider === 'foundry';
  });
  protected readonly showFoundryEmbeddingModel = computed(() => this.model().embeddingProvider === 'foundry');
  protected readonly showFoundryChatModel = computed(() => this.model().llmProvider === 'foundry');
  protected readonly showCloudDisclosure = computed(() => {
    const m = this.model();
    return CLOUD_PROVIDERS.has(m.embeddingProvider) || CLOUD_PROVIDERS.has(m.llmProvider);
  });

  protected onProviderChange(key: 'embeddingProvider' | 'llmProvider', event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.providerChanged.emit({ key, value });
  }
}
