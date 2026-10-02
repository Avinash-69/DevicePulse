import { ApplicationConfig, provideZoneChangeDetection, inject, provideAppInitializer } from '@angular/core';
import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { provideRouter, withComponentInputBinding, withInMemoryScrolling } from '@angular/router';
import { firstValueFrom } from 'rxjs';

import { routes } from './app.routes';
import { authInterceptor } from './core/interceptors/auth.interceptor';
import { errorInterceptor } from './core/interceptors/error.interceptor';
import { AuthService } from './core/auth/auth.service';
import { ThemeService } from './core/services/theme.service';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),

    provideRouter(
      routes,
      // Lets route params bind straight to component inputs, which removes a lot of
      // boilerplate ActivatedRoute plumbing from the detail pages.
      withComponentInputBinding(),
      withInMemoryScrolling({ scrollPositionRestoration: 'top', anchorScrolling: 'enabled' }),
    ),

    provideHttpClient(
      withFetch(),
      // Order matters: the auth interceptor runs first so its refresh-and-retry happens before
      // the error interceptor would otherwise toast a 401 that is about to succeed.
      withInterceptors([authInterceptor, errorInterceptor]),
    ),

    // Applied before the first render so the page does not flash light then switch to dark.
    provideAppInitializer(() => {
      inject(ThemeService);
    }),

    // Revalidates a stored session against the API before the first route activates, so a
    // revoked token or a changed permission set is reflected immediately rather than on the
    // first failed request.
    provideAppInitializer(() => {
      const auth = inject(AuthService);

      return auth.isAuthenticated() ? firstValueFrom(auth.refreshCurrentUser()) : Promise.resolve(null);
    }),
  ],
};
