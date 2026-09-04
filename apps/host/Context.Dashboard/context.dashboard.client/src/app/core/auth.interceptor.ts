import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { ActivatedRouteSnapshot, Router } from '@angular/router';
import { switchMap } from 'rxjs';
import { AdminTokenService } from './admin-token.service';
import { ProjectTokenService } from './project-token.service';

/**
 * Attaches `Authorization: Bearer <token>` to every outgoing request. Two disjoint token kinds,
 * gated by two disjoint route shapes:
 *  - a project-scoped route (has a `:id` route param, e.g. `/projects/:id/...`) gets that
 *    project's own token (see ProjectTokenService) — Phase 3 replaced the single host-wide token
 *    with one bearer token per project.
 *  - an admin-gated request (`POST/GET /api/projects` — creating/listing projects, which by
 *    definition can't use a not-yet-existing project's own token; `GET /api/fs/browse` — the
 *    folder picker used to create that project in the first place; `POST /api/projects/{id}/token`
 *    — ProjectContextService.init's silent token-recovery call, which by definition runs
 *    precisely when this browser does NOT have that project's own token) gets the admin token
 *    instead, fetched once via AdminTokenService's loopback-only bootstrap (see its own remarks)
 *    so there's no login screen for a Dashboard running on the same machine as the host.
 *
 * The natural place to read "the current project id" from would be ProjectContextService, but
 * that's provided at ProjectDetailComponent's level (see its `providers` array), not root, so it
 * isn't reachable from here — a functional interceptor resolves its dependencies from the root
 * EnvironmentInjector, which has no visibility into a component-scoped provider. Router IS root-
 * provided, so this reads the same `:id` route param ProjectDetailComponent does, just off the
 * router snapshot instead.
 *
 * Any other route (no project id, not admin-gated) gets no Authorization header, and 401s same as
 * it would for any unauthenticated caller -- correct behavior for a shared host, not a bug to
 * route around.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (req.headers.has('Authorization')) {
    return next(req);
  }

  if (isAdminGated(req.url)) {
    const adminTokenService = inject(AdminTokenService);
    return adminTokenService
      .getToken()
      .pipe(switchMap(({ token }) => next(req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }))));
  }

  const router = inject(Router);
  const projectTokenService = inject(ProjectTokenService);
  const projectId = currentProjectId(router);
  const token = projectId ? projectTokenService.getToken(projectId) : null;

  return next(token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req);
};

/** `/api/projects` itself (list/create) and the token-recovery route — every other
 *  `/api/projects/{id}/...` route is project-scoped instead (see currentProjectId below). */
function isAdminGated(url: string): boolean {
  const path = new URL(url, window.location.origin).pathname;
  return path === '/api/projects' || path === '/api/fs/browse' || /^\/api\/projects\/[^/]+\/token$/.test(path);
}

function currentProjectId(router: Router): string | null {
  let route: ActivatedRouteSnapshot | null = router.routerState.snapshot.root;
  while (route) {
    const id = route.paramMap.get('id');
    if (id) {
      return id;
    }
    route = route.firstChild;
  }
  return null;
}
