import { Routes } from '@angular/router';
import { signedInGuard } from './auth/signed-in.guard';
import { MyCopiesComponent } from './copies/my-copies.component';
import { OrderPageComponent } from './orders/order-page.component';
import { StudioComponent } from './studio/studio.component';
import { VideoListComponent } from './videos/video-list.component';
import { VideoPageComponent } from './videos/video-page.component';

// No path may start with api, auth or health: the gateway routes those to the services, never to index.html.
export const routes: Routes = [
  { path: '', component: VideoListComponent },
  { path: 'videos/:id', component: VideoPageComponent },
  { path: 'orders/:id', component: OrderPageComponent },
  // Lazy: the tracking page brings Leaflet and the SignalR client, which would push the first load past the 500 kB budget.
  {
    path: 'shipments/:id',
    loadComponent: () => import('./shipments/tracking-page.component').then((m) => m.TrackingPageComponent),
    canActivate: [signedInGuard],
  },
  { path: 'studio', component: StudioComponent, canActivate: [signedInGuard] },
  { path: 'me/copies', component: MyCopiesComponent, canActivate: [signedInGuard] },
];
