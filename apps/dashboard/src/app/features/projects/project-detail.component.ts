import { Component, effect, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { map } from 'rxjs';
import { MatIconModule } from '@angular/material/icon';
import { MatTabsModule } from '@angular/material/tabs';
import { ProjectContextService } from './project-context.service';

@Component({
  selector: 'app-project-detail',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatIconModule, MatTabsModule],
  providers: [ProjectContextService],
  templateUrl: './project-detail.component.html',
  styleUrl: './project-detail.component.scss',
})
export class ProjectDetailComponent {
  private readonly route = inject(ActivatedRoute);
  protected readonly ctx = inject(ProjectContextService);

  // A route param, not a route reload, changes when jumping between two projects' detail pages
  // (same routed component instance) — react to every value, not just the first one, or the
  // context would keep pointing at whichever project was opened first.
  protected readonly projectId = toSignal(this.route.paramMap.pipe(map((p) => p.get('id') ?? '')), {
    initialValue: '',
  });

  constructor() {
    effect(() => {
      const id = this.projectId();
      if (id) {
        this.ctx.init(id);
      }
    });
  }
}
