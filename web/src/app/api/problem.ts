import { HttpErrorResponse } from '@angular/common/http';
import { Problem } from './models';

/** The text to show for a failed call: the problem's title, or a generic line when the reply had none. */
export function problemTitle(error: unknown): string {
  const title = error instanceof HttpErrorResponse ? (error.error as Problem | null)?.title : undefined;
  return title ?? 'Something went wrong. Try again.';
}
