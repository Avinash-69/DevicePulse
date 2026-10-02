import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';

import { NotificationService } from '../services/notification.service';
import { ProblemDetails } from '../models/api.models';

/**
 * Turns the API's ProblemDetails responses into human-readable toasts.
 *
 * Because every endpoint returns the same error shape (Appendix D.1), this one interceptor
 * handles all of them — there is no per-call error handling scattered through the components.
 *
 * Two statuses are deliberately not surfaced here:
 *   * 401 — the auth interceptor is already refreshing and retrying; a toast would appear on a
 *     request that is about to succeed.
 *   * 400 with field errors — those belong next to the field in the form, not in a corner toast,
 *     so the error is rethrown for the component to bind.
 */
export const errorInterceptor: HttpInterceptorFn = (request, next) => {
  const notifications = inject(NotificationService);

  return next(request).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse) {
        report(error, notifications);
      }

      return throwError(() => error);
    }),
  );
};

function report(error: HttpErrorResponse, notifications: NotificationService): void {
  if (error.status === 401) {
    return;
  }

  const problem = asProblemDetails(error);

  // Field-level validation is the form's business.
  if (error.status === 400 && problem?.errors) {
    return;
  }

  // The traceId is the one thing that makes a production failure traceable end to end
  // (Appendix D.4), so it is always shown when present.
  const detail = problem?.traceId ? `Trace ID: ${problem.traceId}` : undefined;

  switch (error.status) {
    case 0:
      notifications.error(
        'Cannot reach the DevicePulse API.',
        'Check that the backend is running and that this origin is in its allowed CORS list.',
      );
      return;

    case 403:
      notifications.warning(
        problem?.detail ?? 'You do not have permission to do that.',
        detail,
      );
      return;

    case 404:
      notifications.warning(problem?.detail ?? 'That item no longer exists.', detail);
      return;

    case 409:
      notifications.warning(problem?.detail ?? 'That conflicts with the current state.', detail);
      return;

    case 429:
      notifications.warning(
        problem?.detail ?? 'Too many requests. Slow down and try again shortly.',
        detail,
      );
      return;

    case 400:
      notifications.warning(problem?.detail ?? 'That request was not valid.', detail);
      return;

    default:
      notifications.error(
        problem?.detail ?? 'Something went wrong on the server.',
        detail,
      );
  }
}

function asProblemDetails(error: HttpErrorResponse): ProblemDetails | null {
  const body = error.error;

  // A non-object body means the failure happened before the API could shape a response —
  // a network error, or a proxy returning HTML.
  if (!body || typeof body !== 'object') {
    return null;
  }

  return body as ProblemDetails;
}

/** Pulls field errors out of a failed request so a form can show them inline. */
export function fieldErrorsFrom(error: unknown): Record<string, string[]> {
  if (!(error instanceof HttpErrorResponse)) {
    return {};
  }

  const problem = error.error as ProblemDetails | undefined;
  return problem?.errors ?? {};
}

/** The single best message to show for a failed request, when a form has no field to attach it to. */
export function messageFrom(error: unknown, fallback = 'Something went wrong.'): string {
  if (!(error instanceof HttpErrorResponse)) {
    return fallback;
  }

  const problem = error.error as ProblemDetails | undefined;

  if (problem?.errors) {
    const first = Object.values(problem.errors)[0];

    if (first?.length) {
      return first[0];
    }
  }

  return problem?.detail ?? fallback;
}
