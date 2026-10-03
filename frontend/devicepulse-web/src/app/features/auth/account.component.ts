import { Component, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';

import { AuthService } from '../../core/auth/auth.service';
import { NotificationService } from '../../core/services/notification.service';
import { fieldErrorsFrom, messageFrom } from '../../core/interceptors/error.interceptor';
import { PageHeaderComponent } from '../../shared/components/ui.components';

/** The signed-in user's own account: their effective permissions, and a password change. */
@Component({
  selector: 'dp-account',
  standalone: true,
  imports: [ReactiveFormsModule, PageHeaderComponent],
  template: `
    <div class="page">
      <dp-page-header title="Your account" />

      <div class="split split-even">
        <section class="card">
          <div class="card-header"><h2>Change password</h2></div>

          <form [formGroup]="form" (ngSubmit)="submit()">
            <div class="card-body stack">
              <p class="muted small">
                Changing your password signs you out of every session, including this one, so you
                will need to sign in again.
              </p>

              <div class="field">
                <label for="current">Current password</label>
                <input id="current" type="password" formControlName="currentPassword" autocomplete="current-password" />
                @for (message of errorsFor('currentPassword'); track message) {
                  <span class="field-error">{{ message }}</span>
                }
              </div>

              <div class="field">
                <label for="next">New password</label>
                <input id="next" type="password" formControlName="newPassword" autocomplete="new-password" />
                <span class="field-hint">
                  At least 10 characters with upper and lower case, a digit and a symbol. The
                  server enforces this independently of this form.
                </span>
                @for (message of errorsFor('newPassword'); track message) {
                  <span class="field-error">{{ message }}</span>
                }
              </div>

              <div class="field">
                <label for="confirm">Confirm new password</label>
                <input id="confirm" type="password" formControlName="confirmPassword" autocomplete="new-password" />
                @if (mismatch()) {
                  <span class="field-error">The two passwords do not match.</span>
                }
              </div>

              @if (formError()) {
                <span class="field-error">{{ formError() }}</span>
              }
            </div>

            <div class="card-footer row">
              <span class="spacer"></span>
              <button type="submit" class="btn btn-primary" [disabled]="saving()">
                @if (saving()) {
                  <span class="spinner"></span>
                }
                Change password
              </button>
            </div>
          </form>
        </section>

        <section class="card">
          <div class="card-header"><h2>Access</h2></div>

          <div class="card-body stack">
            <div>
              <span class="fact-label">Signed in as</span>
              <p>{{ user()?.name }} <span class="muted">({{ user()?.email }})</span></p>
            </div>

            <div>
              <span class="fact-label">Roles</span>
              <div class="chips">
                @for (role of user()?.roles ?? []; track role) {
                  <span class="badge badge-accent">{{ role }}</span>
                }
              </div>
            </div>

            <div>
              <span class="fact-label">
                Effective permissions ({{ user()?.permissions?.length ?? 0 }})
              </span>
              <div class="chips">
                @for (permission of user()?.permissions ?? []; track permission) {
                  <span class="badge badge-neutral mono">{{ permission }}</span>
                }
              </div>
            </div>

            <p class="field-hint">
              This list is what the UI uses to decide what to show you. The API checks the same
              permissions again on every request, so it is the authority — not this page.
            </p>
          </div>
        </section>
      </div>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `

      @media (max-width: 900px) {
      }

      .fact-label {
        display: block;
        margin-bottom: var(--sp-1);
        font-size: 0.7rem;
        font-weight: 600;
        text-transform: uppercase;
        letter-spacing: 0.04em;
        color: var(--text-2);
      }

      .chips {
        display: flex;
        gap: var(--sp-1);
        flex-wrap: wrap;
      }

      .chips .badge { font-weight: 500; }
    `,
  ],
})
export class AccountComponent {
  private readonly auth = inject(AuthService);
  private readonly notifications = inject(NotificationService);
  private readonly router = inject(Router);
  private readonly fb = inject(FormBuilder);

  readonly user = this.auth.user;
  readonly saving = signal(false);
  readonly formError = signal<string | null>(null);

  private serverErrors: Record<string, string[]> = {};

  readonly form = this.fb.nonNullable.group({
    currentPassword: ['', [Validators.required]],
    newPassword: ['', [Validators.required, Validators.minLength(10)]],
    confirmPassword: ['', [Validators.required]],
  });

  /** Confirmation is checked client-side only — the API has no concept of it, and needs none. */
  mismatch(): boolean {
    const { newPassword, confirmPassword } = this.form.getRawValue();
    const control = this.form.controls.confirmPassword;

    return Boolean(confirmPassword && newPassword !== confirmPassword && (control.dirty || control.touched));
  }

  errorsFor(field: string): string[] {
    const control = this.form.get(field);
    const messages: string[] = [];

    if (control && (control.dirty || control.touched)) {
      if (control.hasError('required')) messages.push('This field is required.');
      if (control.hasError('minlength')) messages.push('At least 10 characters.');
    }

    return [...messages, ...(this.serverErrors[field] ?? []), ...(this.serverErrors['password'] ?? [])];
  }

  submit(): void {
    this.serverErrors = {};
    this.formError.set(null);

    if (this.form.invalid || this.mismatch()) {
      this.form.markAllAsTouched();
      return;
    }

    const value = this.form.getRawValue();
    this.saving.set(true);

    this.auth
      .changePassword({ currentPassword: value.currentPassword, newPassword: value.newPassword })
      .subscribe({
        next: () => {
          this.saving.set(false);

          this.notifications.success(
            'Password changed.',
            'All of your sessions were ended — please sign in again.',
          );

          // AuthService has already cleared the local session, since the API revoked the
          // tokens; sending the user to the login page is the only coherent next step.
          void this.router.navigate(['/login']);
        },
        error: (error: unknown) => {
          this.saving.set(false);
          this.serverErrors = fieldErrorsFrom(error);

          if (Object.keys(this.serverErrors).length === 0) {
            this.formError.set(messageFrom(error, 'Could not change the password.'));
          }
        },
      });
  }
}
