import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';

import { AuthService } from './auth.service';
import { NotificationService } from '../services/notification.service';

/**
 * Route guards.
 *
 * These are a navigation convenience, not a security control. A user who edits the URL past a
 * guard reaches a page whose API calls all return 403 — which is exactly the behaviour the
 * master reference describes (§29): the backend is the authority, and the guard just saves the
 * user from landing on a screen that cannot load anything.
 */
export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (auth.isAuthenticated()) {
    return true;
  }

  // The attempted URL is preserved so the user lands where they were going after signing in,
  // rather than always on the dashboard.
  return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
};

/** Keeps an already-signed-in user off the login page. */
export const anonymousOnlyGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  return auth.isAuthenticated() ? router.createUrlTree(['/dashboard']) : true;
};

/** Requires at least one of the given permissions to enter the route. */
export function permissionGuard(...permissions: string[]): CanActivateFn {
  return (_route, state) => {
    const auth = inject(AuthService);
    const router = inject(Router);
    const notifications = inject(NotificationService);

    if (!auth.isAuthenticated()) {
      return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
    }

    if (auth.hasAny(...permissions)) {
      return true;
    }

    // Said out loud rather than redirecting silently: a page that vanishes without explanation
    // reads as a bug, while "you do not have access" is an answer.
    notifications.warning('You do not have access to that page.');

    return router.createUrlTree(['/dashboard']);
  };
}
