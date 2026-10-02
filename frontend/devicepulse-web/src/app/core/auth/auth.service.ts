import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, tap, catchError, of, map, shareReplay } from 'rxjs';

import { environment } from '../../../environments/environment';
import { AuthResponse, ChangePasswordRequest, CurrentUser, LoginRequest } from '../models/api.models';

const ACCESS_TOKEN_KEY = 'devicepulse.accessToken';
const REFRESH_TOKEN_KEY = 'devicepulse.refreshToken';
const USER_KEY = 'devicepulse.user';

/**
 * Owns the client's session: the tokens, the signed-in user, and the permission set used to
 * decide what to render.
 *
 * The permission list here is a convenience for the UI and nothing more. The API re-verifies
 * every permission on every request, so editing localStorage changes which menu items appear
 * and achieves nothing else (§4.4, §29 of the master reference).
 *
 * Tokens live in localStorage, which is a deliberate and bounded trade-off: it survives a page
 * refresh, and the alternative (an httpOnly cookie) would require the API to adopt cookie auth
 * plus CSRF protection. localStorage is readable by any script on the origin, so it is only
 * acceptable while the app ships no third-party scripts and the access token is short-lived.
 * Moving to httpOnly cookies is the right step when identity moves to Keycloak (Phase 2, §5).
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);

  private readonly currentUser = signal<CurrentUser | null>(this.readStoredUser());

  /** The signed-in user, or null. */
  readonly user = this.currentUser.asReadonly();

  readonly isAuthenticated = computed(() => this.currentUser() !== null);

  private readonly permissionSet = computed(() => new Set(this.currentUser()?.permissions ?? []));

  /** In-flight refresh, shared so several 401s cannot trigger several refreshes. */
  private refreshInFlight$: Observable<string | null> | null = null;

  login(request: LoginRequest): Observable<AuthResponse> {
    return this.http
      .post<AuthResponse>(`${environment.apiBaseUrl}/auth/login`, request)
      .pipe(tap((response) => this.storeSession(response)));
  }

  register(name: string, email: string, password: string): Observable<AuthResponse> {
    return this.http
      .post<AuthResponse>(`${environment.apiBaseUrl}/auth/register`, { name, email, password })
      .pipe(tap((response) => this.storeSession(response)));
  }

  changePassword(request: ChangePasswordRequest): Observable<void> {
    // The API ends every session on a password change, so the local one is cleared too rather
    // than left pointing at tokens that have just been revoked.
    return this.http
      .post<void>(`${environment.apiBaseUrl}/auth/change-password`, request)
      .pipe(tap(() => this.clearSession()));
  }

  /**
   * Re-reads the signed-in user from the API. Called on startup so a permission an
   * administrator granted or revoked since the token was issued is reflected in the UI.
   */
  refreshCurrentUser(): Observable<CurrentUser | null> {
    if (!this.accessToken) {
      return of(null);
    }

    return this.http.get<CurrentUser>(`${environment.apiBaseUrl}/auth/me`).pipe(
      tap((user) => {
        this.currentUser.set(user);
        localStorage.setItem(USER_KEY, JSON.stringify(user));
      }),
      catchError(() => {
        // A failure here means the stored token is no longer usable. Clearing is the honest
        // response; leaving a stale user would show a signed-in shell that cannot load anything.
        this.clearSession();
        return of(null);
      }),
    );
  }

  /**
   * Exchanges the refresh token for a new pair.
   *
   * Shared via shareReplay so that several requests failing with 401 at once produce one
   * refresh call, not one each. Without this, the API's single-use rotation would revoke the
   * token family on the second concurrent attempt and sign the user out.
   */
  refreshAccessToken(): Observable<string | null> {
    if (this.refreshInFlight$) {
      return this.refreshInFlight$;
    }

    const refreshToken = localStorage.getItem(REFRESH_TOKEN_KEY);

    if (!refreshToken) {
      return of(null);
    }

    this.refreshInFlight$ = this.http
      .post<AuthResponse>(`${environment.apiBaseUrl}/auth/refresh`, { refreshToken })
      .pipe(
        tap((response) => this.storeSession(response)),
        map((response) => response.accessToken),
        catchError(() => {
          this.clearSession();
          return of(null);
        }),
        tap({
          finalize: () => {
            this.refreshInFlight$ = null;
          },
        }),
        shareReplay(1),
      );

    return this.refreshInFlight$;
  }

  logout(redirectTo: string | null = '/login'): void {
    const refreshToken = localStorage.getItem(REFRESH_TOKEN_KEY);

    if (refreshToken) {
      // Fire and forget: the local session is cleared regardless, because the user has asked to
      // be signed out and a network failure must not leave them apparently still signed in.
      this.http
        .post<void>(`${environment.apiBaseUrl}/auth/logout`, { refreshToken })
        .pipe(catchError(() => of(void 0)))
        .subscribe();
    }

    this.clearSession();

    if (redirectTo) {
      void this.router.navigate([redirectTo]);
    }
  }

  get accessToken(): string | null {
    return localStorage.getItem(ACCESS_TOKEN_KEY);
  }

  /** True when the signed-in user holds the permission. Controls visibility, never access. */
  has(permission: string): boolean {
    return this.permissionSet().has(permission);
  }

  /** True when the user holds at least one of the permissions. */
  hasAny(...permissions: string[]): boolean {
    const held = this.permissionSet();
    return permissions.some((p) => held.has(p));
  }

  private storeSession(response: AuthResponse): void {
    localStorage.setItem(ACCESS_TOKEN_KEY, response.accessToken);
    localStorage.setItem(REFRESH_TOKEN_KEY, response.refreshToken);
    localStorage.setItem(USER_KEY, JSON.stringify(response.user));
    this.currentUser.set(response.user);
  }

  private clearSession(): void {
    localStorage.removeItem(ACCESS_TOKEN_KEY);
    localStorage.removeItem(REFRESH_TOKEN_KEY);
    localStorage.removeItem(USER_KEY);
    this.currentUser.set(null);
  }

  private readStoredUser(): CurrentUser | null {
    const raw = localStorage.getItem(USER_KEY);

    if (!raw) {
      return null;
    }

    try {
      return JSON.parse(raw) as CurrentUser;
    } catch {
      // Corrupt storage should not stop the app from loading; treat it as signed out.
      localStorage.removeItem(USER_KEY);
      return null;
    }
  }
}
