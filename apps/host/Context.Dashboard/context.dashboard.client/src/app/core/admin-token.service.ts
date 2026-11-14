import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, shareReplay, throwError } from 'rxjs';
import { environment } from '../../environments/environment';

/**
 * `GET /api/auth/admin-token` (see AdminTokenBootstrapEndpoints.cs) — a loopback-only, otherwise
 * unauthenticated bootstrap that hands this browser the admin token whenever the Dashboard and
 * Context.Dashboard.Server are running on the same machine. Caches successful/in-flight lookups via
 * `shareReplay(1)`, while clearing the cache on error so subsequent requests retry cleanly if the
 * host was temporarily unavailable.
 */
@Injectable({ providedIn: 'root' })
export class AdminTokenService {
  private readonly http = inject(HttpClient);
  private token$: Observable<{ token: string }> | null = null;

  getToken(): Observable<{ token: string }> {
    if (!this.token$) {
      this.token$ = this.http
        .get<{ token: string }>(`${environment.apiBaseUrl}/api/auth/admin-token`)
        .pipe(
          catchError((err) => {
            this.token$ = null;
            return throwError(() => err);
          }),
          shareReplay(1),
        );
    }
    return this.token$;
  }
}
