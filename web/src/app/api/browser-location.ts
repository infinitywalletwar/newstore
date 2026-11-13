import { InjectionToken } from '@angular/core';

/** The browser's `location`, behind a token so tests can replace it: jsdom cannot navigate. */
export const BROWSER_LOCATION = new InjectionToken<Location>('BROWSER_LOCATION', {
  providedIn: 'root',
  factory: () => window.location,
});
