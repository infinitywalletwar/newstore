import { InjectionToken } from '@angular/core';

/** The browser's `location`, behind a token so tests can replace it: in jsdom, assigning a new URL logs "Not implemented: navigation to another Document" and a test cannot read where it went. */
export const BROWSER_LOCATION = new InjectionToken<Location>('BROWSER_LOCATION', {
  providedIn: 'root',
  factory: () => window.location,
});
