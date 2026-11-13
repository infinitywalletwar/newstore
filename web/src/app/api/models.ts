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
}

export interface VideoDetails extends VideoSummary {
  description: string;
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
