import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    title: 'Send a file · Sealed Transfer',
    loadComponent: () => import('./pages/send/send').then((m) => m.Send),
  },
  {
    path: 'transfers',
    title: 'My transfers · Sealed Transfer',
    loadComponent: () => import('./pages/transfers/transfers').then((m) => m.Transfers),
  },
  {
    path: 't/:id',
    title: 'Receive a file · Sealed Transfer',
    loadComponent: () => import('./pages/receive/receive').then((m) => m.Receive),
  },
  { path: '**', redirectTo: '' },
];
