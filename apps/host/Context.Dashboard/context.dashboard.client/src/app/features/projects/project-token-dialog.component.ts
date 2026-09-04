import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar } from '@angular/material/snack-bar';

export interface ProjectTokenDialogData {
  projectName: string;
  link: string;
}

/**
 * Shown once, right after a project is created: its bearer token (baked into `link` as a query
 * param) can never be recovered again once this closes -- ProjectDto.Token is only ever non-null
 * in the CreateAsync response. This tab already has the token stashed (see
 * projects-list.component.ts), so this dialog exists purely so the link can be bookmarked or
 * handed to another browser/device.
 */
@Component({
  selector: 'app-project-token-dialog',
  imports: [MatDialogModule, MatButtonModule, MatIconModule, MatFormFieldModule, MatInputModule],
  templateUrl: './project-token-dialog.component.html',
  styleUrl: './project-token-dialog.component.scss',
})
export class ProjectTokenDialogComponent {
  protected readonly data = inject<ProjectTokenDialogData>(MAT_DIALOG_DATA);

  private readonly dialogRef = inject(MatDialogRef<ProjectTokenDialogComponent>);
  private readonly snackBar = inject(MatSnackBar);

  protected async copyLink(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.data.link);
      this.snackBar.open('Link copied.', 'Dismiss', { duration: 3000 });
    } catch {
      this.snackBar.open('Could not copy automatically -- select and copy the link manually.', 'Dismiss');
    }
  }

  protected done(): void {
    this.dialogRef.close();
  }
}
