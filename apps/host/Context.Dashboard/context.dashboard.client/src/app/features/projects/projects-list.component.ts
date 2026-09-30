import { CommonModule } from '@angular/common';
import { Component, computed, effect, inject, signal } from '@angular/core';
import { FormField, form, readonly, required } from '@angular/forms/signals';
import { Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatListModule } from '@angular/material/list';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiService } from '../../core/api.service';
import { ProjectDto } from '../../core/models';
import { ProjectTokenService } from '../../core/project-token.service';
import { FolderPickerDialogComponent } from '../../shared/folder-picker/folder-picker-dialog.component';
import { ProjectTokenDialogComponent } from './project-token-dialog.component';

@Component({
  selector: 'app-projects-list',
  imports: [
    CommonModule,
    RouterLink,
    FormField,
    MatCardModule,
    MatListModule,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    MatProgressSpinnerModule,
    MatDialogModule,
    MatTooltipModule,
  ],
  templateUrl: './projects-list.component.html',
  styleUrl: './projects-list.component.scss',
})
export class ProjectsListComponent {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly snackBar = inject(MatSnackBar);
  private readonly dialog = inject(MatDialog);
  private readonly projectTokenService = inject(ProjectTokenService);

  private readonly resource = this.api.projectsResource();
  protected readonly projects = computed(() => (this.resource.hasValue() ? this.resource.value() : []));
  protected readonly loading = this.resource.isLoading;
  protected readonly creating = signal(false);
  protected readonly showCreateForm = signal(false);
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
        this.snackBar.open('Could not reach Context.Host — is it running?', 'Dismiss');
      }
    });
  }

  protected browseForRootPath(): void {
    this.dialog
      .open(FolderPickerDialogComponent, { width: '520px' })
      .afterClosed()
      .subscribe((selectedPath?: string) => {
        if (selectedPath) {
          this.createModel.update((current) => ({ ...current, rootPath: selectedPath }));
        }
      });
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

        if (project.token) {
          // Stash it locally so this tab can use the project right away, and show the
          // one-time deep link so it can be bookmarked or handed to another browser/device --
          // the raw token can never be retrieved again after this.
          this.projectTokenService.setToken(project.id, project.token);
          const link = new URL(`/projects/${project.id}`, window.location.origin);
          link.searchParams.set('projectId', project.id);
          link.searchParams.set('token', project.token);

          this.dialog
            .open(ProjectTokenDialogComponent, {
              width: '520px',
              data: { projectName: project.name, link: link.toString() },
              disableClose: true,
            })
            .afterClosed()
            .subscribe(() => this.router.navigate(['/projects', project.id]));
        } else {
          this.router.navigate(['/projects', project.id]);
        }
      },
      error: (err) => {
        this.creating.set(false);
        const message = err?.status === 409 ? 'A project already exists at that path.' : 'Failed to create project.';
        this.snackBar.open(message, 'Dismiss');
      },
    });
  }

  /** Row is a routerLink `<a>`, so the click needs to stop navigation as well as propagation. */
  protected deleteProject(project: ProjectDto, event: Event): void {
    event.preventDefault();
    event.stopPropagation();

    if (
      !confirm(
        `Delete project "${project.name}"? This removes it from Context Platform (files, index, memories, sessions) but does not touch its files on disk.`,
      )
    ) {
      return;
    }

    this.deletingId.set(project.id);
    this.api.deleteProject(project.id).subscribe({
      next: () => {
        this.deletingId.set(null);
        this.resource.update((current) => (current ?? []).filter((p) => p.id !== project.id));
        this.snackBar.open(`Deleted project "${project.name}".`, 'Dismiss', { duration: 3000 });
      },
      error: () => {
        this.deletingId.set(null);
        this.snackBar.open(`Failed to delete project "${project.name}".`, 'Dismiss');
      },
    });
  }
}
