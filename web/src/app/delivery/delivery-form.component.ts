import { HttpClient } from '@angular/common/http';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Component, DestroyRef, ElementRef, computed, inject, input, output, signal, viewChild } from '@angular/core';
import * as L from 'leaflet';
import { DeliveryDetails, GeoPoint, Warehouse } from '../api/models';
import { createMap } from '../maps/leaflet-map';

const MAX_ADDRESS_LENGTH = 200;
/** Where the map starts when the warehouse can't be read: the pin is still the buyer's to drop. */
const FALLBACK_CENTER: GeoPoint = { latitude: 52.52, longitude: 13.405 };
const START_ZOOM = 12;

@Component({
  selector: 'app-delivery-form',
  template: `
    <form data-testid="delivery-form" class="card" (submit)="$event.preventDefault()">
      <label>
        Delivery address
        <input type="text" data-testid="delivery-address" maxlength="200" [value]="address()" (input)="onAddress($event)" />
      </label>
      <div #map data-testid="delivery-map" class="map"></div>
      <p data-testid="delivery-pin">
        @if (pin(); as p) {
          {{ p.latitude.toFixed(5) }}, {{ p.longitude.toFixed(5) }}
        } @else {
          Click the map to drop a pin
        }
      </p>
      <button type="button" data-testid="confirm-buy" [disabled]="!canConfirm() || busy()" (click)="confirm()">Confirm and buy</button>
      <button type="button" data-testid="cancel-buy" (click)="cancelled.emit()">Cancel</button>
    </form>
  `,
})
export class DeliveryFormComponent {
  private readonly http = inject(HttpClient);
  private readonly mapElement = viewChild.required<ElementRef<HTMLElement>>('map');
  private map?: L.Map;
  private marker?: L.Marker;

  /** True while the order is being posted, so a double click can't send two. */
  readonly busy = input(false);
  readonly confirmed = output<DeliveryDetails>();
  readonly cancelled = output<void>();

  protected readonly address = signal('');
  protected readonly pin = signal<GeoPoint | null>(null);
  protected readonly canConfirm = computed(() => {
    const length = this.address().trim().length;
    return length >= 1 && length <= MAX_ADDRESS_LENGTH && this.pin() !== null;
  });

  constructor() {
    const destroyRef = inject(DestroyRef);
    destroyRef.onDestroy(() => this.map?.remove());
    // Cancel before the answer: the request is dropped and no map is made for a form that is gone.
    this.http.get<Warehouse>('/api/delivery/warehouse').pipe(takeUntilDestroyed(destroyRef)).subscribe({
      next: (warehouse) => this.showMap(warehouse),
      error: () => this.showMap(FALLBACK_CENTER),
    });
  }

  private showMap(center: GeoPoint): void {
    this.map = createMap(this.mapElement().nativeElement, center, START_ZOOM);
    this.map.on('click', (event: L.LeafletMouseEvent) => {
      // Dragging across the date line gives longitudes beyond ±180, which Delivery refuses.
      const { lat, lng } = event.latlng.wrap();
      this.setPin({ latitude: lat, longitude: lng });
    });
  }

  private setPin(point: GeoPoint): void {
    this.pin.set(point);
    const latLng: L.LatLngTuple = [point.latitude, point.longitude];
    if (this.marker) {
      this.marker.setLatLng(latLng);
    } else if (this.map) {
      this.marker = L.marker(latLng).addTo(this.map);
    }
  }

  protected onAddress(event: Event): void {
    this.address.set((event.target as HTMLInputElement).value);
  }

  protected confirm(): void {
    const pin = this.pin();
    if (this.canConfirm() && pin) {
      this.confirmed.emit({ address: this.address().trim(), latitude: pin.latitude, longitude: pin.longitude });
    }
  }
}
