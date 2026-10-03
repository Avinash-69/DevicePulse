import { Component, OnInit, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Observable } from 'rxjs';

import { ApiService } from '../../core/services/api.service';
import { Permissions } from '../../core/auth/permissions';
import { NotificationService } from '../../core/services/notification.service';
import { messageFrom } from '../../core/interceptors/error.interceptor';
import { DeviceType, Location } from '../../core/models/api.models';
import {
  EmptyStateComponent,
  IfPermittedComponent,
  ModalComponent,
  PageHeaderComponent,
} from '../../shared/components/ui.components';

type Kind = 'deviceType' | 'location';

/**
 * Device types and locations — the reference data devices are classified by (§4.2).
 *
 * Neither can be deleted, by design. Both are referenced by live devices, so a delete would
 * either orphan those rows or cascade away real history. Deactivating removes the option from
 * the "register a device" dropdown while leaving existing devices untouched, which is what is
 * actually wanted when a hardware model is discontinued or a site closes.
 */
@Component({
  selector: 'dp-reference-data',
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
        title="Reference data"
      >
        <label class="checkbox">
          <input type="checkbox" [checked]="includeInactive()" (change)="toggleInactive()" />
          Show inactive
        </label>
      </dp-page-header>

      <div class="split split-even">
        <!-- Device types -->
        <section class="card">
          <div class="card-header">
            <h2>Device types</h2>
            <span class="spacer"></span>
            <dp-if-permitted [permission]="perm.referenceDataManage">
              <button type="button" class="btn btn-sm btn-primary" (click)="openCreate('deviceType')">
                Add type
              </button>
            </dp-if-permitted>
          </div>

          @if (loadingTypes()) {
            <div class="skeleton" style="height: 180px; margin: 16px"></div>
          } @else if (deviceTypes().length === 0) {
            <dp-empty
              title="No device types"
              message="A device needs a type before it can be registered."
            />
          } @else {
            <div class="table-wrap">
              <table class="data">
                <thead>
                  <tr>
                    <th>Name</th>
                    <th>Description</th>
                    <th class="right">Devices</th>
                    <th>Status</th>
                    <th class="right"></th>
                  </tr>
                </thead>
                <tbody>
                  @for (type of deviceTypes(); track type.deviceTypeId) {
                    <tr [class.inactive]="!type.isActive">
                      <td>{{ type.name }}</td>
                      <td class="muted small">{{ type.description ?? '—' }}</td>
                      <td class="right mono">{{ type.deviceCount }}</td>
                      <td>
                        <span class="badge" [class]="type.isActive ? 'badge-ok' : 'badge-neutral'">
                          {{ type.isActive ? 'active' : 'inactive' }}
                        </span>
                      </td>
                      <td class="right">
                        <dp-if-permitted [permission]="perm.referenceDataManage">
                          <button type="button" class="btn btn-sm btn-ghost" (click)="openEditType(type)">
                            Edit
                          </button>
                        </dp-if-permitted>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        </section>

        <!-- Locations -->
        <section class="card">
          <div class="card-header">
            <h2>Locations</h2>
            <span class="spacer"></span>
            <dp-if-permitted [permission]="perm.referenceDataManage">
              <button type="button" class="btn btn-sm btn-primary" (click)="openCreate('location')">
                Add location
              </button>
            </dp-if-permitted>
          </div>

          @if (loadingLocations()) {
            <div class="skeleton" style="height: 180px; margin: 16px"></div>
          } @else if (locations().length === 0) {
            <dp-empty
              title="No locations"
              message="A device needs a location before it can be registered."
            />
          } @else {
            <div class="table-wrap">
              <table class="data">
                <thead>
                  <tr>
                    <th>Name</th>
                    <th>Description</th>
                    <th class="right">Devices</th>
                    <th>Status</th>
                    <th class="right"></th>
                  </tr>
                </thead>
                <tbody>
                  @for (location of locations(); track location.locationId) {
                    <tr [class.inactive]="!location.isActive">
                      <td>{{ location.name }}</td>
                      <td class="muted small">{{ location.description ?? '—' }}</td>
                      <td class="right mono">{{ location.deviceCount }}</td>
                      <td>
                        <span class="badge" [class]="location.isActive ? 'badge-ok' : 'badge-neutral'">
                          {{ location.isActive ? 'active' : 'inactive' }}
                        </span>
                      </td>
                      <td class="right">
                        <dp-if-permitted [permission]="perm.referenceDataManage">
                          <button
                            type="button"
                            class="btn btn-sm btn-ghost"
                            (click)="openEditLocation(location)"
                          >
                            Edit
                          </button>
                        </dp-if-permitted>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        </section>
      </div>

      <p class="footnote muted small">
        Neither can be deleted. Both are referenced by devices, so removing one would either
        orphan those devices or take their history with it. Deactivating hides a type or location
        from new registrations while leaving existing devices untouched.
      </p>
    </div>

    <dp-modal [open]="formOpen()" [title]="dialogTitle()" (closed)="formOpen.set(false)">
      <form [formGroup]="form" (ngSubmit)="save()">
        <div class="card-body stack">
          <div class="field">
            <label for="refName">Name</label>
            <input
              id="refName"
              type="text"
              formControlName="name"
              [placeholder]="kind() === 'deviceType' ? 'Cold Chain Monitor' : 'Pune — Cold Storage'"
            />
            @if (invalid('name')) {
              <span class="field-error">A name is required.</span>
            }
          </div>

          <div class="field">
            <label for="refDescription">Description</label>
            <textarea id="refDescription" formControlName="description"></textarea>
          </div>

          @if (editingId() !== null) {
            <label class="checkbox">
              <input type="checkbox" formControlName="isActive" />
              Available for new device registrations
            </label>
            <span class="field-hint">
              Deactivating is refused while devices still use it — reassign them first.
            </span>
          }

          @if (formError()) {
            <span class="field-error">{{ formError() }}</span>
          }
        </div>

        <div class="card-footer row">
          <span class="spacer"></span>
          <button type="button" class="btn" (click)="formOpen.set(false)">Cancel</button>
          <button type="submit" class="btn btn-primary" [disabled]="saving()">
            @if (saving()) {
              <span class="spinner"></span>
            }
            Save
          </button>
        </div>
      </form>
    </dp-modal>
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `

      @media (max-width: 1200px) {
      }

      tr.inactive td { opacity: 0.6; }

      .footnote {
        margin: var(--sp-4) 0 0;
        max-width: 80ch;
        line-height: 1.5;
      }
    `,
  ],
})
export class ReferenceDataComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly notifications = inject(NotificationService);
  private readonly fb = inject(FormBuilder);

  readonly perm = Permissions;

  readonly deviceTypes = signal<DeviceType[]>([]);
  readonly locations = signal<Location[]>([]);
  readonly loadingTypes = signal(true);
  readonly loadingLocations = signal(true);
  readonly includeInactive = signal(false);

  readonly formOpen = signal(false);
  readonly saving = signal(false);
  readonly kind = signal<Kind>('deviceType');
  readonly editingId = signal<number | null>(null);
  readonly formError = signal<string | null>(null);

  readonly form = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(150)]],
    description: [''],
    isActive: [true],
  });

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
    this.loadingTypes.set(true);
    this.loadingLocations.set(true);

    this.api.getDeviceTypes(this.includeInactive()).subscribe({
      next: (types) => {
        this.deviceTypes.set(types);
        this.loadingTypes.set(false);
      },
      error: () => this.loadingTypes.set(false),
    });

    this.api.getLocations(this.includeInactive()).subscribe({
      next: (locations) => {
        this.locations.set(locations);
        this.loadingLocations.set(false);
      },
      error: () => this.loadingLocations.set(false),
    });
  }

  toggleInactive(): void {
    this.includeInactive.update((current) => !current);
    this.load();
  }

  dialogTitle(): string {
    const noun = this.kind() === 'deviceType' ? 'device type' : 'location';
    return this.editingId() === null ? `Add ${noun}` : `Edit ${noun}`;
  }

  invalid(field: string): boolean {
    const control = this.form.get(field);
    return Boolean(control?.invalid && (control.dirty || control.touched));
  }

  openCreate(kind: Kind): void {
    this.kind.set(kind);
    this.editingId.set(null);
    this.formError.set(null);
    this.form.reset({ name: '', description: '', isActive: true });
    this.formOpen.set(true);
  }

  openEditType(type: DeviceType): void {
    this.kind.set('deviceType');
    this.editingId.set(type.deviceTypeId);
    this.formError.set(null);
    this.form.reset({ name: type.name, description: type.description ?? '', isActive: type.isActive });
    this.formOpen.set(true);
  }

  openEditLocation(location: Location): void {
    this.kind.set('location');
    this.editingId.set(location.locationId);
    this.formError.set(null);
    this.form.reset({
      name: location.name,
      description: location.description ?? '',
      isActive: location.isActive,
    });
    this.formOpen.set(true);
  }

  save(): void {
    this.formError.set(null);

    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.saving.set(true);

    const value = this.form.getRawValue();
    const name = value.name.trim();
    const description = value.description.trim() || null;
    const id = this.editingId();
    const isDeviceType = this.kind() === 'deviceType';

    const done = () => {
      this.saving.set(false);
      this.formOpen.set(false);
      this.notifications.success('Saved.');
      this.load();
    };

    const failed = (error: unknown) => {
      this.saving.set(false);
      this.formError.set(messageFrom(error, 'Could not save.'));
    };

    if (id === null) {
      const create$: Observable<DeviceType | Location> = isDeviceType
        ? this.api.createDeviceType(name, description)
        : this.api.createLocation(name, description);

      create$.subscribe({ next: done, error: failed });
      return;
    }

    const update$: Observable<DeviceType | Location> = isDeviceType
      ? this.api.updateDeviceType(id, name, description, value.isActive)
      : this.api.updateLocation(id, name, description, value.isActive);

    update$.subscribe({ next: done, error: failed });
  }
}
