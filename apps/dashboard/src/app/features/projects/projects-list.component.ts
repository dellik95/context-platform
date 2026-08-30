import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
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
import { ProjectDto } from '../../core/models';
import { FolderPickerDialogComponent } from '../../shared/folder-picker/folder-picker-dialog.component';

@Component({
  selector: 'app-projects-list',
  imports: [
    CommonModule,
    RouterLink,
    ReactiveFormsModule,
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
  private readonly fb = inject(FormBuilder);
  private readonly snackBar = inject(MatSnackBar);
  private readonly dialog = inject(MatDialog);

  protected readonly projects = signal<ProjectDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly creating = signal(false);
  protected readonly showCreateForm = signal(false);

  protected readonly createForm = this.fb.nonNullable.group({
    name: ['', Validators.required],
    rootPath: ['', Validators.required],
  });

  constructor() {
    this.reload();
  }

  private reload(): void {
    this.loading.set(true);
    this.api.listProjects().subscribe({
      next: (projects) => {
        this.projects.set(projects);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.snackBar.open('Could not reach Context.Host — is it running?', 'Dismiss');
      },
    });
  }

  protected browseForRootPath(): void {
    this.dialog
      .open(FolderPickerDialogComponent, { width: '520px' })
      .afterClosed()
      .subscribe((selectedPath?: string) => {
        if (selectedPath) {
          this.createForm.patchValue({ rootPath: selectedPath });
        }
      });
  }

  protected submitCreate(): void {
    if (this.createForm.invalid) {
      return;
    }

    this.creating.set(true);
    const { name, rootPath } = this.createForm.getRawValue();
    this.api.createProject({ name, rootPath }).subscribe({
      next: (project) => {
        this.creating.set(false);
        this.showCreateForm.set(false);
        this.createForm.reset();
        this.router.navigate(['/projects', project.id]);
      },
      error: (err) => {
        this.creating.set(false);
        const message = err?.status === 409 ? 'A project already exists at that path.' : 'Failed to create project.';
        this.snackBar.open(message, 'Dismiss');
      },
    });
  }

  protected open(project: ProjectDto): void {
    this.router.navigate(['/projects', project.id]);
  }
}
