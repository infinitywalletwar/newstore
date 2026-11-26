import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, ElementRef, computed, effect, inject, signal, viewChild } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import * as L from 'leaflet';
import { Position, PositionMessage, ShipmentDetails } from '../api/models';
import { problemTitle } from '../api/problem';
import { createMap } from '../maps/leaflet-map';
import { SHIPMENT_STEPS, STATUS_LABELS, stepState } from './shipment-status';
import { TrackingClient, TrackingSubscription } from './tracking-client';

const START_ZOOM = 12;

@Component({
  selector: 'app-tracking-page',
  template: `
    @if (shipment(); as s) {
      <h1>Tracking your DVD #{{ s.copyNumber }}</h1>
      <p data-testid="tracking-address">{{ s.address }}</p>
      <p>
        Status: <strong data-testid="tracking-status" [attr.data-status]="status()">{{ label() }}</strong>
        @if (reconnecting()) {
          <em data-testid="tracking-connection">Reconnecting…</em>
        }
      </p>
      <ol class="steps">
        @for (step of steps; track step) {
          <li data-testid="tracking-step" [attr.data-step]="step" [attr.data-state]="stateOf(step)">{{ labels[step] }}</li>
        }
      </ol>
      @if (latest(); as p) {
        <p data-testid="courier-position" [attr.data-seq]="p.seq" [attr.data-lat]="p.latitude" [attr.data-lng]="p.longitude">
          The courier is at {{ p.latitude.toFixed(5) }}, {{ p.longitude.toFixed(5) }}.
        </p>
      }
      <div #map data-testid="tracking-map" class="map"></div>
    } @else if (notFound()) {
      <p data-testid="tracking-not-found">No such shipment.</p>
    } @else if (loadError(); as message) {
      <p class="error" role="alert">{{ message }}</p>
    }
  `,
})
export class TrackingPageComponent {
  private readonly http = inject(HttpClient);
  private readonly client = inject(TrackingClient);
  private readonly id = inject(ActivatedRoute).snapshot.paramMap.get('id')!;
  private readonly mapElement = viewChild<ElementRef<HTMLElement>>('map');
  private subscription?: TrackingSubscription;
  private finished = false;
  private readonly abort = new AbortController();
  private map?: L.Map;
  private courier?: L.CircleMarker;
  private line?: L.Polyline;

  protected readonly steps = SHIPMENT_STEPS;
  protected readonly labels = STATUS_LABELS;
  protected readonly shipment = signal<ShipmentDetails | null>(null);
  protected readonly notFound = signal(false);
  protected readonly loadError = signal<string | null>(null);
  protected readonly reconnecting = signal(false);
  /** The path, oldest first, with each seq once. */
  protected readonly path = signal<Position[]>([]);
  protected readonly latest = computed(() => this.path().at(-1) ?? null);
  /** The status of the highest seq; before the first position, the shipment's own. */
  protected readonly status = computed(() => this.latest()?.status ?? this.shipment()?.status ?? 'Preparing');
  protected readonly label = computed(() => STATUS_LABELS[this.status()]);

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.finished = true;
      // Ends the session even before its first snapshot, when there is no subscription to stop yet.
      this.abort.abort();
      void this.subscription?.stop();
      this.map?.remove();
    });
    effect(() => this.draw());
    this.http.get<ShipmentDetails>(`/api/shipments/${this.id}`).subscribe({
      next: (details) => this.start(details),
      error: (error: unknown) => {
        if (error instanceof HttpErrorResponse && error.status === 404) {
          this.notFound.set(true);
        } else {
          this.loadError.set(problemTitle(error));
        }
      },
    });
  }

  protected stateOf(step: (typeof SHIPMENT_STEPS)[number]) {
    return stepState(step, this.status());
  }

  private start(details: ShipmentDetails): void {
    // The page was left while the REST call was on its way.
    if (this.finished) {
      return;
    }
    this.shipment.set(details);
    this.merge(details.path);
    if (this.status() === 'Delivered') {
      this.finished = true;
      return;
    }
    this.client
      .track(
        this.id,
        {
          snapshot: (snapshot) => this.merge(snapshot.path),
          position: (message) => this.apply(message),
          connection: (state) => this.reconnecting.set(state === 'reconnecting'),
          notFound: () => {
            this.finished = true;
            this.shipment.set(null);
            this.notFound.set(true);
          },
        },
        this.abort.signal,
      )
      .then((subscription) => {
        this.subscription = subscription;
        // Delivered or destroyed while the first snapshot was on its way.
        if (this.finished) {
          void subscription.stop();
        }
      });
  }

  /** A snapshot: every position of `incoming` that is not shown yet, by seq. */
  private merge(incoming: readonly Position[]): void {
    const bySeq = new Map(this.path().map((p) => [p.seq, p]));
    for (const position of incoming) {
      bySeq.set(position.seq, bySeq.get(position.seq) ?? position);
    }
    this.path.set([...bySeq.values()].sort((a, b) => a.seq - b.seq));
    this.stopWhenDelivered();
  }

  /** A pushed position counts only when it is newer than everything shown. A repeat or a late one changes nothing. */
  private apply(message: PositionMessage): void {
    const highest = this.latest()?.seq ?? -1;
    if (message.seq <= highest) {
      return;
    }
    const { shipmentId: _shipmentId, ...position } = message;
    this.path.set([...this.path(), position]);
    this.stopWhenDelivered();
  }

  private stopWhenDelivered(): void {
    if (this.status() === 'Delivered' && !this.finished) {
      this.finished = true;
      void this.subscription?.stop();
    }
  }

  /** Creates the map once the shipment and the map's element exist, then keeps the courier and the path on it. */
  private draw(): void {
    const shipment = this.shipment();
    const element = this.mapElement();
    const path = this.path();
    if (!shipment || !element) {
      return;
    }
    if (!this.map) {
      const { warehouse, destination } = shipment;
      this.map = createMap(element.nativeElement, warehouse, START_ZOOM);
      L.marker([warehouse.latitude, warehouse.longitude]).addTo(this.map);
      L.marker([destination.latitude, destination.longitude]).addTo(this.map);
      this.map.fitBounds([
        [warehouse.latitude, warehouse.longitude],
        [destination.latitude, destination.longitude],
      ]);
    }
    const points = path.map((p): L.LatLngTuple => [p.latitude, p.longitude]);
    if (this.line) {
      this.line.setLatLngs(points);
    } else {
      this.line = L.polyline(points).addTo(this.map);
    }
    const last = points.at(-1);
    if (last) {
      if (this.courier) {
        this.courier.setLatLng(last);
      } else {
        this.courier = L.circleMarker(last, { radius: 8 }).addTo(this.map);
      }
    }
  }
}
