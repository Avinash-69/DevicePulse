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
        class="btn btn-ghost btn-sm theme-toggle"
        (click)="theme.toggle()"
      >
        {{ theme.isDark() ? 'Light' : 'Dark' }}
      </button>

      <div class="panel">
        <div class="brand">
          <span class="mark" aria-hidden="true">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2">
              <path d="M2 12h4l2.5-7 3.5 14 3-9 2 4h5" stroke-linecap="round" stroke-linejoin="round" />
            </svg>
          </span>
          <div>
            <h1>DevicePulse</h1>
            <p class="muted small">IoT device monitoring and management</p>
          </div>
        </div>

        <form class="panel" [formGroup]="form" (ngSubmit)="submit()">
          <div class="panel-body stack">
            <h2>Sign in</h2>

            @if (errorMessage()) {
              <div class="banner" role="alert">{{ errorMessage() }}</div>
            }

            <div class="field">
              <label for="email">Email address</label>
              <input
                id="email"
                type="email"
                formControlName="email"
                autocomplete="username"
                [class.invalid]="showError('email')"
                placeholder="you@example.com"
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

            <button type="submit" class="btn btn-primary" [disabled]="submitting()">
              @if (submitting()) {
                <span class="spinner"></span>
              }
              Sign in
            </button>
          </div>
        </form>

        <p class="footnote subtle small">
          Access is granted by an administrator. Permissions are enforced by the API on every
          request, not by this page.
        </p>
      </div>
    </div>

    <dp-toasts />
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `
      .login-page {
        position: relative;
        display: grid;
        place-items: center;
        min-height: 100vh;
        padding: var(--sp-7) var(--sp-4);
        background:
          radial-gradient(1100px 520px at 50% -10%, var(--accent-wash), transparent 70%),
          var(--canvas);
      }

      .theme-toggle {
        position: absolute;
        top: 1rem;
        right: 1rem;
      }

      .panel {
        width: 100%;
        max-width: 380px;
      }

      .brand {
        display: flex;
        gap: var(--sp-3);
        align-items: center;
        margin-bottom: var(--sp-5);
      }

      .mark {
        display: grid;
        place-items: center;
        width: 38px;
        height: 38px;
        color: var(--accent-text);
        background: var(--accent);
        border-radius: 9px;
        flex: 0 0 auto;
      }

      .mark svg { width: 23px; height: 23px; }

      .brand h1 { font-size: 1.2rem; }
      .brand p { margin: 0; }

      form { box-shadow: var(--shadow-pop); }

      .banner {
        padding: var(--sp-2) var(--sp-3);
        font-size: 0.82rem;
        color: var(--danger);
        background: var(--danger-wash);
        border-radius: var(--r-md);
      }

      .footnote {
        margin: var(--sp-4) 0 0;
        text-align: center;
        line-height: 1.5;
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
