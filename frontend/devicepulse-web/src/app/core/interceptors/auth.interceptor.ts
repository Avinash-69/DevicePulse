import {
  HttpErrorResponse,
  HttpEvent,
  HttpHandlerFn,
  HttpInterceptorFn,
  HttpRequest,
} from '@angular/common/http';
import { inject } from '@angular/core';
import { Observable, catchError, switchMap, throwError } from 'rxjs';

import { environment } from '../../../environments/environment';
import { AuthService } from '../auth/auth.service';

/**
 * Attaches the bearer token and, on a 401, refreshes once and retries.
 *
 * The retry matters because access tokens are short-lived by design (permissions are baked
 * into them, so a short life bounds how stale they can be). Without this, a user working
 * through a long session would be bounced to the login screen every fifteen minutes.
 */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(AuthService);

  // Only requests to our own API are touched. Adding the token to a third-party URL would leak
  // the credential to whoever that host is.
  if (!request.url.startsWith(environment.apiBaseUrl)) {
    return next(request);
  }

  // The auth endpoints either need no token or carry their own; sending an expired access
  // token to /auth/refresh would make a refresh fail and then try to refresh again.
  const isAuthEndpoint =
    request.url.includes('/auth/login') ||
    request.url.includes('/auth/refresh') ||
    request.url.includes('/auth/register') ||
    request.url.includes('/auth/logout');

  const authorized = isAuthEndpoint ? request : withToken(request, auth.accessToken);

  return next(authorized).pipe(
    catchError((error: unknown) => {
      if (!(error instanceof HttpErrorResponse) || error.status !== 401 || isAuthEndpoint) {
        return throwError(() => error);
      }

      return retryAfterRefresh(request, next, auth, error);
    }),
  );
};

function retryAfterRefresh(
  request: HttpRequest<unknown>,
  next: HttpHandlerFn,
  auth: AuthService,
  originalError: HttpErrorResponse,
): Observable<HttpEvent<unknown>> {
  return auth.refreshAccessToken().pipe(
    switchMap((token) => {
      if (!token) {
        // The refresh token is gone or rejected, so the session is genuinely over.
        // AuthService has already cleared it; the guard will redirect on the next navigation.
        auth.logout();
        return throwError(() => originalError);
      }

      return next(withToken(request, token));
    }),
  );
}

function withToken(request: HttpRequest<unknown>, token: string | null): HttpRequest<unknown> {
  if (!token) {
    return request;
  }

  return request.clone({ setHeaders: { Authorization: `Bearer ${token}` } });
}
