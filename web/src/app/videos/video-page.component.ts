import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { DeliveryDetails, Edition, Order, VideoDetails } from '../api/models';
import { problemTitle } from '../api/problem';
import { AuthService } from '../auth/auth.service';
import { DeliveryFormComponent } from '../delivery/delivery-form.component';
import { VideoPlayerComponent } from './video-player.component';

@Component({
  selector: 'app-video-page',
  imports: [VideoPlayerComponent, DeliveryFormComponent],
  template: `
    @if (video(); as v) {
      <h1>{{ v.title }}</h1>
      <p>{{ v.channel.name }}</p>
      <p>{{ v.description }}</p>
      @if (v.playbackUrl) {
        <app-video-player [src]="v.playbackUrl" />
      }
    } @else if (notFound()) {
      <p>No such video.</p>
    }
    <h2>Editions</h2>
    @for (e of editions(); track e.id) {
      <div class="card" data-testid="edition" [attr.data-format]="e.format">
        <strong>{{ e.format }}</strong> {{ e.price }} · {{ e.copiesLeft }} left
        @if (auth.ready()) {
          @if (auth.isSignedIn()) {
            <button type="button" data-testid="buy" [disabled]="buying() || !e.onSale" (click)="buy(e)">Buy</button>
          } @else {
            <button type="button" data-testid="sign-in-to-buy" (click)="auth.signIn()">Sign in to buy</button>
          }
        }
      </div>
    }
    @if (deliveryFor(); as edition) {
      <app-delivery-form [busy]="buying()" (confirmed)="post(edition, $event)" (cancelled)="cancelDelivery()" />
    }
    @if (buyError(); as message) {
      <p class="error" role="alert" data-testid="buy-error">{{ message }}</p>
    }
  `,
})
export class VideoPageComponent {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private readonly id = inject(ActivatedRoute).snapshot.paramMap.get('id');
  protected readonly auth = inject(AuthService);

  protected readonly video = signal<VideoDetails | null>(null);
  protected readonly editions = signal<Edition[]>([]);
  protected readonly notFound = signal(false);
  protected readonly buying = signal(false);
  protected readonly buyError = signal<string | null>(null);
  /** The DVD edition whose delivery form is open. */
  protected readonly deliveryFor = signal<Edition | null>(null);

  constructor() {
    this.http.get<VideoDetails>(`/api/videos/${this.id}`).subscribe({
      next: (v) => this.video.set(v),
      error: (e: HttpErrorResponse) => this.notFound.set(e.status === 404),
    });
    this.http.get<Edition[]>(`/api/videos/${this.id}/editions`).subscribe({ next: (e) => this.editions.set(e), error: () => {} });
  }

  /** A DVD needs an address and a pin first, so Buy opens the form; any other format is posted at once. */
  protected buy(edition: Edition): void {
    this.buyError.set(null);
    if (edition.format === 'Dvd') {
      this.deliveryFor.set(edition);
    } else {
      this.post(edition);
    }
  }

  protected cancelDelivery(): void {
    this.deliveryFor.set(null);
    this.buyError.set(null);
  }

  protected post(edition: Edition, delivery?: DeliveryDetails): void {
    this.buying.set(true);
    this.buyError.set(null);
    const body = delivery ? { editionId: edition.id, delivery } : { editionId: edition.id };
    this.http.post<Order>('/api/orders', body).subscribe({
      next: (order) => void this.router.navigate(['/orders', order.id]),
      error: (error: unknown) => {
        this.buying.set(false);
        // A 401 already signed the user out (sessionInterceptor), and the page now offers "Sign in to buy".
        if (!(error instanceof HttpErrorResponse && error.status === 401)) {
          this.buyError.set(problemTitle(error));
        }
      },
    });
  }
}
