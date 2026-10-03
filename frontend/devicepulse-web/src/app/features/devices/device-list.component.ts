import { Component, OnInit, computed, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { debounceTime, distinctUntilChanged } from 'rxjs';

import { ApiService } from '../../core/services/api.service';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { NotificationService } from '../../core/services/notification.service';
import { fieldErrorsFrom } from '../../core/interceptors/error.interceptor';
import {
  ConnectivityStatus,
  Device,
  DeviceQuery,
  DeviceType,
  LifecycleStatus,
  Location,
  PagedResult,
} from '../../core/models/api.models';
import {
  ConnectivityBadgeComponent,
  EmptyStateComponent,
  IfPermittedComponent,
  LifecycleBadgeComponent,
  LoadingRowsComponent,
  ModalComponent,
  PageHeaderComponent,
  PaginatorComponent,
} from '../../shared/components/ui.components';
import { RelativeTimePipe } from '../../shared/utils/relative-time.pipe';

/** Device register: paged, filterable, sortable, with registration (§13). */
@Component({
  selector: 'dp-device-list',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    RouterLink,
    PageHeaderComponent,
    PaginatorComponent,
    ConnectivityBadgeComponent,
    LifecycleBadgeComponent,
    EmptyStateComponent,
    LoadingRowsComponent,
    ModalComponent,
    IfPermittedComponent,
    RelativeTimePipe
],
  template: `
    <div class="page">
      <dp-page-header title="Devices">
        <dp-if-permitted [permission]="perm.deviceCreate">
          <button type="button" class="btn btn-primary" (click)="openCreate()">Register device</button>
        </dp-if-permitted>
      </dp-page-header>

      <div class="panel">
        <!-- Filters -->
        <div class="toolbar">
          <div class="field search">
            <input
              type="search"
              placeholder="Search name, code, type or location"
              [formControl]="searchControl"
              aria-label="Search devices"
            />
          </div>

          <div class="field">
            <select [value]="query().connectivityStatus ?? ''" (change)="setConnectivity($event)" aria-label="Connectivity">
              <option value="">Any connectivity</option>
              <option value="Online">Online</option>
              <option value="Offline">Offline</option>
              <option value="Unknown">Never reported</option>
            </select>
          </div>

          <div class="field">
            <select [value]="query().lifecycleStatus ?? ''" (change)="setLifecycle($event)" aria-label="Lifecycle">
              <option value="">Active lifecycle</option>
              <option value="Registered">Registered</option>
              <option value="Active">Active</option>
              <option value="Inactive">Inactive</option>
              <option value="Retired">Retired</option>
            </select>
          </div>

          <div class="field">
            <select [value]="query().deviceTypeId ?? ''" (change)="setDeviceType($event)" aria-label="Device type">
              <option value="">Any type</option>
              @for (type of deviceTypes(); track type.deviceTypeId) {
                <option [value]="type.deviceTypeId">{{ type.name }}</option>
              }
            </select>
          </div>

          <div class="field">
            <select [value]="query().locationId ?? ''" (change)="setLocation($event)" aria-label="Location">
              <option value="">Any location</option>
              @for (location of locations(); track location.locationId) {
                <option [value]="location.locationId">{{ location.name }}</option>
              }
            </select>
          </div>

        </div>

        @if (activeFilters().length > 0) {
          <div class="chips filter-row">
            @for (chip of activeFilters(); track chip.field) {
              <span class="chip">
                <span class="chip-key">{{ chip.key }}</span>
                <span>{{ chip.value }}</span>
                <button
                  type="button"
                  [attr.aria-label]="'Remove ' + chip.key + ' filter'"
                  (click)="clearFilter(chip.field)"
                >
                  &times;
                </button>
              </span>
            }

            <button type="button" class="btn btn-sm btn-ghost" (click)="clearFilters()">
              Clear all
            </button>
          </div>
        }

        @if (loading()) {
          <dp-loading-rows [count]="6" />
        } @else {
        @if (result(); as page) {
          @if (page.items.length === 0) {
            <dp-empty
              title="No devices match"
              [message]="
                hasFilters()
                  ? 'Try widening the filters.'
                  : 'Register your first device, or start the simulator to create some.'
              "
            />
          } @else {
            <div class="table-wrap">
              <table class="data">
                <thead>
                  <tr>
                    <th [class]="sortClass('deviceName')" (click)="sortBy('deviceName')">
                      Device
                    </th>
                    <th [class]="sortClass('deviceCode')" (click)="sortBy('deviceCode')">
                      Code
                    </th>
                    <th>Type</th>
                    <th>Location</th>
                    <th [class]="sortClass('connectivityStatus')" (click)="sortBy('connectivityStatus')">
                      Connectivity
                    </th>
                    <th>Lifecycle</th>
                    <th [class]="sortClass('lastSeenAt')" (click)="sortBy('lastSeenAt')">
                      Last seen
                    </th>
                    <th class="right">Alerts</th>
                    <th>Key</th>
                  </tr>
                </thead>
                <tbody>
                  @for (device of page.items; track device.deviceId) {
                    <tr>
                      <td>
                        <a [routerLink]="['/devices', device.deviceId]">{{ device.deviceName }}</a>
                      </td>
                      <td class="mono subtle">{{ device.deviceCode }}</td>
                      <td class="muted">{{ device.deviceTypeName }}</td>
                      <td class="muted">{{ device.locationName }}</td>
                      <td><dp-connectivity [status]="device.connectivityStatus" /></td>
                      <td><dp-lifecycle [status]="device.lifecycleStatus" /></td>
                      <td class="muted small nowrap">
                        {{ device.lastSeenAt ? (device.lastSeenAt | relativeTime) : 'never' }}
                      </td>
                      <td class="right">
                        @if (device.openAlertCount > 0) {
                          <a
                            class="badge badge-danger"
                            [routerLink]="['/alerts']"
                            [queryParams]="{ deviceId: device.deviceId }"
                          >
                            {{ device.openAlertCount }}
                          </a>
                        } @else {
                          <span class="subtle">—</span>
                        }
                      </td>
                      <td>
                        @if (device.hasApiKey) {
                          <span class="badge badge-accent" title="An ingestion key has been issued">issued</span>
                        } @else {
                          <span class="subtle small">none</span>
                        }
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

    <!-- Register -->
    <dp-modal [open]="createOpen()" title="Register a device" (closed)="createOpen.set(false)">
      <form [formGroup]="createForm" (ngSubmit)="create()">
        <div class="panel-body stack">
          <div class="field">
            <label for="deviceCode">Device code</label>
            <input
              id="deviceCode"
              type="text"
              formControlName="deviceCode"
              placeholder="DP-0001"
              [class.invalid]="invalid('deviceCode')"
            />
            <span class="field-hint">
              Unique, permanent identifier. Letters, digits, hyphens and underscores only.
            </span>
            @for (message of errorsFor('deviceCode'); track message) {
              <span class="field-error">{{ message }}</span>
            }
          </div>

          <div class="field">
            <label for="deviceName">Display name</label>
            <input id="deviceName" type="text" formControlName="deviceName" [class.invalid]="invalid('deviceName')" />
            @for (message of errorsFor('deviceName'); track message) {
              <span class="field-error">{{ message }}</span>
            }
          </div>

          <div class="field">
            <label for="deviceTypeId">Device type</label>
            <select id="deviceTypeId" formControlName="deviceTypeId" [class.invalid]="invalid('deviceTypeId')">
              <option [value]="0" disabled>Choose a type</option>
              @for (type of deviceTypes(); track type.deviceTypeId) {
                <option [value]="type.deviceTypeId">{{ type.name }}</option>
              }
            </select>
            @for (message of errorsFor('deviceTypeId'); track message) {
              <span class="field-error">{{ message }}</span>
            }
          </div>

          <div class="field">
            <label for="locationId">Location</label>
            <select id="locationId" formControlName="locationId" [class.invalid]="invalid('locationId')">
              <option [value]="0" disabled>Choose a location</option>
              @for (location of locations(); track location.locationId) {
                <option [value]="location.locationId">{{ location.name }}</option>
              }
            </select>
            @for (message of errorsFor('locationId'); track message) {
              <span class="field-error">{{ message }}</span>
            }
          </div>

          @if (deviceTypes().length === 0 || locations().length === 0) {
            <p class="field-hint">
              A device needs a type and a location. Create them under
              <a routerLink="/reference-data">Reference data</a> first.
            </p>
          }
        </div>

        <div class="panel-foot row">
          <span class="spacer"></span>
          <button type="button" class="btn" (click)="createOpen.set(false)">Cancel</button>
          <button type="submit" class="btn btn-primary" [disabled]="saving()">
            @if (saving()) {
              <span class="spinner"></span>
            }
            Register
          </button>
        </div>
      </form>
    </dp-modal>
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `
      /* Sits directly under the toolbar, so the filters and what they are doing read together. */
      .filter-row {
        padding: var(--sp-2) var(--sp-3);
        border-bottom: 1px solid var(--line);
      }


      .toolbar .field { flex: 0 0 auto; }
      .toolbar .field select { width: auto; min-width: 150px; }
      .toolbar .search { flex: 1 1 260px; min-width: 200px; }

      a.badge { text-decoration: none; }
      a.badge:hover { text-decoration: underline; }
    `,
  ],
})
export class DeviceListComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly notifications = inject(NotificationService);
  private readonly fb = inject(FormBuilder);
  private readonly route = inject(ActivatedRoute);

  readonly perm = Permissions;

  readonly result = signal<PagedResult<Device> | null>(null);
  readonly deviceTypes = signal<DeviceType[]>([]);
  readonly locations = signal<Location[]>([]);
  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly createOpen = signal(false);

  readonly query = signal<DeviceQuery>({ page: 1, pageSize: 20, sortBy: 'deviceName', sortDescending: false });

  readonly searchControl = this.fb.nonNullable.control('');

  private serverErrors: Record<string, string[]> = {};

  readonly createForm = this.fb.nonNullable.group({
    deviceCode: ['', [Validators.required, Validators.pattern(/^[A-Za-z0-9_-]+$/)]],
    deviceName: ['', [Validators.required, Validators.maxLength(200)]],
    deviceTypeId: [0, [Validators.required, Validators.min(1)]],
    locationId: [0, [Validators.required, Validators.min(1)]],
  });

  ngOnInit(): void {
    // Seeded from the URL so a filtered list is linkable and the top bar's search can navigate
    // here rather than reaching into this component's state.
    const term = this.route.snapshot.queryParamMap.get('search') ?? '';

    if (term) {
      this.searchControl.setValue(term, { emitEvent: false });
      this.query.update((current) => ({ ...current, search: term }));
    }

    // A later search from the top bar arrives as a query-param change on a route that is
    // already active, which does not re-run ngOnInit.
    this.route.queryParamMap.subscribe((params) => {
      const next = params.get('search') ?? '';

      if (next !== (this.query().search ?? '')) {
        this.searchControl.setValue(next, { emitEvent: false });
        this.patchQuery({ search: next || undefined, page: 1 });
      }
    });

    this.load();
    this.loadReferenceData();

    // Debounced so typing a search term issues one request when the user stops, not one per
    // keystroke. distinctUntilChanged stops a re-search when the value has not actually changed.
    this.searchControl.valueChanges
      .pipe(debounceTime(300), distinctUntilChanged())
      .subscribe((term) => this.patchQuery({ search: term || undefined, page: 1 }));
  }

  private load(): void {
    this.loading.set(true);

    this.api.getDevices(this.query()).subscribe({
      next: (page) => {
        this.result.set(page);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  private loadReferenceData(): void {
    // Only fetched when the user can see reference data; otherwise these calls would be
    // guaranteed 403s and would fill the screen with toasts.
    if (!this.auth.has(Permissions.referenceDataView)) {
      return;
    }

    this.api.getDeviceTypes().subscribe({ next: (types) => this.deviceTypes.set(types) });
    this.api.getLocations().subscribe({ next: (locations) => this.locations.set(locations) });
  }

  private patchQuery(patch: Partial<DeviceQuery>): void {
    this.query.update((current) => ({ ...current, ...patch }));
    this.load();
  }

  goToPage(page: number): void {
    this.patchQuery({ page });
  }

  setPageSize(pageSize: number): void {
    this.patchQuery({ pageSize, page: 1 });
  }

  setConnectivity(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.patchQuery({ connectivityStatus: (value || undefined) as ConnectivityStatus | undefined, page: 1 });
  }

  setLifecycle(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.patchQuery({ lifecycleStatus: (value || undefined) as LifecycleStatus | undefined, page: 1 });
  }

  setDeviceType(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.patchQuery({ deviceTypeId: value ? Number(value) : undefined, page: 1 });
  }

  setLocation(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.patchQuery({ locationId: value ? Number(value) : undefined, page: 1 });
  }

  /** Clicking the active column flips direction; clicking another switches to it ascending. */
  sortBy(column: string): void {
    const current = this.query();

    this.patchQuery({
      sortBy: column,
      sortDescending: current.sortBy === column ? !current.sortDescending : false,
      page: 1,
    });
  }

  /**
   * The header's classes for the current sort.
   *
   * A class rather than an appended arrow: the glyph lives in a fixed slot in the stylesheet,
   * so the label does not shift sideways when the sort moves from one column to another.
   */
  sortClass(column: string): string {
    const current = this.query();

    if (current.sortBy !== column) return 'sortable';

    return current.sortDescending ? 'sortable sort-desc' : 'sortable sort-asc';
  }

  /**
   * The filters currently narrowing the list, as removable chips.
   *
   * Five dropdowns do not tell an operator what is being filtered without reading all five.
   * Naming each active filter, and letting it be dismissed individually, does.
   */
  readonly activeFilters = computed(() => {
    const q = this.query();
    const chips: { field: keyof DeviceQuery; key: string; value: string }[] = [];

    if (q.search) chips.push({ field: 'search', key: 'matching', value: q.search });
    if (q.connectivityStatus) chips.push({ field: 'connectivityStatus', key: 'link', value: q.connectivityStatus });
    if (q.lifecycleStatus) chips.push({ field: 'lifecycleStatus', key: 'lifecycle', value: q.lifecycleStatus });

    if (q.deviceTypeId) {
      const name = this.deviceTypes().find((t) => t.deviceTypeId === q.deviceTypeId)?.name;
      chips.push({ field: 'deviceTypeId', key: 'type', value: name ?? String(q.deviceTypeId) });
    }

    if (q.locationId) {
      const name = this.locations().find((l) => l.locationId === q.locationId)?.name;
      chips.push({ field: 'locationId', key: 'at', value: name ?? String(q.locationId) });
    }

    return chips;
  });

  clearFilter(field: keyof DeviceQuery): void {
    if (field === 'search') {
      this.searchControl.setValue('', { emitEvent: false });
    }

    this.patchQuery({ [field]: undefined, page: 1 } as Partial<DeviceQuery>);
  }

  hasFilters(): boolean {
    const q = this.query();
    return Boolean(q.search || q.connectivityStatus || q.lifecycleStatus || q.deviceTypeId || q.locationId);
  }

  clearFilters(): void {
    this.searchControl.setValue('', { emitEvent: false });
    this.query.set({ page: 1, pageSize: this.query().pageSize, sortBy: 'deviceName', sortDescending: false });
    this.load();
  }

  openCreate(): void {
    this.serverErrors = {};
    this.createForm.reset({ deviceCode: '', deviceName: '', deviceTypeId: 0, locationId: 0 });
    this.createOpen.set(true);
  }

  invalid(field: string): boolean {
    const control = this.createForm.get(field);
    return Boolean((control?.invalid && (control.dirty || control.touched)) || this.serverErrors[field]);
  }

  /**
   * Merges client-side messages with the field errors the API returned. Both are shown in the
   * same place, because to the user they are the same thing — the backend validates
   * independently (§24) and its complaints belong next to the field too.
   */
  errorsFor(field: string): string[] {
    const control = this.createForm.get(field);
    const messages: string[] = [];

    if (control && (control.dirty || control.touched)) {
      if (control.hasError('required')) messages.push('This field is required.');
      if (control.hasError('pattern')) messages.push('Only letters, digits, hyphens and underscores.');
      if (control.hasError('maxlength')) messages.push('That is too long.');
      if (control.hasError('min')) messages.push('Choose an option.');
    }

    // The API returns camelCase keys; also check the exact casing in case that ever changes.
    const fromServer = this.serverErrors[field] ?? this.serverErrors[field.toLowerCase()] ?? [];

    return [...messages, ...fromServer];
  }

  create(): void {
    this.serverErrors = {};

    if (this.createForm.invalid) {
      this.createForm.markAllAsTouched();
      return;
    }

    this.saving.set(true);
    const value = this.createForm.getRawValue();

    this.api
      .createDevice({
        deviceCode: value.deviceCode.trim(),
        deviceName: value.deviceName.trim(),
        deviceTypeId: Number(value.deviceTypeId),
        locationId: Number(value.locationId),
      })
      .subscribe({
        next: (device) => {
          this.saving.set(false);
          this.createOpen.set(false);
          this.notifications.success(`${device.deviceName} registered.`);
          this.load();
        },
        error: (error: unknown) => {
          this.saving.set(false);

          // Field errors are bound to the inputs; anything else was already toasted by the
          // error interceptor.
          this.serverErrors = fieldErrorsFrom(error);
        },
      });
  }
}
