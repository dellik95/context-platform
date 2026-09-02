import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { from, switchMap } from 'rxjs';
import { AuthTokenService } from './auth-token.service';

/**
 * Attaches `Authorization: Bearer <token>` to every outgoing request. Skips the bootstrap
 * endpoint itself (`/api/auth/token`) to avoid recursively fetching the token in order to fetch
 * the token, and skips anything already carrying an Authorization header (defensive — nothing in
 * this app sets one manually today, but a future call site doing its own auth shouldn't be
 * silently overwritten).
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (req.url.endsWith('/api/auth/token') || req.headers.has('Authorization')) {
    return next(req);
  }

  const authTokenService = inject(AuthTokenService);
  return from(authTokenService.getToken()).pipe(
    switchMap((token) => next(token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req)),
  );
};
