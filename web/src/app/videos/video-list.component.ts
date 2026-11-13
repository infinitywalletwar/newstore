import { HttpClient, HttpParams } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { PagedResult, VideoSummary } from '../api/models';

@Component({
  selector: 'app-video-list',
  imports: [RouterLink],
  template: `
    <h1>Videos</h1>
    <input type="search" placeholder="Search videos" data-testid="search" [value]="query()" (input)="search($any($event.target).value)" />
    @if (error()) {
      <p class="error">Could not load videos.</p>
    }
    @if (result(); as r) {
      <ul class="cards">
        @for (video of r.items; track video.id) {
          <li class="card" data-testid="video-card">
            <a [routerLink]="['/videos', video.id]">{{ video.title }}</a>
            <div>{{ video.channel.name }}</div>
          </li>
        } @empty {
          <li>No videos found.</li>
        }
      </ul>
      <button type="button" [disabled]="r.page <= 1" (click)="go(r.page - 1)">Previous</button>
      <button type="button" [disabled]="r.page * r.pageSize >= r.total" (click)="go(r.page + 1)">Next</button>
    }
  `,
})
export class VideoListComponent {
  private readonly http = inject(HttpClient);
  protected readonly query = signal('');
  protected readonly result = signal<PagedResult<VideoSummary> | null>(null);
  protected readonly error = signal(false);

  constructor() {
    this.load(1);
  }

  protected search(q: string): void {
    this.query.set(q);
    this.load(1);
  }

  protected go(page: number): void {
    this.load(page);
  }

  private load(page: number): void {
    const params = new HttpParams().set('q', this.query()).set('page', page);
    this.http.get<PagedResult<VideoSummary>>('/api/videos', { params }).subscribe({
      next: (r) => {
        this.error.set(false);
        this.result.set(r);
      },
      error: () => this.error.set(true),
    });
  }
}
