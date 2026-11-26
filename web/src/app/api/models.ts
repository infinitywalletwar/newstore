// The JSON the gateway and the services send, in camelCase. The names match Catalog's and Orders' records.

/** `GET /auth/user`: signed out is a normal 200, never a 401. */
export type SessionUser =
  | { isAuthenticated: false }
  | { isAuthenticated: true; sub: string; name: string; logoutUrl: string };

/** An RFC 9457 problem as the services send it. `type` is `urn:newstore:problem:<slug>` and can be missing. */
export interface Problem {
  type?: string;
  title?: string;
  status?: number;
}

export interface Channel {
  id: string;
  name: string;
  slug: string;
}

export interface VideoSummary {
  id: string;
  title: string;
  durationSeconds: number;
  publishedAt: string;
  channel: Channel;
  /** The thumbnail's public URL, or null until the video has been processed. */
  thumbnailUrl: string | null;
}

export interface VideoDetails extends VideoSummary {
  description: string;
  /** The HLS master playlist's public URL, or null until the video has been processed. */
  playbackUrl: string | null;
}

export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
}

/** `id` is Inventory's edition id: it is what `POST /api/orders` takes. */
export interface Edition {
  id: string;
  format: string;
  price: number;
  copiesTotal: number;
  copiesLeft: number;
  onSale: boolean;
}

export type OrderStatus = 'Reserved' | 'Paying' | 'Completed' | 'Expired' | 'Failed' | 'Refunding' | 'Refunded';

/** The statuses an order never leaves, so the order page stops polling. `Refunding` is not one: `Refunded` follows. */
export const FINAL_STATUSES: readonly OrderStatus[] = ['Completed', 'Expired', 'Failed', 'Refunded'];

export interface Order {
  id: string;
  editionId: string;
  videoId: string;
  format: string;
  price: number;
  status: OrderStatus;
  reservedUntil: string;
  checkoutUrl: string | null;
  copyNumber: number | null;
}

export interface MyCopy {
  orderId: string;
  editionId: string;
  videoId: string;
  format: string;
  copyNumber: number;
  price: number;
  completedAt: string;
  deliveryStatus: string | null;
}

/** `GET /api/me/videos`: the creator's own videos, in every status. */
export interface MyVideo {
  id: string;
  title: string;
  status: string;
  createdAt: string;
  publishedAt: string | null;
  thumbnailUrl: string | null;
}

export type UploadStatus = 'Uploading' | 'Processing' | 'Published' | 'Failed' | 'Expired';

/** The statuses an upload never leaves, so the studio stops polling. */
export const TERMINAL_UPLOAD_STATUSES: readonly UploadStatus[] = ['Published', 'Failed', 'Expired'];

/** `GET /api/me/uploads`: the latest upload of each video. */
export interface UploadSummary {
  id: string;
  videoId: string;
  fileName: string;
  sizeBytes: number;
  status: UploadStatus;
  failureReason: string | null;
  createdAt: string;
  updatedAt: string;
}

/** A part S3 already has. `eTag` is quoted, as S3 sends it. */
export interface UploadedPart {
  partNumber: number;
  eTag: string;
  size: number;
}

/** A presigned PUT URL for one part. `size` is the exact slice it accepts: smaller for the last part. */
export interface PartUrl {
  partNumber: number;
  url: string;
  size: number;
}

/** `GET /api/uploads/{id}` (Media's `UploadResponse`): what S3 has, and URLs for what it lacks. */
export interface UploadState {
  id: string;
  videoId: string;
  fileName: string;
  sizeBytes: number;
  status: UploadStatus;
  failureReason: string | null;
  partSize: number;
  partCount: number;
  uploadedParts: UploadedPart[];
  partUrls: PartUrl[];
  partUrlsExpireAt: string | null;
  createdAt: string;
  updatedAt: string;
}

/** `POST /api/downloads`: a presigned link to the original file. */
export interface DownloadLink {
  url: string;
  expiresAt: string;
  fileName: string;
}

/** What `POST /api/orders` takes for a DVD: where to send it, and the pin the buyer dropped. */
export interface DeliveryDetails {
  address: string;
  latitude: number;
  longitude: number;
}

export interface GeoPoint {
  latitude: number;
  longitude: number;
}

export interface Warehouse {
  name: string;
  latitude: number;
  longitude: number;
}

export type ShipmentStatus = 'Preparing' | 'PickedUp' | 'InTransit' | 'Delivered';

/** One point of the courier's path. `seq` grows by one per tick and is what a client orders and de-duplicates by. */
export interface Position {
  seq: number;
  status: ShipmentStatus;
  latitude: number;
  longitude: number;
  recordedAt: string;
}

/** The hub's `position` message: a `Position` and the shipment it belongs to. */
export interface PositionMessage extends Position {
  shipmentId: string;
}

/** `GET /api/me/shipments` (Delivery's `ShipmentSummary`), newest first. */
export interface ShipmentSummary {
  id: string;
  orderId: string;
  copyNumber: number;
  videoId: string;
  status: ShipmentStatus;
  createdAt: string;
  deliveredAt: string | null;
}

/** `GET /api/shipments/{id}` and the hub's `Track` answer. `path` is every stored position, oldest first. */
export interface ShipmentDetails {
  id: string;
  orderId: string;
  copyNumber: number;
  videoId: string;
  status: ShipmentStatus;
  address: string;
  destination: GeoPoint;
  warehouse: Warehouse;
  lastPosition: Position | null;
  path: Position[];
  createdAt: string;
  pickedUpAt: string | null;
  deliveredAt: string | null;
}
