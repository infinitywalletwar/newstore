import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { BROWSER_LOCATION } from '../api/browser-location';
import { FINAL_STATUSES, Order } from '../api/models';
import { problemTitle } from '../api/problem';

const POLL_INTERVAL_MS = 1000;
const POLL_LIMIT_MS = 2 * 60 * 1000;

@Component({
  selector: 'app-order-page',
  template: `
    @if (order(); as o) {
      <h1>Order</h1>
      <p>Status: <strong data-testid="order-status">{{ o.status }}</strong></p>
      @switch (o.status) {
        @case ('Reserved') {
          <p>Held for you until <time data-testid="reserved-until">{{ o.reservedUntil }}</time>.</p>
          <button type="button" data-testid="pay" [disabled]="paying()" (click)="pay()">Pay</button>
        }
        @case ('Paying') {
          <p>Held until <time data-testid="reserved-until">{{ o.reservedUntil }}</time>. Waiting for the payment.</p>
        }
        @case ('Completed') {
          <p>Your copy number is <strong data-testid="copy-number">{{ o.copyNumber }}</strong>.</p>
        }
        @case ('Expired') {
          <p>The hold ended before the payment arrived.</p>
        }
        @case ('Failed') {
          <p>The payment failed.</p>
        }
      }
      @if (timedOut()) {
        <p data-testid="poll-timeout">Still processing, refresh later.</p>
      }
    } @else if (loadError(); as message) {
      <p class="error">{{ message }}</p>
    }
    @if (payError(); as message) {
      <p class="error" data-testid="pay-error">{{ message }}</p>
    }
  `,
})
export class OrderPageComponent {
  private readonly http = inject(HttpClient);
  private readonly location = inject(BROWSER_LOCATION);
  private readonly id = inject(ActivatedRoute).snapshot.paramMap.get('id');
  private readonly startedAt = Date.now();
  private timer?: ReturnType<typeof setTimeout>;

  protected readonly order = signal<Order | null>(null);
  protected readonly loadError = signal<string | null>(null);
  protected readonly payError = signal<string | null>(null);
  protected readonly paying = signal(false);
  protected readonly timedOut = signal(false);

  constructor() {
    inject(DestroyRef).onDestroy(() => clearTimeout(this.timer));
    this.refresh();
  }

  /** Reads the order once, then asks again a second later while it is neither final nor waiting for the buyer to pay. */
  private refresh(): void {
    this.http.get<Order>(`/api/orders/${this.id}`).subscribe({
      next: (order) => {
        this.order.set(order);
        this.schedulePoll(order);
      },
      error: (error: unknown) => {
        this.loadError.set(error instanceof HttpErrorResponse && error.status === 404 ? 'No such order.' : problemTitle(error));
      },
    });
  }

  private schedulePoll(order: Order): void {
    if (order.status === 'Reserved' || FINAL_STATUSES.includes(order.status)) {
      return;
    }
    if (Date.now() - this.startedAt >= POLL_LIMIT_MS) {
      this.timedOut.set(true);
      return;
    }
    this.timer = setTimeout(() => this.refresh(), POLL_INTERVAL_MS);
  }

  protected pay(): void {
    this.paying.set(true);
    this.payError.set(null);
    this.http.post<{ checkoutUrl: string }>(`/api/orders/${this.id}/pay`, null).subscribe({
      next: (r) => (this.location.href = r.checkoutUrl),
      error: (error: unknown) => {
        this.paying.set(false);
        if (!(error instanceof HttpErrorResponse && error.status === 401)) {
          this.payError.set(problemTitle(error));
        }
      },
    });
  }
}
