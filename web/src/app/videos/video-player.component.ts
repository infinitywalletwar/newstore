import { Component, ElementRef, effect, input, signal, viewChild } from '@angular/core';
import type Hls from 'hls.js';

/**
 * Plays an HLS master playlist. Order matters (hls.js README: check `Hls.isSupported()` first): hls.js through Media
 * Source Extensions where the browser has them, the browser's own HLS (Safari) otherwise, an error when it has neither.
 */
@Component({
  selector: 'app-video-player',
  template: `
    <video #video controls data-testid="player"></video>
    @if (failed()) {
      <p class="error" role="alert" data-testid="player-error">This browser cannot play this video.</p>
    }
    @if (heights().length > 0) {
      <label>
        Quality
        <select data-testid="quality" (change)="choose($any($event.target).value)">
          <option value="-1">Auto</option>
          @for (height of heights(); track $index) {
            <option [value]="$index">{{ height }}p</option>
          }
        </select>
      </label>
    }
  `,
})
export class VideoPlayerComponent {
  readonly src = input.required<string>();
  private readonly video = viewChild.required<ElementRef<HTMLVideoElement>>('video');

  protected readonly failed = signal(false);
  protected readonly heights = signal<number[]>([]);
  private hls: Hls | null = null;

  constructor() {
    // The cleanup runs before the effect runs again (a new `src`) and when the component is destroyed. hls.js is loaded
    // with `import()`, so only a page that shows a player downloads it; `cancelled` covers a destroy during that load.
    effect((onCleanup) => {
      const src = this.src();
      const video = this.video().nativeElement;
      let cancelled = false;
      onCleanup(() => {
        cancelled = true;
        this.release();
      });
      this.failed.set(false);
      this.heights.set([]);
      void import('hls.js').then(({ default: Hls }) => {
        if (cancelled) {
          return;
        }
        if (Hls.isSupported()) {
          const hls = new Hls();
          this.hls = hls;
          hls.on(Hls.Events.MANIFEST_PARSED, () => this.heights.set(hls.levels.map((level) => level.height)));
          hls.on(Hls.Events.ERROR, (_event, data) => {
            if (data.fatal) {
              this.release();
              this.heights.set([]);
              this.failed.set(true);
            }
          });
          hls.loadSource(src);
          hls.attachMedia(video);
        } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
          video.src = src;
        } else {
          this.failed.set(true);
        }
      }).catch(() => {
        // The chunk could not be loaded (offline, a deploy replaced it): no blank player.
        if (!cancelled) {
          this.failed.set(true);
        }
      });
    });
  }

  private release(): void {
    this.hls?.destroy();
    this.hls = null;
  }

  /** `-1` is Auto: hls.js picks the variant by bandwidth. Any other value pins the variant at that index of `hls.levels`. */
  protected choose(value: string): void {
    if (this.hls) {
      this.hls.currentLevel = Number(value);
    }
  }
}
