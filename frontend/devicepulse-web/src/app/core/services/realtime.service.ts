import { Injectable, InjectionToken, inject, signal } from '@angular/core';
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Observable, Subject, auditTime, firstValueFrom, map, merge } from 'rxjs';

import { environment } from '../../../environments/environment';
import { AuthService } from '../auth/auth.service';
import { AlertChangedEvent, DeviceStatusChangedEvent } from '../models/api.models';

/** Must match LiveHub.Path and LiveEvents on the API. */
export const LIVE_HUB_URL = `${environment.apiBaseUrl}/hubs/live`;
const ALERT_CHANGED = 'alertChanged';
const DEVICE_STATUS_CHANGED = 'deviceStatusChanged';

/**
 * offline: not connected and not trying (signed out, or never started).
 * connecting: the first attempt is in flight.
 * live: events are arriving.
 * reconnecting: the connection dropped and is being re-established; screens fall back to polling.
 */
export type LiveState = 'offline' | 'connecting' | 'live' | 'reconnecting';

/** The slice of a SignalR HubConnection this service uses, so tests can supply a fake. */
export interface LiveConnection {
  on(method: string, handler: (payload: never) => void): void;
  onreconnecting(callback: () => void): void;
  onreconnected(callback: () => void): void;
  onclose(callback: () => void): void;
  start(): Promise<void>;
  stop(): Promise<void>;
}

export type LiveConnectionFactory = (url: string, accessTokenFactory: () => Promise<string>) => LiveConnection;

export const LIVE_CONNECTION_FACTORY = new InjectionToken<LiveConnectionFactory>('LIVE_CONNECTION_FACTORY', {
  providedIn: 'root',
  factory: () => (url, accessTokenFactory) =>
    new HubConnectionBuilder()
      .withUrl(url, { accessTokenFactory })
      // Retries indefinitely with a capped backoff. The library default gives up after four
      // attempts, which would leave a wall-mounted dashboard silently polling forever after one
      // API restart.
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (ctx) => retryDelay(ctx.previousRetryCount) })
      .configureLogging(LogLevel.Warning)
      .build(),
});

/** 1s, 2s, 4s ... capped at 30s. */
export function retryDelay(previousAttempts: number): number {
  return Math.min(30_000, 1000 * 2 ** previousAttempts);
}

/** A token is refreshed this close to expiry rather than sent and rejected. */
const EXPIRY_MARGIN_MS = 30_000;

/**
 * The live push channel from the API (§31).
 *
 * One connection for the whole app, opened by the shell when a user signs in and closed when
 * they sign out. Screens do not consume raw events to patch their own numbers; they ask for
 * {@link refreshes} and re-fetch, so the counting stays on the API and a missed event can only
 * make a screen late, never wrong.
 *
 * While the connection is down, {@link isLive} is false and screens keep their old polling as a
 * fallback. Every reconnection emits a refresh, because events sent while disconnected are gone.
 */
@Injectable({ providedIn: 'root' })
export class RealtimeService {
  private readonly auth = inject(AuthService);
  private readonly createConnection = inject(LIVE_CONNECTION_FACTORY);

  private readonly currentState = signal<LiveState>('offline');
  readonly state = this.currentState.asReadonly();

  private readonly alertChanges = new Subject<AlertChangedEvent>();
  private readonly deviceChanges = new Subject<DeviceStatusChangedEvent>();
  private readonly resyncs = new Subject<void>();

  readonly alertChanged$ = this.alertChanges.asObservable();
  readonly deviceStatusChanged$ = this.deviceChanges.asObservable();

  /** Emits when the connection comes back after a gap, during which events may have been lost. */
  readonly resynced$ = this.resyncs.asObservable();

  private connection: LiveConnection | null = null;
  private restartTimer: ReturnType<typeof setTimeout> | null = null;
  private restartAttempts = 0;
  private hasBeenLive = false;

  isLive(): boolean {
    return this.currentState() === 'live';
  }

  /** Opens the connection. Safe to call more than once. */
  connect(): void {
    if (this.connection) {
      return;
    }

    const connection = this.createConnection(LIVE_HUB_URL, () => this.accessToken());
    this.connection = connection;
    this.hasBeenLive = false;

    connection.on(ALERT_CHANGED, (event: AlertChangedEvent) => this.alertChanges.next(event));
    connection.on(DEVICE_STATUS_CHANGED, (event: DeviceStatusChangedEvent) => this.deviceChanges.next(event));

    connection.onreconnecting(() => this.currentState.set('reconnecting'));
    connection.onreconnected(() => this.becameLive());

    // Reached when the automatic reconnect cannot help — chiefly when the API closes the socket
    // because the token that opened it expired. A fresh start fetches a fresh token.
    connection.onclose(() => {
      if (this.connection === connection) {
        this.scheduleRestart();
      }
    });

    this.currentState.set('connecting');
    void this.start(connection);
  }

  /** Closes the connection and stops retrying. Called on sign-out. */
  disconnect(): void {
    const connection = this.connection;
    this.connection = null;

    if (this.restartTimer) {
      clearTimeout(this.restartTimer);
      this.restartTimer = null;
    }

    this.currentState.set('offline');
    void connection?.stop().catch(() => undefined);
  }

  /**
   * A stream that fires when a screen showing the given kinds of data should re-fetch: on a
   * relevant event, and after a reconnection. Bursts are coalesced — a bulk ingest that raises
   * forty alerts produces one refresh, not forty.
   */
  refreshes(kinds: { alerts?: boolean; devices?: boolean }, windowMs = 1000): Observable<void> {
    const sources: Observable<unknown>[] = [this.resynced$];

    if (kinds.alerts) sources.push(this.alertChanged$);
    if (kinds.devices) sources.push(this.deviceStatusChanged$);

    return merge(...sources).pipe(
      auditTime(windowMs),
      map(() => undefined),
    );
  }

  private async start(connection: LiveConnection): Promise<void> {
    try {
      await connection.start();
    } catch {
      // The initial start is not covered by the automatic reconnect, so it is retried here.
      if (this.connection === connection) {
        this.scheduleRestart();
      }
      return;
    }

    if (this.connection === connection) {
      this.becameLive();
    }
  }

  private scheduleRestart(): void {
    const connection = this.connection;

    if (!connection || this.restartTimer) {
      return;
    }

    this.currentState.set('reconnecting');

    this.restartTimer = setTimeout(() => {
      this.restartTimer = null;
      void this.start(connection);
    }, retryDelay(this.restartAttempts++));
  }

  private becameLive(): void {
    this.restartAttempts = 0;
    this.currentState.set('live');

    // The first connection needs no resync: every screen loaded its data moments ago.
    if (this.hasBeenLive) {
      this.resyncs.next();
    }

    this.hasBeenLive = true;
  }

  /**
   * Called by SignalR for every connection attempt. A browser cannot put a header on a
   * WebSocket, so this token travels in the query string; refreshing it first means a
   * reconnection after a long idle spell does not fail on an expired token.
   */
  private async accessToken(): Promise<string> {
    const token = this.auth.accessToken;

    if (!token || expiresWithin(token, EXPIRY_MARGIN_MS)) {
      return (await firstValueFrom(this.auth.refreshAccessToken())) ?? '';
    }

    return token;
  }
}

/** True when the JWT's exp is within marginMs of now, or cannot be read. */
export function expiresWithin(token: string, marginMs: number): boolean {
  try {
    const payload = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
    const { exp } = JSON.parse(atob(payload)) as { exp?: number };

    return typeof exp !== 'number' || exp * 1000 - Date.now() < marginMs;
  } catch {
    return true;
  }
}
