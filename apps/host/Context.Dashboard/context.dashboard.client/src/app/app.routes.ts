import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'projects' },
  {
    path: 'projects',
    loadComponent: () => import('./features/projects/projects-list.component').then((m) => m.ProjectsListComponent),
  },
  {
    path: 'projects/:id',
    loadComponent: () => import('./features/projects/project-detail.component').then((m) => m.ProjectDetailComponent),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      {
        path: 'dashboard',
        loadComponent: () =>
          import('./features/dashboard/dashboard-panel.component').then((m) => m.DashboardPanelComponent),
      },
      {
        path: 'search',
        loadComponent: () => import('./features/search/search-panel.component').then((m) => m.SearchPanelComponent),
      },
      {
        path: 'memories',
        loadComponent: () =>
          import('./features/memories/memories-panel.component').then((m) => m.MemoriesPanelComponent),
      },
      {
        path: 'sessions',
        loadComponent: () =>
          import('./features/sessions/sessions-panel.component').then((m) => m.SessionsPanelComponent),
      },
      {
        path: 'graph',
        loadComponent: () =>
          import('./features/graph/graph-impact-panel.component').then((m) => m.GraphImpactPanelComponent),
      },
      {
        path: 'settings',
        loadComponent: () =>
          import('./features/settings/settings-panel.component').then((m) => m.SettingsPanelComponent),
      },
      {
        path: 'usage',
        loadComponent: () => import('./features/usage/usage-panel.component').then((m) => m.UsagePanelComponent),
      },
    ],
  },
  { path: '**', redirectTo: 'projects' },
];
