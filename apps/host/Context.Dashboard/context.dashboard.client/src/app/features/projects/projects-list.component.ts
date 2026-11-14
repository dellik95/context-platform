import { CommonModule } from '@angular/common';
import { Component, computed, effect, inject, signal } from '@angular/core';
import { FormField, form, readonly, required } from '@angular/forms/signals';
import { Router, RouterLink } from '@angular/router';
import { ApiService } from '../../core/api.service';
import { ProjectDto } from '../../core/models';
import { ProjectTokenService } from '../../core/project-token.service';
import { ConfirmDialogService } from '../../shared/confirm-dialog/confirm-dialog.service';
import { FolderPickerDialogComponent } from '../../shared/folder-picker/folder-picker-dialog.component';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { ProgressBarComponent } from '../../shared/ui/progress-bar.component';
import { SpinnerComponent } from '../../shared/ui/spinner.component';
import { ToastService } from '../../shared/ui/toast.service';
import { ProjectTokenDialogComponent, ProjectTokenDialogData } from './project-token-dialog.component';

@Component({
  selector: 'app-projects-list',
  imports: [
    CommonModule,
    RouterLink,
    FormField,
    FolderPickerDialogComponent,
    ProjectTokenDialogComponent,
    IconComponent,
    SpinnerComponent,
    ProgressBarComponent,
    EmptyStateComponent,
  ],
  templateUrl: './projects-list.component.html',
})
export class ProjectsListComponent {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly confirmDialog = inject(ConfirmDialogService);
  private readonly projectTokenService = inject(ProjectTokenService);

  private readonly resource = this.api.projectsResource();
  protected readonly projects = computed(() => (this.resource.hasValue() ? this.resource.value() : []));
  protected readonly loading = this.resource.isLoading;
  protected readonly creating = signal(false);
  protected readonly showCreateForm = signal(false);
  protected readonly showFolderPicker = signal(false);
  protected readonly tokenDialogData = signal<ProjectTokenDialogData | null>(null);
  protected readonly createdProjectId = signal<string | null>(null);
  protected readonly deletingId = signal<string | null>(null);

  protected readonly createModel = signal({ name: '', rootPath: '' });
  protected readonly createForm = form(this.createModel, (path) => {
    required(path.name);
    required(path.rootPath);
    readonly(path.rootPath);
  });

  constructor() {
    effect(() => {
      if (this.resource.error()) {
        this.toast.error('Could not reach Context.Dashboard.Server — is it running?');
      }
    });
  }

  protected browseForRootPath(): void {
    this.showFolderPicker.set(true);
  }

  protected onFolderSelected(selectedPath: string): void {
    this.showFolderPicker.set(false);
    this.createModel.update((current) => ({ ...current, rootPath: selectedPath }));
  }

  protected submitCreate(): void {
    if (this.createForm().invalid()) {
      return;
    }

    this.creating.set(true);
    const { name, rootPath } = this.createForm().value();
    this.api.createProject({ name, rootPath }).subscribe({
      next: (project) => {
        this.creating.set(false);
        this.showCreateForm.set(false);
        this.createModel.set({ name: '', rootPath: '' });

        if (project.warning) {
          this.toast.warning(project.warning, 20000);
        }

        if (project.token) {
          this.projectTokenService.setToken(project.id, project.token);
          const link = new URL(`/projects/${project.id}`, window.location.origin);
          link.searchParams.set('projectId', project.id);
          link.searchParams.set('token', project.token);

          this.createdProjectId.set(project.id);
          this.tokenDialogData.set({ projectName: project.name, link: link.toString() });
        } else {
          void this.router.navigate(['/projects', project.id]);
        }
      },
      error: (err) => {
        this.creating.set(false);
        const message = err?.status === 409 ? 'A project already exists at that path.' : 'Failed to create project.';
        this.toast.error(message);
      },
    });
  }

  protected onTokenDialogClosed(): void {
    const id = this.createdProjectId();
    this.tokenDialogData.set(null);
    this.createdProjectId.set(null);
    if (id) {
      void this.router.navigate(['/projects', id]);
    }
  }

  protected async deleteProject(project: ProjectDto, event: Event): Promise<void> {
    event.preventDefault();
    event.stopPropagation();

    const confirmed = await this.confirmDialog.open({
      title: 'Delete project',
      message: `Delete project "${project.name}"? This removes it from Context Platform (files, index, memories, sessions) without modifying files on disk.`,
      confirmLabel: 'Delete project',
      confirmTone: 'danger',
    });
    if (!confirmed) {
      return;
    }

    this.deletingId.set(project.id);
    this.api.deleteProject(project.id).subscribe({
      next: () => {
        this.deletingId.set(null);
        this.resource.update((current) => (current ?? []).filter((p) => p.id !== project.id));
        this.toast.success(`Deleted project "${project.name}".`, 3000);
      },
      error: () => {
        this.deletingId.set(null);
        this.toast.error(`Failed to delete project "${project.name}".`);
      },
    });
  }
}
