import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BROWSER_LOCATION } from './api/browser-location';
import { Edition, MyCopy, Order, ShipmentSummary } from './api/models';
import { AuthService } from './auth/auth.service';
import { MyCopiesComponent } from './copies/my-copies.component';
import { OrderPageComponent } from './orders/order-page.component';
import { VideoListComponent } from './videos/video-list.component';
import { VideoPageComponent } from './videos/video-page.component';

// Leaflet needs a real browser layout. The fake keeps the map's handlers, so a test can click the map.
const leaflet = vi.hoisted(() => ({
  maps: [] as Array<{ view?: unknown[]; handlers: Record<string, (event: unknown) => void> }>,
  markers: [] as Array<{ latLng: number[] }>,
}));
vi.mock('leaflet', () => {
  const layer = <T extends object>(self: T) => Object.assign(self, { addTo: vi.fn(() => self), setLatLng: vi.fn() });
  return {
    Icon: { Default: {} },
    map: vi.fn(() => {
      const m = {
        view: undefined as unknown[] | undefined,
        handlers: {} as Record<string, (event: unknown) => void>,
        setView(...args: unknown[]) {
          m.view = args;
          return m;
        },
        on: (name: string, handler: (event: unknown) => void) => (m.handlers[name] = handler),
        remove: vi.fn(),
      };
      leaflet.maps.push(m);
      return m;
    }),
    tileLayer: vi.fn(() => layer({})),
    marker: vi.fn((latLng: number[]) => {
      const m = layer({ latLng });
      leaflet.markers.push(m);
      return m;
    }),
  };
});

const fakeLocation = { origin: 'http://localhost:8100', pathname: '/', search: '', href: '' };
const video = {
  id: 'v1', title: 'Sourdough', description: 'Bread', durationSeconds: 60, publishedAt: '2026-01-01T00:00:00Z',
  channel: { id: 'c1', name: 'Bakery', slug: 'bakery' },
};
const edition = (over: Partial<Edition> = {}): Edition => ({ id: 'e1', format: 'Digital', price: 9.5, copiesTotal: 10, copiesLeft: 4, onSale: true, ...over });
const order = (over: Partial<Order> = {}): Order => ({
  id: 'o1', editionId: 'e1', videoId: 'v1', format: 'Digital', price: 9.5, status: 'Reserved',
  reservedUntil: '2026-01-01T01:00:00Z', checkoutUrl: null, copyNumber: null, ...over,
});

const warehouse = { name: 'newstore warehouse', latitude: 52.4836, longitude: 13.3897 };
const dvd = () => edition({ id: 'e2', format: 'Dvd' });
const summary = (over: Partial<ShipmentSummary> = {}): ShipmentSummary => ({
  id: 'sh1', orderId: 'o1', copyNumber: 7, videoId: 'v1', status: 'InTransit', createdAt: '2026-01-01T00:00:00Z', deliveredAt: null, ...over,
});

function setup(routeId: string) {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
      { provide: BROWSER_LOCATION, useValue: fakeLocation },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: routeId }) } } },
    ],
  });
  return { backend: TestBed.inject(HttpTestingController), auth: TestBed.inject(AuthService) };
}

const q = (f: ComponentFixture<unknown>, id: string) => (f.nativeElement as HTMLElement).querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

async function signIn(backend: HttpTestingController, auth: AuthService, signedIn: boolean) {
  const loaded = auth.load();
  backend.expectOne('/auth/user').flush(signedIn ? { isAuthenticated: true, sub: 's', name: 'Ann', logoutUrl: '/auth/logout?sid=1' } : { isAuthenticated: false });
  await loaded;
}

describe('pages', () => {
  beforeEach(() => {
    leaflet.maps.length = 0;
    leaflet.markers.length = 0;
  });
  afterEach(() => vi.useRealTimers());

  it('Video_page_shows_sign_in_to_buy_when_signed_out', async () => {
    const { backend, auth } = setup('v1');
    await signIn(backend, auth, false);
    const f = TestBed.createComponent(VideoPageComponent);
    backend.expectOne('/api/videos/v1').flush(video);
    backend.expectOne('/api/videos/v1/editions').flush([edition()]);
    f.detectChanges();

    expect(q(f, 'edition')?.dataset['format']).toBe('Digital');
    expect(q(f, 'edition')?.textContent).toContain('4 left');
    expect(q(f, 'sign-in-to-buy')).not.toBeNull();
    expect(q(f, 'buy')).toBeNull();
  });

  it('Buy_disables_the_button_and_opens_the_order', async () => {
    const { backend, auth } = setup('v1');
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    await signIn(backend, auth, true);
    const f = TestBed.createComponent(VideoPageComponent);
    backend.expectOne('/api/videos/v1').flush(video);
    backend.expectOne('/api/videos/v1/editions').flush([edition()]);
    f.detectChanges();

    q(f, 'buy')!.click();
    f.detectChanges();
    expect((q(f, 'buy') as HTMLButtonElement).disabled).toBe(true);

    const post = backend.expectOne('/api/orders');
    expect(post.request.body).toEqual({ editionId: 'e1' });
    post.flush(order(), { status: 201, statusText: 'Created' });
    expect(navigate).toHaveBeenCalledWith(['/orders', 'o1']);
  });

  it('Buy_shows_the_problem_title_on_a_409', async () => {
    const { backend, auth } = setup('v1');
    await signIn(backend, auth, true);
    const f = TestBed.createComponent(VideoPageComponent);
    backend.expectOne('/api/videos/v1').flush(video);
    backend.expectOne('/api/videos/v1/editions').flush([edition()]);
    f.detectChanges();

    q(f, 'buy')!.click();
    backend.expectOne('/api/orders').flush({ title: 'Sold out.' }, { status: 409, statusText: 'Conflict' });
    f.detectChanges();

    expect(q(f, 'buy-error')?.textContent).toContain('Sold out.');
    expect((q(f, 'buy') as HTMLButtonElement).disabled).toBe(false);
  });

  it('Order_page_polls_until_completed_and_shows_the_copy_number', () => {
    vi.useFakeTimers();
    const { backend } = setup('o1');
    const f = TestBed.createComponent(OrderPageComponent);
    f.detectChanges();
    backend.expectOne('/api/orders/o1').flush(order({ status: 'Paying' }));
    f.detectChanges();
    expect(q(f, 'order-status')?.textContent).toContain('Paying');
    expect(q(f, 'pay')).toBeNull();

    vi.advanceTimersByTime(1000);
    backend.expectOne('/api/orders/o1').flush(order({ status: 'Paying' }));
    vi.advanceTimersByTime(1000);
    backend.expectOne('/api/orders/o1').flush(order({ status: 'Completed', copyNumber: 7 }));
    f.detectChanges();

    expect(q(f, 'copy-number')?.textContent).toContain('7');
    vi.advanceTimersByTime(5000);
    backend.expectNone('/api/orders/o1');
  });

  it('Order_page_stops_polling_after_two_minutes', () => {
    vi.useFakeTimers();
    const { backend } = setup('o1');
    const f = TestBed.createComponent(OrderPageComponent);
    f.detectChanges();
    backend.expectOne('/api/orders/o1').flush(order({ status: 'Paying' }));
    for (let i = 0; i < 120; i++) {
      vi.advanceTimersByTime(1000);
      backend.expectOne('/api/orders/o1').flush(order({ status: 'Paying' }));
    }
    f.detectChanges();

    expect(q(f, 'poll-timeout')?.textContent).toContain('Still processing, refresh later');
    vi.advanceTimersByTime(5000);
    backend.expectNone('/api/orders/o1');
  });

  it('Order_page_keeps_polling_after_a_failed_poll', () => {
    vi.useFakeTimers();
    const { backend } = setup('o1');
    const f = TestBed.createComponent(OrderPageComponent);
    f.detectChanges();
    backend.expectOne('/api/orders/o1').flush(order({ status: 'Paying' }));

    vi.advanceTimersByTime(1000);
    backend.expectOne('/api/orders/o1').flush({ title: 'Down' }, { status: 503, statusText: 'Service Unavailable' });
    f.detectChanges();
    expect(q(f, 'refresh-failed')?.textContent).toContain("Couldn't refresh the status; trying again.");
    expect(q(f, 'order-status')?.textContent).toContain('Paying');

    vi.advanceTimersByTime(1000);
    backend.expectOne('/api/orders/o1').flush(order({ status: 'Completed', copyNumber: 7 }));
    f.detectChanges();
    expect(q(f, 'refresh-failed')).toBeNull();
    expect(q(f, 'copy-number')?.textContent).toContain('7');
  });

  it('Order_page_gives_up_at_the_limit_when_every_poll_fails', () => {
    vi.useFakeTimers();
    const { backend } = setup('o1');
    const f = TestBed.createComponent(OrderPageComponent);
    f.detectChanges();
    backend.expectOne('/api/orders/o1').flush(order({ status: 'Paying' }));
    for (let i = 0; i < 120; i++) {
      vi.advanceTimersByTime(1000);
      backend.expectOne('/api/orders/o1').flush({}, { status: 503, statusText: 'Service Unavailable' });
    }
    f.detectChanges();

    expect(q(f, 'poll-timeout')?.textContent).toContain('Still processing, refresh later');
    vi.advanceTimersByTime(5000);
    backend.expectNone('/api/orders/o1');
  });

  it('Order_page_pay_sends_the_browser_to_the_checkout', () => {
    const { backend } = setup('o1');
    const f = TestBed.createComponent(OrderPageComponent);
    f.detectChanges();
    backend.expectOne('/api/orders/o1').flush(order());
    f.detectChanges();

    q(f, 'pay')!.click();
    backend.expectOne('/api/orders/o1/pay').flush({ checkoutUrl: 'http://localhost:8105/pay/abc' });

    expect(fakeLocation.href).toBe('http://localhost:8105/pay/abc');
  });

  it('My_copies_lists_each_copy', () => {
    const { backend } = setup('');
    const f = TestBed.createComponent(MyCopiesComponent);
    f.detectChanges();
    const copy = (n: number): MyCopy => ({
      orderId: `o${n}`, editionId: 'e1', videoId: 'v1', format: 'Digital', copyNumber: n, price: 9.5,
      completedAt: '2026-01-01T00:00:00Z',
    });
    backend.expectOne('/api/me/copies').flush([copy(3), copy(2)]);
    f.detectChanges();

    const rows = (f.nativeElement as HTMLElement).querySelectorAll('[data-testid="copy-row"]');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('#3');
    expect(rows[0].textContent).toContain('—');
  });

  /** Signs in, opens the video page with one DVD edition and the Digital one, and clicks Buy on the DVD. */
  async function openCheckout() {
    const { backend, auth } = setup('v1');
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    await signIn(backend, auth, true);
    const f = TestBed.createComponent(VideoPageComponent);
    backend.expectOne('/api/videos/v1').flush(video);
    backend.expectOne('/api/videos/v1/editions').flush([edition(), dvd()]);
    f.detectChanges();
    (f.nativeElement as HTMLElement).querySelector<HTMLElement>('[data-format="Dvd"] [data-testid="buy"]')!.click();
    f.detectChanges();
    return { backend, f, navigate };
  }

  const typeAddress = (f: ComponentFixture<unknown>, text: string) => {
    const input = q(f, 'delivery-address') as HTMLInputElement;
    input.value = text;
    input.dispatchEvent(new Event('input'));
    f.detectChanges();
  };
  const clickMap = (f: ComponentFixture<unknown>, lat: number, lng: number) => {
    leaflet.maps[0].handlers['click']({ latlng: { lat, lng, wrap() { return this; } } });
    f.detectChanges();
  };
  const confirm = (f: ComponentFixture<unknown>) => q(f, 'confirm-buy') as HTMLButtonElement;

  it('Dvd_buy_asks_for_an_address_and_a_pin_before_posting', async () => {
    const { backend, f } = await openCheckout();

    expect(q(f, 'delivery-form')).not.toBeNull();
    expect(confirm(f).disabled).toBe(true);
    backend.expectNone('/api/orders');

    q(f, 'cancel-buy')!.click();
    f.detectChanges();
    expect(q(f, 'delivery-form')).toBeNull();
    backend.expectNone('/api/orders');
  });

  it('Checkout_map_starts_on_the_warehouse', async () => {
    const { backend, f } = await openCheckout();
    backend.expectOne('/api/delivery/warehouse').flush(warehouse);
    f.detectChanges();

    expect(leaflet.maps).toHaveLength(1);
    expect(leaflet.maps[0].view).toEqual([[52.4836, 13.3897], 12]);
    expect(q(f, 'delivery-pin')?.textContent).toContain('Click the map to drop a pin');
  });

  it('Map_click_drops_the_pin_and_enables_confirm', async () => {
    const { backend, f } = await openCheckout();
    backend.expectOne('/api/delivery/warehouse').flush(warehouse);
    typeAddress(f, 'Alexanderplatz 1, 10178 Berlin');
    expect(confirm(f).disabled).toBe(true);

    clickMap(f, 52.5219, 13.4132);
    expect(q(f, 'delivery-pin')?.textContent).toContain('52.52190, 13.41320');
    expect(confirm(f).disabled).toBe(false);

    // A second click moves the one marker.
    clickMap(f, 52.5, 13.4);
    expect(leaflet.markers).toHaveLength(1);
    expect(q(f, 'delivery-pin')?.textContent).toContain('52.50000, 13.40000');
  });

  it('Confirm_posts_the_order_with_the_delivery', async () => {
    const { backend, f, navigate } = await openCheckout();
    backend.expectOne('/api/delivery/warehouse').flush(warehouse);
    typeAddress(f, '  Alexanderplatz 1, 10178 Berlin ');
    clickMap(f, 52.5219, 13.4132);

    confirm(f).click();
    const post = backend.expectOne('/api/orders');
    expect(post.request.body).toEqual({
      editionId: 'e2', delivery: { address: 'Alexanderplatz 1, 10178 Berlin', latitude: 52.5219, longitude: 13.4132 },
    });
    post.flush(order({ id: 'o9' }), { status: 201, statusText: 'Created' });
    expect(navigate).toHaveBeenCalledWith(['/orders', 'o9']);
  });

  it('Address_over_200_characters_keeps_confirm_disabled', async () => {
    const { backend, f } = await openCheckout();
    backend.expectOne('/api/delivery/warehouse').flush(warehouse);
    clickMap(f, 52.5219, 13.4132);

    typeAddress(f, 'x'.repeat(200));
    expect(confirm(f).disabled).toBe(false);
    typeAddress(f, 'x'.repeat(201));
    expect(confirm(f).disabled).toBe(true);
    typeAddress(f, '   ');
    expect(confirm(f).disabled).toBe(true);
  });

  it('Digital_buy_posts_without_delivery', async () => {
    const { backend, auth } = setup('v1');
    vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    await signIn(backend, auth, true);
    const f = TestBed.createComponent(VideoPageComponent);
    backend.expectOne('/api/videos/v1').flush(video);
    backend.expectOne('/api/videos/v1/editions').flush([edition(), dvd()]);
    f.detectChanges();

    (f.nativeElement as HTMLElement).querySelector<HTMLElement>('[data-format="Digital"] [data-testid="buy"]')!.click();

    expect(backend.expectOne('/api/orders').request.body).toEqual({ editionId: 'e1' });
    expect(q(f, 'delivery-form')).toBeNull();
  });

  it('Delivery_problem_shows_its_title', async () => {
    const { backend, f } = await openCheckout();
    backend.expectOne('/api/delivery/warehouse').flush(warehouse);
    typeAddress(f, 'Alexanderplatz 1');
    clickMap(f, 52.5219, 13.4132);

    confirm(f).click();
    backend.expectOne('/api/orders').flush(
      { type: 'urn:newstore:problem:delivery-required', title: 'A DVD needs a delivery address and a pin.', status: 400 },
      { status: 400, statusText: 'Bad Request' },
    );
    f.detectChanges();

    expect(q(f, 'buy-error')?.textContent).toContain('A DVD needs a delivery address and a pin.');
    expect(confirm(f).disabled).toBe(false);
  });

  it('Order_page_links_a_completed_dvd_order_to_its_tracking_page', () => {
    vi.useFakeTimers();
    const { backend } = setup('o1');
    const f = TestBed.createComponent(OrderPageComponent);
    f.detectChanges();
    backend.expectOne('/api/orders/o1').flush(order({ status: 'Completed', format: 'Dvd', copyNumber: 7 }));
    backend.expectOne('/api/me/shipments').flush([]);
    f.detectChanges();
    expect(q(f, 'shipment-pending')?.textContent).toContain('Preparing your shipment…');
    expect(q(f, 'track-link')).toBeNull();

    vi.advanceTimersByTime(1999);
    backend.expectNone('/api/me/shipments');
    vi.advanceTimersByTime(1);
    backend.expectOne('/api/me/shipments').flush([summary({ id: 'other', orderId: 'o0' }), summary()]);
    f.detectChanges();

    expect(q(f, 'shipment-pending')).toBeNull();
    expect(q(f, 'track-link')?.getAttribute('href')).toBe('/shipments/sh1');
    vi.advanceTimersByTime(10_000);
    backend.expectNone('/api/me/shipments');
  });

  it('Order_page_gives_up_looking_for_the_shipment_after_a_minute', () => {
    vi.useFakeTimers();
    const { backend } = setup('o1');
    const f = TestBed.createComponent(OrderPageComponent);
    f.detectChanges();
    backend.expectOne('/api/orders/o1').flush(order({ status: 'Completed', format: 'Dvd', copyNumber: 7 }));
    backend.expectOne('/api/me/shipments').flush([]);
    for (let i = 0; i < 30; i++) {
      vi.advanceTimersByTime(2000);
      backend.expectOne('/api/me/shipments').flush([]);
    }
    f.detectChanges();

    expect(q(f, 'shipment-pending')).toBeNull();
    vi.advanceTimersByTime(10_000);
    backend.expectNone('/api/me/shipments');
  });

  it('Order_page_shows_no_track_link_for_a_digital_order', () => {
    const { backend } = setup('o1');
    const f = TestBed.createComponent(OrderPageComponent);
    f.detectChanges();
    backend.expectOne('/api/orders/o1').flush(order({ status: 'Completed', format: 'Digital', copyNumber: 7 }));
    f.detectChanges();

    backend.expectNone('/api/me/shipments');
    expect(q(f, 'track-link')).toBeNull();
    expect(q(f, 'shipment-pending')).toBeNull();
  });

  it('My_copies_shows_each_physical_copys_status_and_a_track_link', () => {
    const { backend } = setup('');
    const f = TestBed.createComponent(MyCopiesComponent);
    f.detectChanges();
    const copy = (n: number, format: string): MyCopy => ({
      orderId: `o${n}`, editionId: 'e1', videoId: 'v1', format, copyNumber: n, price: 9.5, completedAt: '2026-01-01T00:00:00Z',
    });
    backend.expectOne('/api/me/copies').flush([copy(2, 'Dvd'), copy(1, 'Digital')]);
    backend.expectOne('/api/me/shipments').flush([summary({ id: 'sh2', orderId: 'o2', status: 'PickedUp' })]);
    f.detectChanges();

    const rows = (f.nativeElement as HTMLElement).querySelectorAll('[data-testid="copy-row"]');
    expect(rows[0].querySelector('[data-testid="delivery-status"]')?.textContent).toContain('Picked up');
    expect(rows[0].querySelector('[data-testid="track-link"]')?.getAttribute('href')).toBe('/shipments/sh2');
    expect(rows[1].querySelector('[data-testid="delivery-status"]')?.textContent?.trim()).toBe('—');
    expect(rows[1].querySelector('[data-testid="track-link"]')).toBeNull();
  });

  it('My_copies_stay_listed_when_the_shipments_call_fails', () => {
    const { backend } = setup('');
    const f = TestBed.createComponent(MyCopiesComponent);
    f.detectChanges();
    backend.expectOne('/api/me/copies').flush([
      { orderId: 'o2', editionId: 'e1', videoId: 'v1', format: 'Dvd', copyNumber: 2, price: 9.5, completedAt: '2026-01-01T00:00:00Z' },
    ]);
    backend.expectOne('/api/me/shipments').flush({}, { status: 503, statusText: 'Service Unavailable' });
    f.detectChanges();

    expect(f.nativeElement.querySelectorAll('[data-testid="copy-row"]')).toHaveLength(1);
    expect(q(f, 'delivery-status')?.textContent?.trim()).toBe('—');
  });

  it('Video_list_shows_thumbnails', () => {
    const { backend } = setup('');
    const f = TestBed.createComponent(VideoListComponent);
    f.detectChanges();
    const item = (id: string, thumbnailUrl: string | null) => ({ ...video, id, thumbnailUrl });
    backend.expectOne((r) => r.url === '/api/videos').flush({
      items: [item('v1', 'http://localhost:4566/newstore-media/hls/v1/thumbnail.jpg'), item('v2', null)], page: 1, pageSize: 20, total: 2,
    });
    f.detectChanges();

    const images = (f.nativeElement as HTMLElement).querySelectorAll('[data-testid="video-thumbnail"]');
    expect(images).toHaveLength(1);
    expect(images[0].getAttribute('src')).toBe('http://localhost:4566/newstore-media/hls/v1/thumbnail.jpg');
  });

  it('Download_opens_the_link', () => {
    const { backend } = setup('');
    const f = TestBed.createComponent(MyCopiesComponent);
    f.detectChanges();
    const copy = (n: number, format: string) => ({
      orderId: `o${n}`, editionId: 'e1', videoId: 'v1', format, copyNumber: n, price: 9.5, completedAt: '2026-01-01T00:00:00Z',
    });
    backend.expectOne('/api/me/copies').flush([copy(1, 'Digital'), copy(2, 'Physical')]);
    f.detectChanges();
    expect(q(f, 'download')).not.toBeNull();
    expect((f.nativeElement as HTMLElement).querySelectorAll('[data-testid="download"]')).toHaveLength(1);

    q(f, 'download')!.click();
    const post = backend.expectOne('/api/downloads');
    expect(post.request.body).toEqual({ videoId: 'v1' });
    post.flush({ url: 'http://localhost:4566/newstore-media/originals/x?sig=1', expiresAt: '2026-01-01T00:10:00Z', fileName: 'a.mp4' });

    expect(fakeLocation.href).toBe('http://localhost:4566/newstore-media/originals/x?sig=1');
  });
});
