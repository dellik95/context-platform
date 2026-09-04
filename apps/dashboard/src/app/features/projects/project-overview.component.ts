import { CommonModule } from '@angular/common';
import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ProjectContextService } from './project-context.service';

@Component({
  selector: 'app-project-overview',
  imports: [CommonModule, MatCardModule, MatButtonModule, MatIconModule, MatProgressBarModule],
  templateUrl: './project-overview.component.html',
  styleUrl: './project-overview.component.scss',
})
export class ProjectOverviewComponent {
  protected readonly ctx = inject(ProjectContextService);

  protected reindex(full: boolean): void {
    this.ctx.triggerIndex(full);
  }

  protected cancelIndex(): void {
    this.ctx.cancelIndex();
  }
}
