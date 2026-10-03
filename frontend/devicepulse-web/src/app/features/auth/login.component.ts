import { Component, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';

import { AuthService } from '../../core/auth/auth.service';
import { ThemeService } from '../../core/services/theme.service';
import { messageFrom } from '../../core/interceptors/error.interceptor';
import { ToastsComponent } from '../../layout/toasts.component';

@Component({
  selector: 'dp-login',
  standalone: true,
  imports: [ReactiveFormsModule, ToastsComponent],
  template: `
    <div class="login-page">
      <button
        type="button"
        class="btn btn-ghost btn-icon theme-toggle"
        [attr.aria-label]="'Switch to ' + (theme.isDark() ? 'light' : 'dark') + ' theme'"
        (click)="theme.toggle()"
      >
        @if (theme.isDark()) {
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <circle cx="12" cy="12" r="4" />
            <path d="M12 2v2M12 20v2M2 12h2M20 12h2M5 5l1.5 1.5M17.5 17.5 19 19M19 5l-1.5 1.5M6.5 17.5 5 19" stroke-linecap="round" />
          </svg>
        } @else {
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <path d="M20 14.5A8.5 8.5 0 0 1 9.5 4a7 7 0 1 0 10.5 10.5z" stroke-linecap="round" stroke-linejoin="round" />
          </svg>
        }
      </button>

      <main class="column">
        <p class="brand">
          <svg class="mark" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" aria-hidden="true">
            <path d="M2 12h4l2.5-7 3.5 14 3-9 2 4h5" stroke-linecap="round" stroke-linejoin="round" />
          </svg>
          DevicePulse
        </p>

        <h1>Sign in to the fleet console</h1>

        <form class="stack" [formGroup]="form" (ngSubmit)="submit()" novalidate>
          @if (errorMessage()) {
            <div class="banner" role="alert">{{ errorMessage() }}</div>
          }

          <div class="field">
            <label for="email">Email</label>
            <input
              id="email"
              type="email"
              formControlName="email"
              autocomplete="username"
              [class.invalid]="showError('email')"
            />
            @if (showError('email')) {
              <span class="field-error">Enter a valid email address.</span>
            }
          </div>

          <div class="field">
            <label for="password">Password</label>
            <input
              id="password"
              type="password"
              formControlName="password"
              autocomplete="current-password"
              [class.invalid]="showError('password')"
            />
            @if (showError('password')) {
              <span class="field-error">Enter your password.</span>
            }
          </div>

          <button type="submit" class="btn btn-primary submit" [disabled]="submitting()">
            @if (submitting()) {
              <span class="spinner"></span>
            }
            Sign in
          </button>
        </form>

        <p class="footnote text-2 small">No account? Ask an administrator to create one for you.</p>
      </main>
    </div>

    <dp-toasts />
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `
      /* No card, no gradient, no hero. A sign-in page has one job, and the form sits on the
         page the way it would on a piece of equipment's own login screen. */
      .login-page {
        position: relative;
        display: grid;
        align-items: center;
        min-height: 100vh;
        padding: var(--sp-7) var(--sp-4);
        background: var(--canvas);
      }

      .theme-toggle {
        position: absolute;
        top: var(--sp-3);
        right: var(--sp-3);
      }

      .column {
        width: 100%;
        max-width: 340px;
        margin: 0 auto;
      }

      .brand {
        display: flex;
        gap: var(--sp-2);
        align-items: center;
        margin: 0 0 var(--sp-8);
        font-weight: var(--fw-semibold);
      }

      .mark {
        width: 20px;
        height: 20px;
        color: var(--accent);
      }

      h1 {
        margin-bottom: var(--sp-6);
        font-size: var(--fs-xl);
        font-weight: var(--fw-medium);
      }

      .submit {
        height: 36px;
        margin-top: var(--sp-2);
      }

      input { height: 36px; }

      .banner {
        padding: var(--sp-2) var(--sp-3);
        font-size: var(--fs-sm);
        color: var(--danger);
        background: var(--danger-wash);
        border-left: 3px solid var(--danger);
      }

      .footnote {
        margin: var(--sp-6) 0 0;
        padding-top: var(--sp-4);
        border-top: 1px solid var(--line);
      }
    `,
  ],
})
export class LoginComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly fb = inject(FormBuilder);

  readonly theme = inject(ThemeService);

  readonly submitting = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly form = this.fb.nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required]],
  });

  showError(field: 'email' | 'password'): boolean {
    const control = this.form.controls[field];
    return control.invalid && (control.dirty || control.touched);
  }

  submit(): void {
    this.errorMessage.set(null);

    if (this.form.invalid) {
      // Marked as touched so the inline messages appear; the submit was a deliberate action,
      // so the user should see what is missing.
      this.form.markAllAsTouched();
      return;
    }

    this.submitting.set(true);

    this.auth.login(this.form.getRawValue()).subscribe({
      next: () => {
        // Returned to where they were trying to go, which is what makes a mid-session
        // expiry unobtrusive.
        const returnUrl = new URLSearchParams(window.location.search).get('returnUrl');
        void this.router.navigateByUrl(returnUrl || '/dashboard');
      },
      error: (error: unknown) => {
        this.submitting.set(false);

        // Shown in the form rather than as a toast: a failed sign-in belongs next to the
        // fields the user is about to correct.
        this.errorMessage.set(messageFrom(error, 'Could not sign in. Please try again.'));
      },
    });
  }
}
