import { Component, inject, signal } from '@angular/core';
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

  protected readonly currentPath = signal('');
  protected readonly parentPath = signal<string | null>(null);
  protected readonly directories = signal<DirectoryEntryDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly isRoot = signal(true);

  constructor() {
    this.load(undefined);
  }

  private load(path: string | undefined): void {
    this.loading.set(true);
    this.api.browseDirectory(path).subscribe({
      next: (res) => {
        this.currentPath.set(res.path);
        this.parentPath.set(res.parentPath);
        this.directories.set(res.directories);
        this.isRoot.set(!path);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.snackBar.open('Could not list that directory.', 'Dismiss');
      },
    });
  }

  protected navigateInto(dir: DirectoryEntryDto): void {
    this.load(dir.fullPath);
  }

  protected navigateUp(): void {
    // A drive root's parentPath is null — going "up" from there means back to the drive list.
    this.load(this.parentPath() ?? undefined);
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
