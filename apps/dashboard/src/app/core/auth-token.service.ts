import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';

/**
 * Fetches and caches the bearer token every other Context.Host request now needs (see
 * BACKLOG-02/03 in docs/architecture/ARCHITECTURE_ASSESSMENT.md — CORS used to be wildcard +
 * credentials, which is what let ANY local tab call this API; now every route but /health and
 * this bootstrap endpoint itself requires the token).
 *
 * `GET /api/auth/token` (Context.Host/Api/BootstrapEndpoints.cs) is deliberately the one
 * unauthenticated REST endpoint: it's safe precisely because of the same-origin policy this fix
 * introduces — a cross-origin page's JS can't read the response unless the server's (now
 * restricted, dev-only) CORS policy explicitly allows that origin, and Production has no CORS
 * policy at all. Fetched once and cached for the app's lifetime; a token only ever changes by
 * re-running `context claude install`, which restarts this host anyway.
 */
@Injectable({ providedIn: 'root' })
export class AuthTokenService {
  private readonly http = inject(HttpClient);
  private tokenPromise: Promise<string | null> | null = null;

  getToken(): Promise<string | null> {
    this.tokenPromise ??= this.fetchToken();
    return this.tokenPromise;
  }

  private async fetchToken(): Promise<string | null> {
    try {
      const response = await firstValueFrom(
        this.http.get<{ token: string | null }>(`${environment.apiBaseUrl}/api/auth/token`),
      );
      return response.token ?? null;
    } catch {
      // No local config installed yet (context claude install never ran) — every other endpoint
      // will 401 too; surfacing that as a normal request failure is enough, no special handling.
      return null;
    }
  }
}
