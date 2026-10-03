import { provideHttpClient, withXhr } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { environment } from '../../../environments/environment';
import { AlertChangedEvent, DeviceStatusChangedEvent } from '../models/api.models';
import {
  LIVE_CONNECTION_FACTORY,
  LIVE_HUB_URL,
  LiveConnection,
  RealtimeService,
  expiresWithin,
  retryDelay,
} from './realtime.service';

/** Stands in for a SignalR HubConnection, letting each test drive the server side. */
class FakeConnection implements LiveConnection {
  readonly handlers = new Map<string, (payload: never) => void>();
  reconnecting = () => undefined as void;
  reconnected = () => undefined as void;
  closed = () => undefined as void;

  startCalls = 0;
  failStarts = 0;
  stopped = false;

  constructor(readonly accessTokenFactory: () => Promise<string>) {}

  on(method: string, handler: (payload: never) => void): void {
    this.handlers.set(method, handler);
  }

  onreconnecting(callback: () => void): void {
    this.reconnecting = callback;
  }

  onreconnected(callback: () => void): void {
    this.reconnected = callback;
  }

  onclose(callback: () => void): void {
    this.closed = callback;
  }

  start(): Promise<void> {
    this.startCalls++;

    if (this.failStarts > 0) {
      this.failStarts--;
      return Promise.reject(new Error('API unreachable'));
    }

    return Promise.resolve();
  }

  stop(): Promise<void> {
    this.stopped = true;
    return Promise.resolve();
  }

  push<T>(method: string, payload: T): void {
    (this.handlers.get(method) as (payload: T) => void)(payload);
  }
}

/** A JWT whose only meaningful content is its expiry, which is all the service reads. */
function tokenExpiringIn(ms: number): string {
  const payload = btoa(JSON.stringify({ exp: Math.floor((Date.now() + ms) / 1000) }))
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
    .replace(/=+$/, '');

  return `header.${payload}.signature`;
}

const flush = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

const alertRaised: AlertChangedEvent = {
  alertId: 41,
  deviceId: 3,
  change: 'Raised',
  severity: 'Critical',
  status: 'Open',
  message: 'Freezer 1 is at 41 °C.',
  occurredAt: new Date().toISOString(),
};

const wentOffline: DeviceStatusChangedEvent = {
  deviceId: 3,
  deviceCode: 'FRZ-1',
  deviceName: 'Freezer 1',
  connectivityStatus: 'Offline',
  lifecycleStatus: 'Active',
  lastSeenAt: null,
  occurredAt: new Date().toISOString(),
};

describe('RealtimeService', () => {
  let service: RealtimeService;
  let connections: FakeConnection[];
  let failNextStarts: number;
  let http: HttpTestingController;

  const connection = () => connections[connections.length - 1];

  beforeEach(() => {
    localStorage.clear();
    connections = [];
    failNextStarts = 0;

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withXhr()),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: LIVE_CONNECTION_FACTORY,
          useValue: (url: string, accessTokenFactory: () => Promise<string>) => {
            expect(url).toBe(LIVE_HUB_URL);
            const fake = new FakeConnection(accessTokenFactory);
            fake.failStarts = failNextStarts;
            connections.push(fake);
            return fake;
          },
        },
      ],
    });

    service = TestBed.inject(RealtimeService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    service.disconnect();
    http.verify();
    localStorage.clear();
    vi.useRealTimers();
  });

  it('points at the hub path the API maps', () => {
    expect(LIVE_HUB_URL).toBe(`${environment.apiBaseUrl}/hubs/live`);
  });

  it('goes live once the connection starts, and opens only one connection', async () => {
    expect(service.state()).toBe('offline');

    service.connect();
    service.connect();
    expect(service.state()).toBe('connecting');

    await flush();

    expect(connections.length).toBe(1);
    expect(service.isLive()).toBe(true);
  });

  it('relays pushed alert and device events', async () => {
    const alerts: AlertChangedEvent[] = [];
    const devices: DeviceStatusChangedEvent[] = [];

    service.alertChanged$.subscribe((e) => alerts.push(e));
    service.deviceStatusChanged$.subscribe((e) => devices.push(e));

    service.connect();
    await flush();

    connection().push('alertChanged', alertRaised);
    connection().push('deviceStatusChanged', wentOffline);

    expect(alerts).toEqual([alertRaised]);
    expect(devices).toEqual([wentOffline]);
  });

  it('coalesces a burst of events into one refresh', async () => {
    vi.useFakeTimers();

    let refreshes = 0;
    service.refreshes({ alerts: true }).subscribe(() => refreshes++);

    service.connect();
    await vi.advanceTimersByTimeAsync(0);

    for (let i = 0; i < 40; i++) {
      connection().push('alertChanged', { ...alertRaised, alertId: i });
    }

    await vi.advanceTimersByTimeAsync(1000);

    expect(refreshes).toBe(1);
  });

  it('only refreshes for the kinds of data asked for', async () => {
    vi.useFakeTimers();

    let refreshes = 0;
    service.refreshes({ alerts: true }).subscribe(() => refreshes++);

    service.connect();
    await vi.advanceTimersByTimeAsync(0);

    connection().push('deviceStatusChanged', wentOffline);
    await vi.advanceTimersByTimeAsync(1000);

    expect(refreshes).toBe(0);
  });

  it('asks screens to re-fetch after a reconnection, since events were missed', async () => {
    vi.useFakeTimers();

    let refreshes = 0;
    service.refreshes({ devices: true }).subscribe(() => refreshes++);

    service.connect();
    await vi.advanceTimersByTimeAsync(0);
    await vi.advanceTimersByTimeAsync(1000);

    // The first connection is not a resync: the screens have only just loaded.
    expect(refreshes).toBe(0);

    connection().reconnecting();
    expect(service.state()).toBe('reconnecting');

    connection().reconnected();
    expect(service.isLive()).toBe(true);

    await vi.advanceTimersByTimeAsync(1000);
    expect(refreshes).toBe(1);
  });

  it('keeps retrying a first connection the API refused, with a growing delay', async () => {
    vi.useFakeTimers();
    failNextStarts = 2;

    service.connect();
    await vi.advanceTimersByTimeAsync(0);

    expect(service.state()).toBe('reconnecting');
    expect(connection().startCalls).toBe(1);

    await vi.advanceTimersByTimeAsync(retryDelay(0));
    expect(connection().startCalls).toBe(2);

    await vi.advanceTimersByTimeAsync(retryDelay(1));
    expect(connection().startCalls).toBe(3);
    expect(service.isLive()).toBe(true);
  });

  it('restarts after the API closes the connection, as it does when the token expires', async () => {
    vi.useFakeTimers();

    service.connect();
    await vi.advanceTimersByTimeAsync(0);

    connection().closed();
    expect(service.state()).toBe('reconnecting');

    await vi.advanceTimersByTimeAsync(retryDelay(0));

    expect(connection().startCalls).toBe(2);
    expect(service.isLive()).toBe(true);
  });

  it('stops for good on disconnect', async () => {
    vi.useFakeTimers();

    service.connect();
    await vi.advanceTimersByTimeAsync(0);

    const opened = connection();
    service.disconnect();
    opened.closed();

    await vi.advanceTimersByTimeAsync(60_000);

    expect(opened.stopped).toBe(true);
    expect(opened.startCalls).toBe(1);
    expect(service.state()).toBe('offline');
  });

  it('hands the hub the stored token while it is still valid', async () => {
    const token = tokenExpiringIn(10 * 60 * 1000);
    localStorage.setItem('devicepulse.accessToken', token);

    service.connect();

    await expect(connection().accessTokenFactory()).resolves.toBe(token);
  });

  it('refreshes a token about to expire before handing it to the hub', async () => {
    localStorage.setItem('devicepulse.accessToken', tokenExpiringIn(5_000));
    localStorage.setItem('devicepulse.refreshToken', 'refresh-token-1');

    service.connect();
    const pending = connection().accessTokenFactory();

    const fresh = tokenExpiringIn(10 * 60 * 1000);
    http.expectOne(`${environment.apiBaseUrl}/auth/refresh`).flush({
      accessToken: fresh,
      refreshToken: 'refresh-token-2',
      accessTokenExpiresAt: new Date(Date.now() + 10 * 60 * 1000).toISOString(),
      user: { userId: 1, name: 'Ops', email: 'ops@devicepulse.test', roles: [], permissions: [] },
    });

    await expect(pending).resolves.toBe(fresh);
  });
});

describe('expiresWithin', () => {
  it('reads the expiry from a JWT', () => {
    expect(expiresWithin(tokenExpiringIn(60_000), 30_000)).toBe(false);
    expect(expiresWithin(tokenExpiringIn(10_000), 30_000)).toBe(true);
  });

  it('treats an unreadable token as expired', () => {
    expect(expiresWithin('not-a-jwt', 30_000)).toBe(true);
  });
});
