import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, shareReplay } from 'rxjs';
import { environment } from '../../environments/environment';

/**
 * `GET /api/auth/admin-token` (see AdminTokenBootstrapEndpoints.cs) — a loopback-only, otherwise
 * unauthenticated bootstrap that hands this browser the admin token whenever the Dashboard and
 * Context.Dashboard.Server are running on the same machine, so `POST/GET /api/projects` and
 * `GET /api/fs/browse` (see auth.interceptor.ts) work with no login screen. `shareReplay(1)` caches
 * the one in-flight/completed request so every admin-gated call this tab makes shares it instead of
 * re-fetching; a 403 (non-loopback host, or no token configured there — see AdminTokenProvider)
 * just propagates to whatever admin-gated call triggered the fetch, same as any other 401/403.
 */
@Injectable({ providedIn: 'root' })
export class AdminTokenService {
  private readonly http = inject(HttpClient);
  private readonly token$ = this.http
    .get<{ token: string }>(`${environment.apiBaseUrl}/api/auth/admin-token`)
    .pipe(shareReplay(1));

  getToken(): Observable<{ token: string }> {
    return this.token$;
  }
}
