import { ChangeDetectionStrategy, Component, computed, effect, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { map } from 'rxjs';
import { IconComponent, IconName } from '../../shared/ui/icon.component';
import { ProgressBarComponent } from '../../shared/ui/progress-bar.component';
import { ProjectContextService } from './project-context.service';

interface NavTab {
  route: string;
  label: string;
  icon: IconName;
}

const PROJECT_TABS: readonly NavTab[] = [
  { route: 'dashboard', label: 'Dashboard', icon: 'dashboard' },
  { route: 'search', label: 'Search', icon: 'search' },
  { route: 'memories', label: 'Memories', icon: 'psychology' },
  { route: 'sessions', label: 'Sessions', icon: 'history' },
  { route: 'graph', label: 'Graph', icon: 'account-tree' },
  { route: 'settings', label: 'Settings', icon: 'settings' },
  { route: 'usage', label: 'Usage', icon: 'monitoring' },
];

@Component({
  selector: 'app-project-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, IconComponent, ProgressBarComponent],
  providers: [ProjectContextService],
  templateUrl: './project-detail.component.html',
})
export class ProjectDetailComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  protected readonly ctx = inject(ProjectContextService);

  protected readonly tabs = PROJECT_TABS;

  protected readonly projectId = toSignal(this.route.paramMap.pipe(map((p) => p.get('id') ?? '')), {
    initialValue: '',
  });

  protected readonly busy = computed(() => this.router.currentNavigation() !== null || !this.ctx.projectId());

  constructor() {
    effect(() => {
      const id = this.projectId();
      if (id) {
        void this.ctx.init(id);
      }
    });
  }
}
