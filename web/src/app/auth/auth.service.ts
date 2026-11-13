import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { BROWSER_LOCATION } from '../api/browser-location';
import { Problem, SessionUser } from '../api/models';

/**
 * The app never sees a token. The gateway keeps the session; the browser holds only an HttpOnly cookie and asks
 * `GET /auth/user` who it is (ADR 0015).
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly location = inject(BROWSER_LOCATION);
  private loading?: Promise<void>;

  readonly user = signal<SessionUser>({ isAuthenticated: false });
  /** Set when the gateway says the session ended on its own (not when the user signed out). */
  readonly notice = signal<string | null>(null);
  /** False until `/auth/user` has answered, so a page does not flash "Sign in" at someone who is signed in. */
  readonly ready = signal(false);
  readonly isSignedIn = computed(() => this.user().isAuthenticated);

  /** Loads `/auth/user` once; every later call returns the same promise. */
  load(): Promise<void> {
    this.loading ??= firstValueFrom(this.http.get<SessionUser>('/auth/user')).then(
      (user) => this.user.set(user),
      () => this.user.set({ isAuthenticated: false }),
    ).then(() => this.ready.set(true));
    return this.loading;
  }

  /** A full-page navigation: the sign-in is a redirect to Keycloak, not an XHR. */
  signIn(returnUrl = this.location.pathname + this.location.search): void {
    this.location.href = `/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`;
  }

  signOut(): void {
    const user = this.user();
    if (user.isAuthenticated) {
      this.location.href = user.logoutUrl;
    }
  }

  /** Any API 401 means there is no session. `session-expired` also tells the user why. */
  handleUnauthorized(error: HttpErrorResponse): void {
    this.user.set({ isAuthenticated: false });
    if ((error.error as Problem | null)?.type?.endsWith('session-expired')) {
      this.notice.set('Your session ended. Sign in again.');
    }
  }
}
