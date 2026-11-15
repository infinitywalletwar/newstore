import { HttpClient } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BROWSER_LOCATION } from '../api/browser-location';
import { DownloadLink, MyCopy } from '../api/models';
import { problemTitle } from '../api/problem';

@Component({
  selector: 'app-my-copies',
  imports: [RouterLink, DatePipe],
  template: `
    <h1>My copies</h1>
    @if (error()) {
      <p class="error">Could not load your copies.</p>
    }
    @if (downloadError(); as message) {
      <p class="error" role="alert" data-testid="download-error">{{ message }}</p>
    }
    @if (copies(); as list) {
      <table>
        <thead><tr><th>Copy</th><th>Format</th><th>Price</th><th>Bought</th><th>Delivery</th><th></th></tr></thead>
        <tbody>
          @for (c of list; track c.orderId) {
            <tr data-testid="copy-row">
              <td>#{{ c.copyNumber }}</td>
              <td><a [routerLink]="['/videos', c.videoId]">{{ c.format }}</a></td>
              <td>{{ c.price }}</td>
              <td>{{ c.completedAt | date: 'medium' }}</td>
              <td>{{ c.deliveryStatus ?? '—' }}</td>
              <td>
                @if (c.format === 'Digital') {
                  <button type="button" data-testid="download" (click)="download(c)">Download</button>
                }
              </td>
            </tr>
          } @empty {
            <tr><td colspan="6">You have no copies yet.</td></tr>
          }
        </tbody>
      </table>
    }
  `,
})
export class MyCopiesComponent {
  private readonly http = inject(HttpClient);
  private readonly location = inject(BROWSER_LOCATION);
  protected readonly copies = signal<MyCopy[] | null>(null);
  protected readonly error = signal(false);
  protected readonly downloadError = signal<string | null>(null);

  constructor() {
    this.http.get<MyCopy[]>('/api/me/copies').subscribe({
      next: (c) => this.copies.set(c),
      error: () => this.error.set(true),
    });
  }

  /** Asks for a presigned link to the original and sends the browser to it. */
  protected download(copy: MyCopy): void {
    this.downloadError.set(null);
    this.http.post<DownloadLink>('/api/downloads', { videoId: copy.videoId }).subscribe({
      next: (link) => (this.location.href = link.url),
      error: (error: unknown) => this.downloadError.set(problemTitle(error)),
    });
  }
}
