import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { BROWSER_LOCATION } from '../api/browser-location';

/**
 * The gateway rejects a cookie-authenticated call without `X-CSRF: 1` (ADR 0015). Add it to our own origin's `/api` and
 * `/auth` calls and to nothing else: a header on a call to another site would trigger a CORS preflight and leak the marker.
 */
export const csrfInterceptor: HttpInterceptorFn = (req, next) => {
  const origin = inject(BROWSER_LOCATION).origin;
  const url = new URL(req.url, origin);
  const isOurs = url.origin === origin && ['/api', '/auth'].some((p) => url.pathname === p || url.pathname.startsWith(p + '/'));
  return next(isOurs ? req.clone({ setHeaders: { 'X-CSRF': '1' } }) : req);
};
