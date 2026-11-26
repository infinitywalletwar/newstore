import { Injectable } from '@angular/core';
import { HttpTransportType, HubConnection, HubConnectionBuilder } from '@microsoft/signalr';
import { PositionMessage, ShipmentDetails } from '../api/models';

export interface TrackingHandlers {
  snapshot(details: ShipmentDetails): void;
  position(message: PositionMessage): void;
  connection(state: 'connected' | 'reconnecting'): void;
}

export interface TrackingSubscription {
  stop(): Promise<void>;
}

/** The pause before a new connection when the old one closed for good, or when the first connect failed. */
export const REOPEN_DELAY_MS = 2000;

const RETRY_DELAYS_MS = [0, 1000, 2000, 5000];

/** The automatic reconnect policy: 0, 1, 2, then every 5 seconds. It never returns null, so it never gives up. */
export function nextRetryDelay(previousRetryCount: number): number {
  return RETRY_DELAYS_MS[previousRetryCount] ?? 5000;
}

@Injectable({ providedIn: 'root' })
export class TrackingClient {
  /** Connects, calls Track, and calls it again after every reconnect. Resolves after the first snapshot. */
  async track(shipmentId: string, handlers: TrackingHandlers): Promise<TrackingSubscription> {
    const session = new TrackingSession(shipmentId, handlers);
    await session.begin();
    return session;
  }
}

/**
 * One tracked shipment. The hub forgets a connection's groups when the connection goes, so every way back to a working
 * connection ends in the same place: start, then `Track`. A failure anywhere on that path drops the connection and
 * tries again, until `stop()`.
 */
class TrackingSession implements TrackingSubscription {
  private stopped = false;
  private connection?: HubConnection;
  private timer?: ReturnType<typeof setTimeout>;
  private firstSnapshot?: () => void;

  constructor(
    private readonly shipmentId: string,
    private readonly handlers: TrackingHandlers,
  ) {}

  begin(): Promise<void> {
    return new Promise<void>((resolve) => {
      this.firstSnapshot = resolve;
      void this.open();
    });
  }

  async stop(): Promise<void> {
    this.stopped = true;
    clearTimeout(this.timer);
    const connection = this.connection;
    this.connection = undefined;
    await connection?.stop();
  }

  private async open(): Promise<void> {
    if (this.stopped) {
      return;
    }
    // The browser sends the session cookie with the upgrade by itself. A WebSocket can't carry a header anyway.
    const connection = new HubConnectionBuilder()
      .withUrl('/api/hubs/tracking', { transport: HttpTransportType.WebSockets, skipNegotiation: true })
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (context) => nextRetryDelay(context.previousRetryCount) })
      .build();
    this.connection = connection;

    connection.on('position', (message: PositionMessage) => this.handlers.position(message));
    connection.onreconnecting(() => this.handlers.connection('reconnecting'));
    connection.onreconnected(async () => {
      try {
        await this.subscribe(connection);
      } catch {
        this.drop(connection);
      }
    });
    // Closed for good: the server closed it (the token expired) or the automatic reconnect was stopped.
    connection.onclose(() => this.drop(connection));

    try {
      await connection.start();
      await this.subscribe(connection);
    } catch {
      this.drop(connection);
    }
  }

  private async subscribe(connection: HubConnection): Promise<void> {
    const snapshot = await connection.invoke<ShipmentDetails>('Track', this.shipmentId);
    if (this.connection !== connection) {
      return;
    }
    this.handlers.snapshot(snapshot);
    this.handlers.connection('connected');
    this.firstSnapshot?.();
    this.firstSnapshot = undefined;
  }

  /** Forgets `connection` and opens a new one after a pause. Called from every failure path, so the first caller wins. */
  private drop(connection: HubConnection): void {
    if (this.stopped || this.connection !== connection) {
      return;
    }
    this.connection = undefined;
    this.handlers.connection('reconnecting');
    void connection.stop().catch(() => {});
    this.timer = setTimeout(() => void this.open(), REOPEN_DELAY_MS);
  }
}
