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
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../../core/api.service';
import { FolderPickerDialogComponent } from '../../shared/folder-picker/folder-picker-dialog.component';

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
    MatDialogModule,
  ],
  templateUrl: './projects-list.component.html',
  styleUrl: './projects-list.component.scss',
})
export class ProjectsListComponent {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly snackBar = inject(MatSnackBar);
  private readonly dialog = inject(MatDialog);

  private readonly resource = this.api.projectsResource();
  protected readonly projects = computed(() => (this.resource.hasValue() ? this.resource.value() : []));
  protected readonly loading = this.resource.isLoading;
  protected readonly creating = signal(false);
  protected readonly showCreateForm = signal(false);

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
        this.router.navigate(['/projects', project.id]);
      },
      error: (err) => {
        this.creating.set(false);
        const message = err?.status === 409 ? 'A project already exists at that path.' : 'Failed to create project.';
        this.snackBar.open(message, 'Dismiss');
      },
    });
  }
}
