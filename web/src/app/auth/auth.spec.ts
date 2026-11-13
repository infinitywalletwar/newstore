import { HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { HttpClient } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { RouterStateSnapshot } from '@angular/router';
import { BROWSER_LOCATION } from '../api/browser-location';
import { AuthService } from './auth.service';
import { csrfInterceptor } from './csrf.interceptor';
import { sessionInterceptor } from './session.interceptor';
import { signedInGuard } from './signed-in.guard';

const fakeLocation = { origin: 'http://localhost:8100', pathname: '/videos/1', search: '?x=1', href: '' };

function setup() {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([csrfInterceptor, sessionInterceptor])),
      provideHttpClientTesting(),
      { provide: BROWSER_LOCATION, useValue: fakeLocation },
    ],
  });
  return { http: TestBed.inject(HttpClient), backend: TestBed.inject(HttpTestingController), auth: TestBed.inject(AuthService) };
}

describe('auth', () => {
  beforeEach(() => (fakeLocation.href = ''));

  it('Adds_the_csrf_header_to_api_and_auth_calls_only', () => {
    const { http, backend } = setup();
    const sent = (url: string) => {
      http.get(url).subscribe();
      const req = backend.expectOne(url);
      req.flush({});
      return req.request.headers.get('X-CSRF');
    };

    expect(sent('/api/videos')).toBe('1');
    expect(sent('/auth/user')).toBe('1');
    expect(sent('http://localhost:8100/api/orders')).toBe('1');
    expect(sent('/apiary')).toBeNull();
    expect(sent('/index.html')).toBeNull();
    expect(sent('http://localhost:8105/api/x')).toBeNull();
    expect(sent('https://other.example/api/x')).toBeNull();
  });

  it('Session_expired_clears_the_user', async () => {
    const { http, backend, auth } = setup();
    const loaded = auth.load();
    backend.expectOne('/auth/user').flush({ isAuthenticated: true, sub: 's', name: 'Ann', logoutUrl: '/auth/logout?sid=1' });
    await loaded;
    expect(auth.isSignedIn()).toBe(true);

    let received: HttpErrorResponse | undefined;
    http.get('/api/me/copies').subscribe({ error: (e: HttpErrorResponse) => (received = e) });
    backend
      .expectOne('/api/me/copies')
      .flush({ type: 'urn:newstore:problem:session-expired', title: 'Your session ended. Sign in again.' }, { status: 401, statusText: 'Unauthorized' });

    expect(received?.status).toBe(401);
    expect(auth.isSignedIn()).toBe(false);
    expect(auth.notice()).toBe('Your session ended. Sign in again.');
  });

  it('A_plain_401_signs_out_without_a_note', () => {
    const { http, backend, auth } = setup();
    auth.user.set({ isAuthenticated: true, sub: 's', name: 'Ann', logoutUrl: '/x' });
    http.get('/api/orders/1').subscribe({ error: () => {} });
    backend.expectOne('/api/orders/1').flush({ type: 'urn:newstore:problem:unauthorized' }, { status: 401, statusText: 'Unauthorized' });

    expect(auth.isSignedIn()).toBe(false);
    expect(auth.notice()).toBeNull();
  });

  it('Signed_in_guard_sends_a_signed_out_viewer_to_sign_in', async () => {
    const { backend } = setup();
    const result = TestBed.runInInjectionContext(() => signedInGuard({} as never, { url: '/me/copies' } as RouterStateSnapshot));
    backend.expectOne('/auth/user').flush({ isAuthenticated: false });

    expect(await result).toBe(false);
    expect(fakeLocation.href).toBe('/auth/login?returnUrl=%2Fme%2Fcopies');
  });
});
