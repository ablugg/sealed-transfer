import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { AuthService as Auth0Service } from '@auth0/auth0-angular';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';

export interface UserProfile {
  id: string;
  displayName: string;
  email: string;
  organizationId: string | null;
  organizationName: string | null;
  role: string;
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly auth0 = inject(Auth0Service, { optional: true });
  private readonly http = inject(HttpClient);

  readonly user = signal<UserProfile | null>(null);
  readonly displayName = signal<string | null>(null);
  readonly auth0Authenticated = signal(false);
  readonly isAuthenticated = computed(() => this.auth0Authenticated());
  readonly isAdmin = computed(() => this.user()?.role === 'Admin');
  readonly enabled = !!environment.auth0Domain;

  async initialize(): Promise<void> {
    if (!this.enabled || !this.auth0) return;

    this.auth0.isAuthenticated$.subscribe((isAuth) => {
      this.auth0Authenticated.set(isAuth);
      if (isAuth) {
        this.loadAuth0User();
        this.loadProfile();
      } else {
        this.user.set(null);
        this.displayName.set(null);
      }
    });
  }

  signIn(): void {
    this.auth0?.loginWithRedirect().subscribe();
  }

  signOut(): void {
    this.user.set(null);
    this.displayName.set(null);
    this.auth0Authenticated.set(false);
    this.auth0?.logout({ logoutParams: { returnTo: window.location.origin } }).subscribe();
  }

  private loadAuth0User(): void {
    // Auth0 SDK provides the ID token claims (name, email) via user$.
    this.auth0!.user$.subscribe((auth0User) => {
      if (auth0User) {
        this.displayName.set(auth0User.name ?? auth0User.nickname ?? auth0User.email ?? 'Signed in');
      }
    });
  }

  private async loadProfile(): Promise<void> {
    try {
      const profile = await firstValueFrom(this.http.get<UserProfile>('/api/identity/me'));
      if (profile) {
        this.user.set(profile);
        this.displayName.set(profile.displayName);
      }
    } catch {
      // API call may fail if user record doesn't exist yet — Auth0 user info is still shown.
    }
  }
}
