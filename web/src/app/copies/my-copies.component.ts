import { HttpClient } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MyCopy } from '../api/models';

@Component({
  selector: 'app-my-copies',
  imports: [RouterLink, DatePipe],
  template: `
    <h1>My copies</h1>
    @if (error()) {
      <p class="error">Could not load your copies.</p>
    }
    @if (copies(); as list) {
      <table>
        <thead><tr><th>Copy</th><th>Format</th><th>Price</th><th>Bought</th><th>Delivery</th></tr></thead>
        <tbody>
          @for (c of list; track c.orderId) {
            <tr data-testid="copy-row">
              <td>#{{ c.copyNumber }}</td>
              <td><a [routerLink]="['/videos', c.videoId]">{{ c.format }}</a></td>
              <td>{{ c.price }}</td>
              <td>{{ c.completedAt | date: 'medium' }}</td>
              <td>{{ c.deliveryStatus ?? '—' }}</td>
            </tr>
          } @empty {
            <tr><td colspan="5">You have no copies yet.</td></tr>
          }
        </tbody>
      </table>
    }
  `,
})
export class MyCopiesComponent {
  protected readonly copies = signal<MyCopy[] | null>(null);
  protected readonly error = signal(false);

  constructor() {
    inject(HttpClient).get<MyCopy[]>('/api/me/copies').subscribe({
      next: (c) => this.copies.set(c),
      error: () => this.error.set(true),
    });
  }
}
