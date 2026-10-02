import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AuthService } from './auth.service';
import { Permissions } from './permissions';
import { environment } from '../../../environments/environment';
import { AuthResponse } from '../models/api.models';

/**
 * The session layer is worth testing directly: it holds the tokens, decides what the UI offers,
 * and contains the one piece of genuinely subtle logic on the client — refresh coalescing.
 */
describe('AuthService', () => {
  let service: AuthService;
  let http: HttpTestingController;

  const authResponse = (overrides: Partial<AuthResponse> = {}): AuthResponse => ({
    accessToken: 'access-token-1',
    refreshToken: 'refresh-token-1',
    accessTokenExpiresAt: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
    user: {
      userId: 7,
      name: 'Ops User',
      email: 'ops@devicepulse.test',
      roles: ['Operator'],
      permissions: [Permissions.deviceView, Permissions.alertResolve],
    },
    ...overrides,
  });

  beforeEach(() => {
    localStorage.clear();

    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });

    service = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    localStorage.clear();
  });

  it('starts unauthenticated with nothing in storage', () => {
    expect(service.isAuthenticated()).toBeFalse();
    expect(service.user()).toBeNull();
  });

  it('stores the session after a successful login', () => {
    service.login({ email: 'ops@devicepulse.test', password: 'secret' }).subscribe();

    const request = http.expectOne(`${environment.apiBaseUrl}/auth/login`);
    expect(request.request.method).toBe('POST');
    request.flush(authResponse());

    expect(service.isAuthenticated()).toBeTrue();
    expect(service.user()?.email).toBe('ops@devicepulse.test');
    expect(service.accessToken).toBe('access-token-1');
  });

  it('restores the signed-in user from storage after a reload', () => {
    service.login({ email: 'ops@devicepulse.test', password: 'secret' }).subscribe();
    http.expectOne(`${environment.apiBaseUrl}/auth/login`).flush(authResponse());
    http.verify();

    // Rebuilding the injector stands in for a page reload: the new service instance has to
    // recover the session from storage, or every refresh would bounce the user to the login page.
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });

    const revived = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);

    expect(revived.isAuthenticated()).toBeTrue();
    expect(revived.user()?.email).toBe('ops@devicepulse.test');
    expect(revived.has(Permissions.deviceView)).toBeTrue();
  });

  it('reports the permissions it holds and refuses the ones it does not', () => {
    service.login({ email: 'ops@devicepulse.test', password: 'secret' }).subscribe();
    http.expectOne(`${environment.apiBaseUrl}/auth/login`).flush(authResponse());

    expect(service.has(Permissions.deviceView)).toBeTrue();
    expect(service.has(Permissions.alertResolve)).toBeTrue();

    // Not granted, so the UI must not offer it — the API would refuse it anyway.
    expect(service.has(Permissions.settingsManage)).toBeFalse();
    expect(service.has(Permissions.userCreate)).toBeFalse();
  });

  it('matches permissions exactly rather than by prefix', () => {
    service.login({ email: 'ops@devicepulse.test', password: 'secret' }).subscribe();
    http.expectOne(`${environment.apiBaseUrl}/auth/login`).flush(authResponse());

    expect(service.has('device')).toBeFalse();
    expect(service.has('device.view.extra')).toBeFalse();
  });

  it('hasAny is true when at least one permission is held', () => {
    service.login({ email: 'ops@devicepulse.test', password: 'secret' }).subscribe();
    http.expectOne(`${environment.apiBaseUrl}/auth/login`).flush(authResponse());

    expect(service.hasAny(Permissions.settingsManage, Permissions.deviceView)).toBeTrue();
    expect(service.hasAny(Permissions.settingsManage, Permissions.userCreate)).toBeFalse();
    expect(service.hasAny()).toBeFalse();
  });

  it('clears the session on logout and revokes the refresh token', () => {
    service.login({ email: 'ops@devicepulse.test', password: 'secret' }).subscribe();
    http.expectOne(`${environment.apiBaseUrl}/auth/login`).flush(authResponse());

    service.logout(null);

    const logout = http.expectOne(`${environment.apiBaseUrl}/auth/logout`);
    expect(logout.request.body).toEqual({ refreshToken: 'refresh-token-1' });
    logout.flush(null);

    expect(service.isAuthenticated()).toBeFalse();
    expect(service.accessToken).toBeNull();
  });

  it('clears the local session even when the logout call fails', () => {
    service.login({ email: 'ops@devicepulse.test', password: 'secret' }).subscribe();
    http.expectOne(`${environment.apiBaseUrl}/auth/login`).flush(authResponse());

    service.logout(null);

    // The user asked to be signed out; a network failure must not leave them apparently
    // still signed in.
    http.expectOne(`${environment.apiBaseUrl}/auth/logout`).error(new ProgressEvent('network'));

    expect(service.isAuthenticated()).toBeFalse();
  });

  it('coalesces concurrent refreshes into a single request', () => {
    service.login({ email: 'ops@devicepulse.test', password: 'secret' }).subscribe();
    http.expectOne(`${environment.apiBaseUrl}/auth/login`).flush(authResponse());

    const results: (string | null)[] = [];

    // Three requests failing with 401 at once must produce ONE refresh. The API rotates
    // refresh tokens single-use, so a second concurrent attempt would be treated as a replay
    // and revoke the whole family — signing the user out instead of recovering.
    service.refreshAccessToken().subscribe((token) => results.push(token));
    service.refreshAccessToken().subscribe((token) => results.push(token));
    service.refreshAccessToken().subscribe((token) => results.push(token));

    const refresh = http.expectOne(`${environment.apiBaseUrl}/auth/refresh`);
    refresh.flush(authResponse({ accessToken: 'access-token-2', refreshToken: 'refresh-token-2' }));

    expect(results).toEqual(['access-token-2', 'access-token-2', 'access-token-2']);
    expect(service.accessToken).toBe('access-token-2');
  });

  it('clears the session when the refresh token is rejected', () => {
    service.login({ email: 'ops@devicepulse.test', password: 'secret' }).subscribe();
    http.expectOne(`${environment.apiBaseUrl}/auth/login`).flush(authResponse());

    let token: string | null = 'unset';
    service.refreshAccessToken().subscribe((value) => (token = value));

    http
      .expectOne(`${environment.apiBaseUrl}/auth/refresh`)
      .flush({ detail: 'expired' }, { status: 401, statusText: 'Unauthorized' });

    expect(token).toBeNull();
    expect(service.isAuthenticated()).toBeFalse();
  });

  it('does not attempt a refresh when there is no refresh token', () => {
    let token: string | null = 'unset';
    service.refreshAccessToken().subscribe((value) => (token = value));

    expect(token).toBeNull();
    http.expectNone(`${environment.apiBaseUrl}/auth/refresh`);
  });

  it('clears the session when /auth/me rejects a stored token', () => {
    service.login({ email: 'ops@devicepulse.test', password: 'secret' }).subscribe();
    http.expectOne(`${environment.apiBaseUrl}/auth/login`).flush(authResponse());

    service.refreshCurrentUser().subscribe();

    http
      .expectOne(`${environment.apiBaseUrl}/auth/me`)
      .flush({ detail: 'revoked' }, { status: 401, statusText: 'Unauthorized' });

    // A stale user with a dead token would render a signed-in shell that cannot load anything.
    expect(service.isAuthenticated()).toBeFalse();
  });

  it('picks up a permission change reported by /auth/me', () => {
    service.login({ email: 'ops@devicepulse.test', password: 'secret' }).subscribe();
    http.expectOne(`${environment.apiBaseUrl}/auth/login`).flush(authResponse());

    expect(service.has(Permissions.settingsManage)).toBeFalse();

    service.refreshCurrentUser().subscribe();

    http.expectOne(`${environment.apiBaseUrl}/auth/me`).flush({
      userId: 7,
      name: 'Ops User',
      email: 'ops@devicepulse.test',
      roles: ['Operator', 'Admin'],
      permissions: [Permissions.deviceView, Permissions.settingsManage],
    });

    expect(service.has(Permissions.settingsManage)).toBeTrue();
    expect(service.has(Permissions.alertResolve)).toBeFalse();
  });

  it('survives corrupt data in storage instead of failing to start', () => {
    localStorage.setItem('devicepulse.user', '{not valid json');

    // A fresh TestBed stands in for a reload with a corrupted entry.
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });

    const revived = TestBed.inject(AuthService);

    expect(revived.isAuthenticated()).toBeFalse();
    expect(localStorage.getItem('devicepulse.user')).toBeNull();

    http = TestBed.inject(HttpTestingController);
  });
});
