import { inject } from '@angular/core';
import { CanActivateFn } from '@angular/router';
import { AuthService } from './auth.service';

/** A signed-out viewer is sent to the gateway's sign-in and comes back to the page they asked for. */
export const signedInGuard: CanActivateFn = async (_route, state) => {
  const auth = inject(AuthService);
  await auth.load();
  if (auth.isSignedIn()) {
    return true;
  }
  auth.signIn(state.url);
  return false;
};
