import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BROWSER_LOCATION } from './api/browser-location';
import { Edition, MyCopy, Order } from './api/models';
import { AuthService } from './auth/auth.service';
import { MyCopiesComponent } from './copies/my-copies.component';
import { OrderPageComponent } from './orders/order-page.component';
import { VideoPageComponent } from './videos/video-page.component';

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
      completedAt: '2026-01-01T00:00:00Z', deliveryStatus: null,
    });
    backend.expectOne('/api/me/copies').flush([copy(3), copy(2)]);
    f.detectChanges();

    const rows = (f.nativeElement as HTMLElement).querySelectorAll('[data-testid="copy-row"]');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('#3');
    expect(rows[0].textContent).toContain('—');
  });
});
