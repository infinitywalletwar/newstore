import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { Position, PositionMessage, ShipmentDetails, ShipmentStatus } from '../api/models';
import { createMap } from '../maps/leaflet-map';
import { TrackingPageComponent } from './tracking-page.component';
import { REOPEN_DELAY_MS, TrackingClient } from './tracking-client';

// The real SignalR client needs a server and a WebSocket. The fake records how it is built and lets a test fire its events.
const hub = vi.hoisted(() => ({
  connections: [] as Array<Record<string, any>>,
  snapshot: undefined as unknown,
  startFails: 0,
}));
vi.mock('@microsoft/signalr', () => {
  class FakeConnection {
    handlers: Record<string, (...args: any[]) => any> = {};
    builder: FakeBuilder | undefined;
    start = vi.fn(async () => {
      if (hub.startFails > 0) {
        hub.startFails--;
        throw new Error('connection refused');
      }
    });
    stop = vi.fn(async () => {});
    invoke = vi.fn(async () => hub.snapshot);
    on = (name: string, handler: (...args: any[]) => any) => (this.handlers[name] = handler);
    onreconnecting = (handler: () => void) => (this.handlers['reconnecting'] = handler);
    onreconnected = (handler: () => void) => (this.handlers['reconnected'] = handler);
    onclose = (handler: () => void) => (this.handlers['close'] = handler);
  }
  class FakeBuilder {
    url = '';
    options: unknown;
    reconnect: { nextRetryDelayInMilliseconds(context: { previousRetryCount: number }): number | null } | undefined;
    withUrl(url: string, options: unknown) {
      this.url = url;
      this.options = options;
      return this;
    }
    withAutomaticReconnect(reconnect: FakeBuilder['reconnect']) {
      this.reconnect = reconnect;
      return this;
    }
    build() {
      const connection = new FakeConnection();
      connection.builder = this;
      hub.connections.push(connection);
      return connection;
    }
  }
  return { HubConnectionBuilder: FakeBuilder, HttpTransportType: { WebSockets: 1 } };
});

// Leaflet draws into a real DOM. The fake records what the page asks it to draw.
const leaflet = vi.hoisted(() => ({
  maps: [] as Array<Record<string, any>>,
  tiles: [] as Array<{ url: string; options: { attribution: string } }>,
  lines: [] as Array<{ points: number[][]; setLatLngs: (points: number[][]) => void }>,
  markers: [] as Array<{ latLng: number[] }>,
  circles: [] as Array<{ latLng: number[] }>,
}));
vi.mock('leaflet', () => {
  const layer = <T extends object>(self: T) => Object.assign(self, { addTo: vi.fn(() => self), setLatLng: vi.fn() });
  return {
    Icon: { Default: {} },
    map: vi.fn(() => {
      const m: Record<string, any> = { on: vi.fn(), fitBounds: vi.fn(), remove: vi.fn() };
      m['setView'] = vi.fn(() => m);
      leaflet.maps.push(m);
      return m;
    }),
    tileLayer: vi.fn((url: string, options: { attribution: string }) => {
      leaflet.tiles.push({ url, options });
      return layer({});
    }),
    marker: vi.fn((latLng: number[]) => {
      const m = layer({ latLng });
      leaflet.markers.push(m);
      return m;
    }),
    circleMarker: vi.fn((latLng: number[]) => {
      const m = layer({ latLng });
      leaflet.circles.push(m);
      return m;
    }),
    polyline: vi.fn((points: number[][]) => {
      const line: { points: number[][]; setLatLngs: (p: number[][]) => void } = layer({ points, setLatLngs: (p: number[][]) => (line.points = p) });
      leaflet.lines.push(line);
      return line;
    }),
  };
});

const position = (seq: number, status: ShipmentStatus = 'InTransit'): Position => ({
  seq, status, latitude: 52.4 + seq / 1000, longitude: 13.3 + seq / 1000, recordedAt: '2026-01-01T00:00:00Z',
});
const message = (seq: number, status: ShipmentStatus = 'InTransit'): PositionMessage => ({ shipmentId: 's1', ...position(seq, status) });
const details = (path: Position[] = [], status: ShipmentStatus = 'InTransit'): ShipmentDetails => ({
  id: 's1', orderId: 'o1', copyNumber: 3, videoId: 'v1', status, address: 'Alexanderplatz 1, Berlin',
  destination: { latitude: 52.5219, longitude: 13.4132 },
  warehouse: { name: 'newstore warehouse', latitude: 52.4836, longitude: 13.3897 },
  lastPosition: path.at(-1) ?? null, path, createdAt: '2026-01-01T00:00:00Z', pickedUpAt: null, deliveredAt: null,
});

const q = (f: ComponentFixture<unknown>, id: string) => (f.nativeElement as HTMLElement).querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
const all = (f: ComponentFixture<unknown>, id: string) => Array.from((f.nativeElement as HTMLElement).querySelectorAll(`[data-testid="${id}"]`)) as HTMLElement[];
const tick = (ms = 0) => vi.advanceTimersByTimeAsync(ms);

function setup() {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(), provideHttpClientTesting(),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 's1' }) } } },
    ],
  });
  return TestBed.inject(HttpTestingController);
}

/** Opens the page, answers `GET /api/shipments/s1` and lets the connection start and answer `Track`. */
async function openPage(shipment: ShipmentDetails, snapshot: ShipmentDetails = shipment) {
  hub.snapshot = snapshot;
  const backend = setup();
  const f = TestBed.createComponent(TrackingPageComponent);
  f.detectChanges();
  backend.expectOne('/api/shipments/s1').flush(shipment);
  f.detectChanges();
  await tick();
  f.detectChanges();
  return { f, backend, connection: hub.connections[0] };
}

const handlers = () => ({ snapshot: vi.fn(), position: vi.fn(), connection: vi.fn() });

describe('tracking', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    hub.connections.length = 0;
    hub.startFails = 0;
    hub.snapshot = details();
    leaflet.maps.length = leaflet.tiles.length = leaflet.lines.length = leaflet.markers.length = leaflet.circles.length = 0;
  });
  afterEach(() => vi.useRealTimers());

  it('Tracking_client_connects_with_websockets_only_and_skips_negotiation', async () => {
    const subscription = await Promise.all([TestBed.inject(TrackingClient).track('s1', handlers()), tick()]).then(([s]) => s);

    const builder = hub.connections[0]['builder'];
    expect(builder.url).toBe('/api/hubs/tracking');
    expect(builder.options).toEqual({ transport: 1, skipNegotiation: true });
    expect(hub.connections[0]['invoke']).toHaveBeenCalledWith('Track', 's1');
    await subscription.stop();
  });

  it('Tracking_client_keeps_reconnecting_until_stopped', async () => {
    const tracked = TestBed.inject(TrackingClient).track('s1', handlers());
    await tick();
    const subscription = await tracked;

    const policy = hub.connections[0]['builder'].reconnect.nextRetryDelayInMilliseconds;
    expect([0, 1, 2, 3, 4, 50].map((previousRetryCount) => policy({ previousRetryCount }))).toEqual([0, 1000, 2000, 5000, 5000, 5000]);

    // The server closed the connection (the token expired): a new one starts 2 s later, with Track again.
    hub.connections[0]['handlers']['close']();
    await tick(REOPEN_DELAY_MS - 1);
    expect(hub.connections).toHaveLength(1);
    await tick(1);
    expect(hub.connections).toHaveLength(2);
    expect(hub.connections[1]['invoke']).toHaveBeenCalledWith('Track', 's1');

    // After stop() a close starts nothing.
    await subscription.stop();
    hub.connections[1]['handlers']['close']();
    await tick(60_000);
    expect(hub.connections).toHaveLength(2);
  });

  it('Tracking_client_retries_when_the_first_connect_fails', async () => {
    hub.startFails = 2;
    const h = handlers();
    const tracked = TestBed.inject(TrackingClient).track('s1', h);
    await tick();
    expect(hub.connections).toHaveLength(1);
    await tick(REOPEN_DELAY_MS);
    expect(hub.connections).toHaveLength(2);
    await tick(REOPEN_DELAY_MS);
    expect(hub.connections).toHaveLength(3);

    const subscription = await tracked;
    expect(h.snapshot).toHaveBeenCalledTimes(1);
    await subscription.stop();
  });

  it('Tracking_page_loads_the_shipment_then_tracks_it', async () => {
    const { f, connection } = await openPage(details());

    expect(connection['invoke']).toHaveBeenCalledWith('Track', 's1');
    expect(q(f, 'tracking-address')?.textContent).toContain('Alexanderplatz 1, Berlin');
    expect(leaflet.maps).toHaveLength(1);
    expect(leaflet.markers.map((m) => m.latLng)).toEqual([[52.4836, 13.3897], [52.5219, 13.4132]]);
  });

  it('Tracking_page_ignores_positions_it_has_already_shown', async () => {
    const { f, connection } = await openPage(details());

    for (const seq of [5, 4, 5]) {
      connection['handlers']['position'](message(seq));
    }
    f.detectChanges();

    expect(leaflet.lines[0].points).toEqual([[position(5).latitude, position(5).longitude]]);
    expect(q(f, 'courier-position')?.dataset['seq']).toBe('5');
  });

  it('Tracking_page_tracks_again_after_a_reconnect_and_merges_the_snapshot', async () => {
    const { f, connection } = await openPage(details());
    connection['handlers']['position'](message(3));
    connection['handlers']['position'](message(4));
    f.detectChanges();

    connection['handlers']['reconnecting']();
    f.detectChanges();
    expect(q(f, 'tracking-connection')?.textContent).toContain('Reconnecting…');

    // The gap: positions 5 and 6 were sent while the page was away. The snapshot has them.
    connection['invoke'].mockResolvedValueOnce(details([1, 2, 3, 4, 5, 6].map((seq) => position(seq))));
    await connection['handlers']['reconnected']();
    f.detectChanges();

    expect(connection['invoke'].mock.calls.filter(([method]: string[]) => method === 'Track')).toHaveLength(2);
    expect(q(f, 'tracking-connection')).toBeNull();
    expect(leaflet.lines[0].points).toHaveLength(6);
    expect(q(f, 'courier-position')?.dataset['seq']).toBe('6');

    // A late position from before the gap changes nothing.
    connection['handlers']['position'](message(5));
    expect(leaflet.lines[0].points).toHaveLength(6);
  });

  it('Tracking_page_shows_the_four_steps', async () => {
    const { f } = await openPage(details([position(20, 'InTransit')]));

    expect(all(f, 'tracking-step').map((s) => [s.dataset['step'], s.dataset['state']])).toEqual([
      ['Preparing', 'done'], ['PickedUp', 'done'], ['InTransit', 'current'], ['Delivered', 'todo'],
    ]);
    expect(q(f, 'tracking-status')?.dataset['status']).toBe('InTransit');
    expect(q(f, 'tracking-status')?.textContent).toContain('In transit');
  });

  it('Tracking_page_stops_at_delivered_and_on_destroy', async () => {
    const { f, connection } = await openPage(details());
    expect(connection['stop']).not.toHaveBeenCalled();

    connection['handlers']['position'](message(9, 'Delivered'));
    f.detectChanges();
    expect(connection['stop']).toHaveBeenCalledTimes(1);
    expect(all(f, 'tracking-step').map((s) => s.dataset['state'])).toEqual(['done', 'done', 'done', 'done']);

    TestBed.resetTestingModule();
    const second = await openPage(details());
    second.f.destroy();
    await tick();
    expect(second.connection['stop']).toHaveBeenCalledTimes(1);
  });

  it('Tracking_page_shows_not_found_for_a_404', async () => {
    const backend = setup();
    const f = TestBed.createComponent(TrackingPageComponent);
    f.detectChanges();
    backend.expectOne('/api/shipments/s1').flush({ title: 'Not found.' }, { status: 404, statusText: 'Not Found' });
    f.detectChanges();

    expect(q(f, 'tracking-not-found')).not.toBeNull();
    expect(hub.connections).toHaveLength(0);
  });

  it('Map_shows_the_openstreetmap_attribution', () => {
    createMap(document.createElement('div'), { latitude: 52.4836, longitude: 13.3897 }, 12);

    expect(leaflet.tiles[0].url).toBe('https://tile.openstreetmap.org/{z}/{x}/{y}.png');
    expect(leaflet.tiles[0].options.attribution).toContain('OpenStreetMap');
    expect(leaflet.tiles[0].options.attribution).toContain('https://www.openstreetmap.org/copyright');
  });
});
