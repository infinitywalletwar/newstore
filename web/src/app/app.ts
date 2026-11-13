import { Component, inject } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';
import { AuthService } from './auth/auth.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink],
  template: `
    <header>
      <a routerLink="/" class="brand">newstore</a>
      <nav>
        @if (auth.user(); as user) {
          @if (user.isAuthenticated) {
            <a routerLink="/me/copies" data-testid="my-copies-link">My copies</a>
            <span data-testid="user-name">{{ user.name }}</span>
            <button type="button" data-testid="sign-out" (click)="auth.signOut()">Sign out</button>
          } @else {
            <button type="button" data-testid="sign-in" (click)="auth.signIn()">Sign in</button>
          }
        }
      </nav>
    </header>
    @if (auth.notice(); as notice) {
      <p class="notice" role="status">{{ notice }}</p>
    }
    <main><router-outlet /></main>
  `,
})
export class App {
  protected readonly auth = inject(AuthService);

  constructor() {
    void this.auth.load();
  }
}
