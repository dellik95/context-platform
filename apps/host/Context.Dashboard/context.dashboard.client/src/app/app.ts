import { Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiService } from './core/api.service';
import { HealthResponse } from './core/models';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, MatToolbarModule, MatIconModule, MatTooltipModule],
  templateUrl: './app.html',
  styleUrl: './app.scss',
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
