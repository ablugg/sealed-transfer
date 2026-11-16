import { AuthConfig } from '@auth0/auth0-angular';
import { environment } from '../../environments/environment';

export function createAuth0Config(): AuthConfig {
  return {
    domain: environment.auth0Domain,
    clientId: environment.auth0ClientId,
    authorizationParams: {
      redirect_uri: typeof window !== 'undefined' ? window.location.origin : '',
      ...(environment.auth0Audience ? { audience: environment.auth0Audience } : {}),
    },
    httpInterceptor: {
      allowedList: [
        { uri: '/api/identity/*' },
        // Attach token to transfer creation if the user is signed in.
        { uri: '/api/transfers', allowAnonymous: true },
      ],
    },
  };
}
