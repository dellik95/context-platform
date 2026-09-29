import { Component, computed, effect, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../../core/api.service';

/** Host-wide log tail (GET /api/logs/tail), fetched when the dialog opens. */
@Component({
  selector: 'app-logs-dialog',
  imports: [MatButtonModule, MatDialogModule, MatIconModule, MatProgressBarModule],
  templateUrl: './logs-dialog.component.html',
  styleUrl: './logs-dialog.component.scss',
})
export class LogsDialogComponent {
  private readonly api = inject(ApiService);
  private readonly snackBar = inject(MatSnackBar);

  private readonly resource = this.api.logTailResource();
  protected readonly lines = computed(() => (this.resource.hasValue() ? this.resource.value().lines : null));
  protected readonly filePath = computed(() => (this.resource.hasValue() ? this.resource.value().filePath : null));
  protected readonly loading = this.resource.isLoading;
  // A Refresh keeps the previous tail visible instead of blanking it while the new one loads.
  protected readonly initialLoading = computed(() => this.loading() && !this.resource.hasValue());

  constructor() {
    effect(() => {
      if (this.resource.error()) {
        this.snackBar.open('Failed to load log tail — is Context.Host running?', 'Dismiss');
      }
    });
  }

  protected refresh(): void {
    this.resource.reload();
  }
}
