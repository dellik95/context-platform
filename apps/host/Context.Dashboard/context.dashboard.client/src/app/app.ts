import { Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';
import { ApiService } from './core/api.service';
import { HealthResponse } from './core/models';
import { ConfirmDialogHostComponent } from './shared/confirm-dialog/confirm-dialog.component';
import { IconComponent } from './shared/ui/icon.component';
import { ToastContainerComponent } from './shared/ui/toast-container.component';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, IconComponent, ToastContainerComponent, ConfirmDialogHostComponent],
  templateUrl: './app.html',
})
export class App implements OnInit {
  private readonly api = inject(ApiService);

  protected readonly health = signal<HealthResponse | null>(null);
  protected readonly healthError = signal(false);

  ngOnInit(): void {
    this.api.health().subscribe({
      next: (h) => this.health.set(h),
      error: () => this.healthError.set(true),
    });
  }
}
