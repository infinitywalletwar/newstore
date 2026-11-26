import * as L from 'leaflet';
import { GeoPoint } from '../api/models';

// angular.json copies node_modules/leaflet/dist/images to /leaflet/. Without this Leaflet works out the marker
// images' path from its stylesheet's URL, which a bundled stylesheet breaks.
L.Icon.Default.imagePath = 'leaflet/';

export const TILE_URL = 'https://tile.openstreetmap.org/{z}/{x}/{y}.png';
export const TILE_ATTRIBUTION = '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors';

/**
 * A map centred on `center`, with OpenStreetMap's tiles. The attribution control stays on (bottom-right), as the
 * tile usage policy asks. The tiles are only those the map shows: no pre-fetching.
 */
export function createMap(element: HTMLElement, center: GeoPoint, zoom: number): L.Map {
  const map = L.map(element).setView([center.latitude, center.longitude], zoom);
  L.tileLayer(TILE_URL, { maxZoom: 19, attribution: TILE_ATTRIBUTION }).addTo(map);
  return map;
}
