import { Component, OnInit, computed, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';

import { ApiService } from '../../core/services/api.service';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { NotificationService } from '../../core/services/notification.service';
import { fieldErrorsFrom, messageFrom } from '../../core/interceptors/error.interceptor';
import { Permission, Role } from '../../core/models/api.models';
import {
  EmptyStateComponent,
  IfPermittedComponent,
  ModalComponent,
  PageHeaderComponent,
} from '../../shared/components/ui.components';

/**
 * Role and permission administration (§12, Appendix A.3).
 *
 * This is where the RBAC story stops being theoretical: a Super Admin can create a role, attach
 * any subset of the permission catalog to it, and assign it to users — entirely through the UI,
 * with the API enforcing the result centrally.
 *
 * The permission *catalog* is read-only here on purpose (Appendix C item 4). Keys are defined in
 * code as features are built, because a key with no enforcement behind it would be a permission
 * that silently does nothing.
 */
@Component({
  selector: 'dp-role-list',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    PageHeaderComponent,
    EmptyStateComponent,
    ModalComponent,
    IfPermittedComponent
],
  template: `
    <div class="page">
      <dp-page-header
        title="Roles"
      >
        <dp-if-permitted [permission]="perm.roleManage">
          <button type="button" class="btn btn-primary" (click)="openCreate()">New role</button>
        </dp-if-permitted>
      </dp-page-header>

      @if (loading()) {
        <div class="skeleton" style="height: 280px"></div>
      } @else if (roles().length === 0) {
        <div class="panel">
          <dp-empty title="No roles" message="The backend seeds four system roles on first run." />
        </div>
      } @else {
        <div class="panel">
          <div class="table-wrap">
            <table class="data">
              <thead>
                <tr>
                  <th>Role</th>
                  <th class="col-optional">Description</th>
                  <th>Permissions</th>
                  <th class="right">Users</th>
                  <th class="actions"><span class="sr-only">Actions</span></th>
                </tr>
              </thead>
              <tbody>
                @for (role of roles(); track role.roleId) {
                  <tr [class.inactive]="!role.isActive">
                    <td class="primary nowrap">
                      {{ role.name }}
                      @if (role.isSystemRole) {
                        <span class="tag" title="Seeded by the application; cannot be renamed or disabled">built-in</span>
                      }
                      @if (!role.isActive) {
                        <span class="tag">disabled</span>
                      }
                    </td>
                    <td class="text-2 col-optional desc">{{ role.description }}</td>
                    <td>
                      <!-- Coverage of the catalog, so "how much can this role do" is comparable
                           down the column at a glance. -->
                      <span class="coverage">
                        <span class="meter">
                          <span [style.width.%]="(role.permissions.length / (catalog().length || 1)) * 100"></span>
                        </span>
                        <span class="num">{{ role.permissions.length }}</span>
                        <span class="text-3">of {{ catalog().length }}</span>
                      </span>
                    </td>
                    <td class="right num">{{ role.userCount }}</td>
                    <td class="actions">
                      @if (canManagePermissions()) {
                        <button type="button" class="btn btn-row" (click)="openPermissions(role)">Permissions</button>
                      }
                      @if (canManageRoles()) {
                        <button type="button" class="btn btn-row" (click)="openEdit(role)">Edit</button>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </div>
      }
    </div>

    <!-- Create / edit the role itself -->
    <dp-modal
      [open]="formOpen()"
      [title]="editing() ? 'Edit role' : 'New role'"
      (closed)="formOpen.set(false)"
    >
      <form [formGroup]="form" (ngSubmit)="save()">
        <div class="panel-body stack">
          @if (editing()?.isSystemRole) {
            <p class="notice small">
              This is a built-in role. Its name cannot be changed and it cannot be disabled,
              because the application refers to it by name. Its permission set is still editable.
            </p>
          }

          <div class="field">
            <label for="roleName">Name</label>
            <input
              id="roleName"
              type="text"
              formControlName="name"
              placeholder="Site Supervisor"
              [attr.readonly]="editing()?.isSystemRole ? true : null"
            />
            @for (message of errorsFor('name'); track message) {
              <span class="field-error">{{ message }}</span>
            }
          </div>

          <div class="field">
            <label for="roleDescription">Description</label>
            <textarea
              id="roleDescription"
              formControlName="description"
              placeholder="What this role is for and who should hold it."
            ></textarea>
          </div>

          @if (editing() && !editing()!.isSystemRole) {
            <label class="checkbox">
              <input type="checkbox" formControlName="isActive" />
              Role is active and can be assigned
            </label>
          }

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
            {{ editing() ? 'Save changes' : 'Create role' }}
          </button>
        </div>
      </form>
    </dp-modal>

    <!-- Permission editor -->
    <dp-modal
      [open]="permissionsFor() !== null"
      [title]="'Permissions · ' + (permissionsFor()?.name ?? '')"
      width="720px"
      (closed)="permissionsFor.set(null)"
    >
      @if (permissionsFor(); as role) {
        <div class="panel-body stack">
          <div class="row row-wrap">
            <span class="text-2 small">
              {{ selected().length }} of {{ catalog().length }} selected
            </span>
            <span class="spacer"></span>
            <button type="button" class="btn btn-sm btn-ghost" (click)="selectAll()">Select all</button>
            <button type="button" class="btn btn-sm btn-ghost" (click)="selectNone()">Clear all</button>
          </div>

          @if (role.name === 'SuperAdmin') {
            <p class="notice small">
              The SuperAdmin role must keep every permission. Without it, one edit could remove
              the ability to undo that edit, leaving nobody able to administer the application.
            </p>
          }

          <div class="catalog">
            @for (group of catalogByCategory(); track group.category) {
              <fieldset>
                <legend>
                  {{ group.category }}
                  <button
                    type="button"
                    class="btn btn-sm btn-ghost"
                    (click)="toggleCategory(group.permissions)"
                  >
                    {{ allSelected(group.permissions) ? 'none' : 'all' }}
                  </button>
                </legend>

                @for (permission of group.permissions; track permission.key) {
                  <label class="perm">
                    <input
                      type="checkbox"
                      [checked]="selected().includes(permission.key)"
                      (change)="togglePermission(permission.key)"
                    />
                    <span class="perm-body">
                      <span class="perm-name">{{ permission.name }}</span>
                      <span class="perm-key mono text-3">{{ permission.key }}</span>
                      @if (permission.description) {
                        <span class="perm-desc text-2 small">{{ permission.description }}</span>
                      }
                    </span>
                  </label>
                }
              </fieldset>
            }
          </div>

          @if (formError()) {
            <span class="field-error">{{ formError() }}</span>
          }

          <p class="field-hint">
            Users holding this role keep their current permissions until their next sign-in or
            token refresh, because permissions travel inside the access token.
          </p>
        </div>

        <div class="panel-foot row">
          <span class="spacer"></span>
          <button type="button" class="btn" (click)="permissionsFor.set(null)">Cancel</button>
          <button type="button" class="btn btn-primary" (click)="savePermissions(role)" [disabled]="saving()">
            @if (saving()) {
              <span class="spinner"></span>
            }
            Save permissions
          </button>
        </div>
      }
    </dp-modal>
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `
      .desc { max-width: 52ch; }

      .tag {
        margin-left: var(--sp-2);
        font-size: var(--fs-meta);
        font-weight: var(--fw-normal);
        color: var(--text-3);
      }

      tr.inactive td { color: var(--text-3); }

      .coverage {
        display: inline-flex;
        gap: var(--sp-2);
        align-items: center;
        white-space: nowrap;
      }

      .meter {
        position: relative;
        width: 64px;
        height: 4px;
        background: var(--panel-3);
      }

      .meter span {
        position: absolute;
        inset: 0 auto 0 0;
        background: var(--accent);
      }

      .notice {
        margin: 0;
        padding: var(--sp-2) var(--sp-3);
        color: var(--warn);
        background: var(--warn-wash);
        border-radius: var(--r-md);
      }

      .catalog {
        display: grid;
        gap: var(--sp-4);
        max-height: 50vh;
        overflow-y: auto;
        padding-right: var(--sp-1);
      }

      fieldset {
        margin: 0;
        padding: var(--sp-2) var(--sp-3) var(--sp-3);
        border: 1px solid var(--line);
        border-radius: var(--r-md);
      }

      legend {
        display: flex;
        gap: var(--sp-2);
        align-items: center;
        padding: 0 var(--sp-1);
        font-size: 0.78rem;
        font-weight: 600;
        color: var(--text-2);
      }

      .perm {
        display: grid;
        grid-template-columns: 16px 1fr;
        gap: var(--sp-2);
        align-items: start;
        padding: var(--sp-1) 0;
        cursor: pointer;
      }

      .perm input {
        margin-top: var(--sp-1);
        width: 15px;
        height: 15px;
        accent-color: var(--accent);
      }

      .perm-body { display: grid; gap: 2px; }
      .perm-name { font-size: 0.85rem; }
      .perm-key { font-size: 0.7rem; }
      .perm-desc { line-height: 1.35; }
    `,
  ],
})
export class RoleListComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly notifications = inject(NotificationService);
  private readonly fb = inject(FormBuilder);

  readonly perm = Permissions;

  readonly canManageRoles = computed(() => this.auth.has(Permissions.roleManage));
  readonly canManagePermissions = computed(() => this.auth.has(Permissions.permissionManage));

  readonly roles = signal<Role[]>([]);
  readonly catalog = signal<Permission[]>([]);
  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly formOpen = signal(false);
  readonly editing = signal<Role | null>(null);
  readonly permissionsFor = signal<Role | null>(null);
  readonly selected = signal<string[]>([]);
  readonly formError = signal<string | null>(null);

  private serverErrors: Record<string, string[]> = {};

  readonly form = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(100)]],
    description: [''],
    isActive: [true],
  });

  /** Grouped by the category the backend declares, which keeps a 28-item list navigable. */
  readonly catalogByCategory = computed(() => {
    const groups = new Map<string, Permission[]>();

    for (const permission of this.catalog()) {
      const existing = groups.get(permission.category);

      if (existing) {
        existing.push(permission);
      } else {
        groups.set(permission.category, [permission]);
      }
    }

    return [...groups.entries()]
      .map(([category, permissions]) => ({ category, permissions }))
      .sort((a, b) => a.category.localeCompare(b.category));
  });

  ngOnInit(): void {
    this.load();

    if (this.auth.has(Permissions.permissionView)) {
      this.api.getPermissionCatalog().subscribe({ next: (catalog) => this.catalog.set(catalog) });
    }
  }

  private load(): void {
    this.loading.set(true);

    this.api.getRoles().subscribe({
      next: (roles) => {
        this.roles.set(roles);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  openCreate(): void {
    this.serverErrors = {};
    this.formError.set(null);
    this.editing.set(null);
    this.form.reset({ name: '', description: '', isActive: true });
    this.formOpen.set(true);
  }

  openEdit(role: Role): void {
    this.serverErrors = {};
    this.formError.set(null);
    this.editing.set(role);
    this.form.reset({ name: role.name, description: role.description ?? '', isActive: role.isActive });
    this.formOpen.set(true);
  }

  openPermissions(role: Role): void {
    this.formError.set(null);
    this.permissionsFor.set(role);
    this.selected.set([...role.permissions]);
  }

  errorsFor(field: string): string[] {
    const control = this.form.get(field);
    const messages: string[] = [];

    if (control && (control.dirty || control.touched)) {
      if (control.hasError('required')) messages.push('This field is required.');
      if (control.hasError('maxlength')) messages.push('That is too long.');
    }

    return [...messages, ...(this.serverErrors[field] ?? [])];
  }

  togglePermission(key: string): void {
    this.selected.update((current) =>
      current.includes(key) ? current.filter((k) => k !== key) : [...current, key],
    );
  }

  toggleCategory(permissions: Permission[]): void {
    const keys = permissions.map((p) => p.key);

    if (this.allSelected(permissions)) {
      this.selected.update((current) => current.filter((k) => !keys.includes(k)));
    } else {
      this.selected.update((current) => [...new Set([...current, ...keys])]);
    }
  }

  allSelected(permissions: Permission[]): boolean {
    const selected = this.selected();
    return permissions.every((p) => selected.includes(p.key));
  }

  selectAll(): void {
    this.selected.set(this.catalog().map((p) => p.key));
  }

  selectNone(): void {
    this.selected.set([]);
  }

  save(): void {
    this.serverErrors = {};
    this.formError.set(null);

    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.saving.set(true);
    const value = this.form.getRawValue();
    const role = this.editing();

    const failed = (error: unknown) => {
      this.saving.set(false);
      this.serverErrors = fieldErrorsFrom(error);

      if (Object.keys(this.serverErrors).length === 0) {
        this.formError.set(messageFrom(error, 'Could not save the role.'));
      }
    };

    if (role) {
      this.api
        .updateRole(role.roleId, {
          name: value.name.trim(),
          description: value.description.trim() || null,
          isActive: role.isSystemRole ? true : value.isActive,
        })
        .subscribe({
          next: () => {
            this.saving.set(false);
            this.formOpen.set(false);
            this.notifications.success('Role updated.');
            this.load();
          },
          error: failed,
        });

      return;
    }

    this.api
      .createRole({
        name: value.name.trim(),
        description: value.description.trim() || null,
        // A new role starts with no permissions; they are attached in the permission editor,
        // which keeps the creation form short and the grant explicit.
        permissions: [],
      })
      .subscribe({
        next: (created) => {
          this.saving.set(false);
          this.formOpen.set(false);
          this.notifications.success(`${created.name} created. Now attach its permissions.`);
          this.load();
          this.openPermissions(created);
        },
        error: failed,
      });
  }

  savePermissions(role: Role): void {
    this.saving.set(true);
    this.formError.set(null);

    this.api.setRolePermissions(role.roleId, this.selected()).subscribe({
      next: (updated) => {
        this.saving.set(false);
        this.permissionsFor.set(null);

        this.notifications.success(
          `${updated.name} now holds ${updated.permissions.length} permission(s).`,
          'Affected users pick this up at their next sign-in.',
        );

        this.load();
      },
      error: (error: unknown) => {
        this.saving.set(false);
        this.formError.set(messageFrom(error, 'Could not save the permissions.'));
      },
    });
  }
}
