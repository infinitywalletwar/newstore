import { Routes } from '@angular/router';
import { signedInGuard } from './auth/signed-in.guard';
import { MyCopiesComponent } from './copies/my-copies.component';
import { OrderPageComponent } from './orders/order-page.component';
import { VideoListComponent } from './videos/video-list.component';
import { VideoPageComponent } from './videos/video-page.component';

// No path may start with api, auth or health: the gateway routes those to the services, never to index.html.
export const routes: Routes = [
  { path: '', component: VideoListComponent },
  { path: 'videos/:id', component: VideoPageComponent },
  { path: 'orders/:id', component: OrderPageComponent },
  { path: 'me/copies', component: MyCopiesComponent, canActivate: [signedInGuard] },
];
