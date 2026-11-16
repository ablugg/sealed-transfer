import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { APP_INITIALIZER, ApplicationConfig, provideBrowserGlobalErrorListeners, makeEnvironmentProviders, EnvironmentProviders } from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { provideAuth0, authHttpInterceptorFn } from '@auth0/auth0-angular';
import { routes } from './app.routes';
import { createAuth0Config } from './auth/auth-config';
import { AuthService } from './auth/auth.service';
import { environment } from '../environments/environment';

function initializeAuth(authService: AuthService) {
  return () => authService.initialize();
}

function getAuth0Providers(): EnvironmentProviders[] {
  if (!environment.auth0Domain) {
    return [];
  }

  return [
    provideAuth0(createAuth0Config()),
  ];
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(
      withFetch(),
      ...(environment.auth0Domain ? [withInterceptors([authHttpInterceptorFn])] : []),
    ),
    ...getAuth0Providers(),
    {
      provide: APP_INITIALIZER,
      useFactory: initializeAuth,
      deps: [AuthService],
      multi: true,
    },
  ],
};
