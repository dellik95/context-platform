import { Component, computed, effect, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../../core/api.service';
import { DirectoryEntryDto } from '../../core/models';

/**
 * Local-filesystem folder browser for "create project" — replaces having to type an absolute
 * path by hand. Backed by GET /api/fs/browse (Context.Host runs loopback-only, so exposing local
 * directory listing here is no bigger a trust boundary than the CLI already has).
 */
@Component({
  selector: 'app-folder-picker-dialog',
  imports: [MatDialogModule, MatButtonModule, MatIconModule, MatListModule, MatProgressBarModule],
  templateUrl: './folder-picker-dialog.component.html',
  styleUrl: './folder-picker-dialog.component.scss',
})
export class FolderPickerDialogComponent {
  private readonly api = inject(ApiService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly dialogRef = inject(MatDialogRef<FolderPickerDialogComponent, string>);

  private readonly requestedPath = signal<string | undefined>(undefined);
  private readonly resource = this.api.browseDirectoryResource(this.requestedPath);

  protected readonly currentPath = computed(() => (this.resource.hasValue() ? this.resource.value().path : ''));
  protected readonly parentPath = computed(() => (this.resource.hasValue() ? this.resource.value().parentPath : null));
  protected readonly directories = computed(() => (this.resource.hasValue() ? this.resource.value().directories : []));
  protected readonly isRoot = computed(() => !this.requestedPath());
  protected readonly loading = this.resource.isLoading;

  constructor() {
    effect(() => {
      if (this.resource.error()) {
        this.snackBar.open('Could not list that directory.', 'Dismiss');
      }
    });
  }

  protected navigateInto(dir: DirectoryEntryDto): void {
    this.requestedPath.set(dir.fullPath);
  }

  protected navigateUp(): void {
    // A drive root's parentPath is null — going "up" from there means back to the drive list.
    this.requestedPath.set(this.parentPath() ?? undefined);
  }

  protected selectCurrent(): void {
    if (this.currentPath()) {
      this.dialogRef.close(this.currentPath());
    }
  }

  protected cancel(): void {
    this.dialogRef.close(undefined);
  }
}
