import { Component, OnInit, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { debounceTime, distinctUntilChanged } from 'rxjs';

import { ApiService } from '../../core/services/api.service';
import { Permissions } from '../../core/auth/permissions';
import { AuthService } from '../../core/auth/auth.service';
import { NotificationService } from '../../core/services/notification.service';
import { fieldErrorsFrom, messageFrom } from '../../core/interceptors/error.interceptor';
import { PagedResult, Role, User, UserQuery } from '../../core/models/api.models';
import {
  EmptyStateComponent,
  IfPermittedComponent,
  LoadingRowsComponent,
  ModalComponent,
  PageHeaderComponent,
  PaginatorComponent,
} from '../../shared/components/ui.components';
import { AbsoluteTimePipe, RelativeTimePipe } from '../../shared/utils/relative-time.pipe';

/**
 * User administration (§12).
 *
 * Creating a user is a privileged, server-side operation: the browser posts to the API, which
 * checks the permission, hashes the password, assigns roles and writes an audit entry. The
 * browser never holds any administrative credential of its own.
 */
@Component({
  selector: 'dp-user-list',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    PageHeaderComponent,
    PaginatorComponent,
    EmptyStateComponent,
    LoadingRowsComponent,
    ModalComponent,
    IfPermittedComponent,
    RelativeTimePipe,
    AbsoluteTimePipe
],
  template: `
    <div class="page">
      <dp-page-header title="Users">
        <dp-if-permitted [permission]="perm.userCreate">
          <button type="button" class="btn btn-primary" (click)="openCreate()">Add user</button>
        </dp-if-permitted>
      </dp-page-header>

      <div class="panel">
        <div class="toolbar">
          <div class="field search">
            <input
              type="search"
              placeholder="Search name or email"
              [formControl]="searchControl"
              aria-label="Search users"
            />
          </div>

          <div class="field">
            <select [value]="query().roleId ?? ''" (change)="setRole($event)" aria-label="Role">
              <option value="">Any role</option>
              @for (role of roles(); track role.roleId) {
                <option [value]="role.roleId">{{ role.name }}</option>
              }
            </select>
          </div>

          <div class="field">
            <select [value]="activeFilter()" (change)="setActive($event)" aria-label="Status">
              <option value="">Any status</option>
              <option value="true">Active</option>
              <option value="false">Deactivated</option>
            </select>
          </div>
        </div>

        @if (loading()) {
          <dp-loading-rows [count]="5" />
        } @else {
          @if (result(); as page) {
            @if (page.items.length === 0) {
              <dp-empty title="No users match" message="Try clearing the filters." />
            } @else {
              <div class="table-wrap">
                <table class="data">
                  <thead>
                    <tr>
                      <th>Name</th>
                      <th>Email</th>
                      <th>Roles</th>
                      <th>Status</th>
                      <th>Last sign-in</th>
                      <th class="right">Actions</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (user of page.items; track user.userId) {
                      <tr>
                        <td>
                          {{ user.name }}
                          @if (user.userId === currentUserId()) {
                            <span class="badge badge-accent">you</span>
                          }
                        </td>
                        <td class="muted">{{ user.email }}</td>
                        <td>
                          <div class="role-chips">
                            @for (role of user.roles; track role.roleId) {
                              <span class="badge badge-neutral">{{ role.name }}</span>
                            }
                          </div>
                        </td>
                        <td>
                          @if (!user.isActive) {
                            <span class="badge badge-danger">deactivated</span>
                          } @else if (user.isLockedOut) {
                            <span
                              class="badge badge-warn"
                              [title]="'Locked until ' + (user.lockedOutUntil | absoluteTime: true)"
                            >
                              locked out
                            </span>
                          } @else {
                            <span class="badge badge-ok">active</span>
                          }
                        </td>
                        <td class="muted small nowrap">
                          {{ user.lastLoginAt ? (user.lastLoginAt | relativeTime) : 'never' }}
                        </td>
                        <td class="right nowrap">
                          <dp-if-permitted [permission]="perm.userUpdate">
                            <button type="button" class="btn btn-sm" (click)="openEdit(user)">Edit</button>
                            <button type="button" class="btn btn-sm btn-ghost" (click)="openReset(user)">
                              Reset password
                            </button>
                          </dp-if-permitted>

                          <dp-if-permitted [permission]="perm.userDisable">
                            <button
                              type="button"
                              class="btn btn-sm btn-ghost"
                              (click)="toggleStatus(user)"
                              [disabled]="busyId() === user.userId || user.userId === currentUserId()"
                              [title]="
                                user.userId === currentUserId()
                                  ? 'You cannot deactivate your own account'
                                  : ''
                              "
                            >
                              {{ user.isActive ? 'Deactivate' : 'Activate' }}
                            </button>
                          </dp-if-permitted>
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>

              <dp-paginator
                [page]="page.page"
                [pageSize]="page.pageSize"
                [totalCount]="page.totalCount"
                [totalPages]="page.totalPages"
                (pageChange)="goToPage($event)"
                (pageSizeChange)="setPageSize($event)"
              />
            }
          }
        }
      </div>
    </div>

    <!-- Create / edit -->
    <dp-modal
      [open]="formOpen()"
      [title]="editing() ? 'Edit user' : 'Add user'"
      (closed)="formOpen.set(false)"
    >
      <form [formGroup]="form" (ngSubmit)="save()">
        <div class="panel-body stack">
          <div class="field">
            <label for="name">Full name</label>
            <input id="name" type="text" formControlName="name" />
            @for (message of errorsFor('name'); track message) {
              <span class="field-error">{{ message }}</span>
            }
          </div>

          <div class="field">
            <label for="email">Email address</label>
            <input id="email" type="email" formControlName="email" autocomplete="off" />
            <span class="field-hint">Used to sign in. Must be unique.</span>
            @for (message of errorsFor('email'); track message) {
              <span class="field-error">{{ message }}</span>
            }
          </div>

          @if (!editing()) {
            <div class="field">
              <label for="password">Initial password</label>
              <input id="password" type="text" formControlName="password" autocomplete="new-password" />
              <span class="field-hint">
                Share it with the user and have them change it at first sign-in. The server
                enforces its own strength policy regardless of what this form accepts.
              </span>
              @for (message of errorsFor('password'); track message) {
                <span class="field-error">{{ message }}</span>
              }
            </div>
          }

          <div class="field">
            <label>Roles</label>
            <div class="role-picker">
              @for (role of roles(); track role.roleId) {
                <label class="checkbox" [class.disabled]="!role.isActive">
                  <input
                    type="checkbox"
                    [checked]="selectedRoles().includes(role.roleId)"
                    [disabled]="!role.isActive"
                    (change)="toggleRole(role.roleId)"
                  />
                  <span>
                    {{ role.name }}
                    @if (!role.isActive) {
                      <span class="subtle small">(disabled)</span>
                    }
                  </span>
                </label>
              }
            </div>
            <span class="field-hint">
              Permissions come from the roles assigned here. Review them under Roles.
            </span>
            @for (message of errorsFor('roleIds'); track message) {
              <span class="field-error">{{ message }}</span>
            }
          </div>

          @if (formError()) {
            <span class="field-error">{{ formError() }}</span>
          }
        </div>

        <div class="panel-foot row">
          <span class="spacer"></span>
          <button type="button" class="btn" (click)="formOpen.set(false)">Cancel</button>
          <button type="submit" class="btn btn-primary" [disabled]="saving()">
            @if (saving()) {
              <span class="spinner"></span>
            }
            {{ editing() ? 'Save changes' : 'Create user' }}
          </button>
        </div>
      </form>
    </dp-modal>

    <!-- Reset password -->
    <dp-modal [open]="resetting() !== null" title="Reset password" (closed)="resetting.set(null)">
      <div class="panel-body stack">
        <p class="small">
          Sets a new password for <strong>{{ resetting()?.email }}</strong>, clears any lockout,
          and signs them out of every existing session.
        </p>

        <div class="field">
          <label for="newPassword">New password</label>
          <input id="newPassword" type="text" [formControl]="newPassword" autocomplete="new-password" />
          <span class="field-hint">
            Recorded in the audit log as an event — the password itself is never stored or logged.
          </span>
        </div>

        @if (formError()) {
          <span class="field-error">{{ formError() }}</span>
        }
      </div>

      <div class="panel-foot row">
        <span class="spacer"></span>
        <button type="button" class="btn" (click)="resetting.set(null)">Cancel</button>
        <button type="button" class="btn btn-primary" (click)="resetPassword()" [disabled]="saving()">
          Reset password
        </button>
      </div>
    </dp-modal>
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `

      .toolbar .field select { width: auto; min-width: 140px; }
      .toolbar .search { flex: 1 1 240px; }

      .role-chips {
        display: flex;
        gap: var(--sp-1);
        flex-wrap: wrap;
      }

      .role-picker {
        display: grid;
        gap: var(--sp-2);
        padding: var(--sp-2) var(--sp-3);
        border: 1px solid var(--line);
        border-radius: var(--r-md);
      }

      .checkbox.disabled { opacity: 0.55; }

      td.right .btn + .btn { margin-left: var(--sp-1); }
    `,
  ],
})
export class UserListComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly notifications = inject(NotificationService);
  private readonly fb = inject(FormBuilder);

  readonly perm = Permissions;

  readonly result = signal<PagedResult<User> | null>(null);
  readonly roles = signal<Role[]>([]);
  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly busyId = signal<number | null>(null);
  readonly formOpen = signal(false);
  readonly editing = signal<User | null>(null);
  readonly resetting = signal<User | null>(null);
  readonly formError = signal<string | null>(null);
  readonly selectedRoles = signal<number[]>([]);

  readonly query = signal<UserQuery>({ page: 1, pageSize: 20 });
  readonly activeFilter = signal<string>('');

  readonly searchControl = this.fb.nonNullable.control('');
  readonly newPassword = this.fb.nonNullable.control('');

  private serverErrors: Record<string, string[]> = {};

  readonly form = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(200)]],
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required, Validators.minLength(10)]],
  });

  readonly currentUserId = () => this.auth.user()?.userId ?? null;

  ngOnInit(): void {
    this.load();

    if (this.auth.has(Permissions.roleView)) {
      this.api.getRoles().subscribe({ next: (roles) => this.roles.set(roles) });
    }

    this.searchControl.valueChanges
      .pipe(debounceTime(300), distinctUntilChanged())
      .subscribe((term) => this.patchQuery({ search: term || undefined, page: 1 }));
  }

  private load(): void {
    this.loading.set(true);

    this.api.getUsers(this.query()).subscribe({
      next: (page) => {
        this.result.set(page);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  private patchQuery(patch: Partial<UserQuery>): void {
    this.query.update((current) => ({ ...current, ...patch }));
    this.load();
  }

  goToPage(page: number): void {
    this.patchQuery({ page });
  }

  setPageSize(pageSize: number): void {
    this.patchQuery({ pageSize, page: 1 });
  }

  setRole(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.patchQuery({ roleId: value ? Number(value) : undefined, page: 1 });
  }

  setActive(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.activeFilter.set(value);
    this.patchQuery({ isActive: value === '' ? undefined : value === 'true', page: 1 });
  }

  toggleRole(roleId: number): void {
    this.selectedRoles.update((current) =>
      current.includes(roleId) ? current.filter((id) => id !== roleId) : [...current, roleId],
    );
  }

  openCreate(): void {
    this.serverErrors = {};
    this.formError.set(null);
    this.editing.set(null);
    this.selectedRoles.set([]);

    this.form.reset({ name: '', email: '', password: '' });
    this.form.controls.password.enable();

    this.formOpen.set(true);
  }

  openEdit(user: User): void {
    this.serverErrors = {};
    this.formError.set(null);
    this.editing.set(user);
    this.selectedRoles.set(user.roles.map((r) => r.roleId));

    this.form.reset({ name: user.name, email: user.email, password: '' });

    // Disabled on edit so its required validator does not block the form — changing a password
    // is a separate, explicitly-audited operation.
    this.form.controls.password.disable();

    this.formOpen.set(true);
  }

  openReset(user: User): void {
    this.formError.set(null);
    this.newPassword.setValue('');
    this.resetting.set(user);
  }

  errorsFor(field: string): string[] {
    const control = this.form.get(field);
    const messages: string[] = [];

    if (control && (control.dirty || control.touched)) {
      if (control.hasError('required')) messages.push('This field is required.');
      if (control.hasError('email')) messages.push('Enter a valid email address.');
      if (control.hasError('minlength')) messages.push('At least 10 characters.');
      if (control.hasError('maxlength')) messages.push('That is too long.');
    }

    return [...messages, ...(this.serverErrors[field] ?? [])];
  }

  save(): void {
    this.serverErrors = {};
    this.formError.set(null);

    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    if (this.selectedRoles().length === 0) {
      // Checked here because a user with no role would have no permissions at all, which is
      // never a useful account. The API refuses it too.
      this.serverErrors = { roleIds: ['Assign at least one role.'] };
      return;
    }

    this.saving.set(true);
    const value = this.form.getRawValue();
    const user = this.editing();

    const failed = (error: unknown) => {
      this.saving.set(false);
      this.serverErrors = fieldErrorsFrom(error);

      if (Object.keys(this.serverErrors).length === 0) {
        this.formError.set(messageFrom(error, 'Could not save the user.'));
      }
    };

    if (user) {
      this.api
        .updateUser(user.userId, {
          name: value.name.trim(),
          email: value.email.trim(),
          roleIds: this.selectedRoles(),
        })
        .subscribe({
          next: () => {
            this.saving.set(false);
            this.formOpen.set(false);
            this.notifications.success('User updated.');
            this.load();
          },
          error: failed,
        });

      return;
    }

    this.api
      .createUser({
        name: value.name.trim(),
        email: value.email.trim(),
        password: value.password,
        roleIds: this.selectedRoles(),
      })
      .subscribe({
        next: (created) => {
          this.saving.set(false);
          this.formOpen.set(false);
          this.notifications.success(`${created.email} created.`);
          this.load();
        },
        error: failed,
      });
  }

  resetPassword(): void {
    const user = this.resetting();

    if (!user) {
      return;
    }

    this.saving.set(true);
    this.formError.set(null);

    this.api.resetUserPassword(user.userId, this.newPassword.value).subscribe({
      next: () => {
        this.saving.set(false);
        this.resetting.set(null);
        this.notifications.success(
          `Password reset for ${user.email}.`,
          'Their existing sessions have been ended.',
        );
        this.load();
      },
      error: (error: unknown) => {
        this.saving.set(false);
        this.formError.set(messageFrom(error, 'Could not reset the password.'));
      },
    });
  }

  toggleStatus(user: User): void {
    const activating = !user.isActive;

    if (!activating) {
      const confirmed = window.confirm(
        `Deactivate ${user.email}?\n\nThey will be signed out immediately and will not be able to sign in again until reactivated.`,
      );

      if (!confirmed) {
        return;
      }
    }

    this.busyId.set(user.userId);

    this.api.setUserStatus(user.userId, activating).subscribe({
      next: () => {
        this.busyId.set(null);
        this.notifications.success(`${user.email} ${activating ? 'activated' : 'deactivated'}.`);
        this.load();
      },
      error: () => this.busyId.set(null),
    });
  }
}
