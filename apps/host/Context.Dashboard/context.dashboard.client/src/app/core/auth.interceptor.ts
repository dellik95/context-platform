import { HttpErrorResponse, HttpEvent, HttpHandlerFn, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { ActivatedRouteSnapshot, Router } from '@angular/router';
import { Observable, catchError, finalize, shareReplay, switchMap, throwError } from 'rxjs';
import { AdminTokenService } from './admin-token.service';
import { ApiService } from './api.service';
import { ProjectTokenService } from './project-token.service';

const inFlightProjectTokenReissues = new Map<string, Observable<string>>();

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
 *
 * A project-scoped request can also 401 because the *stored* token has gone stale -- the server
 * rotated it (`POST /api/projects/{id}/token`, e.g. via `context claude install` re-run) while
 * this browser still has the old value cached. ProjectContextService.init's own recovery only
 * covers "no token at all"; it can't see a token that's present but wrong. See
 * `reissueAndRetry` below for the complementary fix: one silent reissue-and-replay per request.
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
  const apiService = inject(ApiService);
  const projectId = currentProjectId(router);
  const token = projectId ? projectTokenService.getToken(projectId) : null;
  const outgoing = token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req;

  return next(outgoing).pipe(
    catchError((error: unknown) => {
      if (!projectId || !(error instanceof HttpErrorResponse) || error.status !== 401) {
        return throwError(() => error);
      }
      return reissueAndRetry(apiService, projectTokenService, projectId, token, req, next, error);
    }),
  );
};

/**
 * Mints a fresh token for `projectId` (the same admin-gated `POST .../token` call
 * ProjectContextService.init uses, which loops back through this interceptor's admin-gated branch
 * for its own Authorization header), stashes it, and replays `req` once with it attached.
 * Concurrent 401s for the same project coalesce onto a single in-flight reissue call so rapid
 * parallel dashboard requests don't race and invalidate each other's rotated tokens.
 *
 * `originalError` is what propagates if the reissue call itself fails -- masking it behind a 403
 * from the token endpoint would hide the actual cause. The retried request is deliberately NOT
 * wrapped in another catchError: if it 401s again (project deleted, genuinely bad credentials
 * rather than a stale cache) that failure surfaces as-is instead of looping back into a second
 * reissue attempt.
 */
function reissueAndRetry(
  apiService: ApiService,
  projectTokenService: ProjectTokenService,
  projectId: string,
  failedToken: string | null,
  req: HttpRequest<unknown>,
  next: HttpHandlerFn,
  originalError: unknown,
): Observable<HttpEvent<unknown>> {
  const currentStoredToken = projectTokenService.getToken(projectId);
  if (currentStoredToken && currentStoredToken !== failedToken) {
    return next(req.clone({ setHeaders: { Authorization: `Bearer ${currentStoredToken}` } }));
  }

  let sharedReissue$ = inFlightProjectTokenReissues.get(projectId);
  if (!sharedReissue$) {
    sharedReissue$ = apiService.reissueToken(projectId).pipe(
      switchMap((dto) => {
        if (!dto.token) {
          return throwError(() => originalError);
        }
        projectTokenService.setToken(projectId, dto.token);
        return [dto.token];
      }),
      catchError(() => throwError(() => originalError)),
      finalize(() => inFlightProjectTokenReissues.delete(projectId)),
      shareReplay({ bufferSize: 1, refCount: true }),
    );
    inFlightProjectTokenReissues.set(projectId, sharedReissue$);
  }

  return sharedReissue$.pipe(
    catchError(() => throwError(() => originalError)),
    switchMap((freshToken) =>
      next(req.clone({ setHeaders: { Authorization: `Bearer ${freshToken}` } })),
    ),
  );
}

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
