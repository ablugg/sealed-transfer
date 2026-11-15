import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

export interface CreateTransferRequest {
  readonly cryptoHeader: string;
  readonly encryptedMetadata: string;
  readonly chunkCount: number;
  readonly ciphertextBytes: number;
  readonly expiresInHours: number;
  readonly maxDownloads: number | null;
  readonly passwordProtected: boolean;
}

export interface CreateTransferResponse {
  readonly id: string;
  readonly ownerToken: string;
  readonly expiresAt: string;
}

export interface TransferInfo {
  readonly id: string;
  readonly cryptoHeader: string;
  readonly encryptedMetadata: string;
  readonly chunkCount: number;
  readonly ciphertextBytes: number;
  readonly expiresAt: string;
  readonly downloadsRemaining: number | null;
  readonly passwordProtected: boolean;
}

export interface DownloadGrant {
  readonly downloadToken: string;
  readonly expiresAt: string;
}

const OWNER_TOKEN_HEADER = 'X-Owner-Token';
const DOWNLOAD_TOKEN_HEADER = 'X-Download-Token';

/** Thin HTTP client for the transfers API. Everything it sends is ciphertext or public parameters. */
@Injectable({ providedIn: 'root' })
export class TransferApi {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/transfers';

  create(request: CreateTransferRequest): Observable<CreateTransferResponse> {
    return this.http.post<CreateTransferResponse>(this.baseUrl, request);
  }

  uploadChunk(id: string, ownerToken: string, index: number, data: ArrayBuffer): Observable<void> {
    return this.http.put<void>(`${this.baseUrl}/${id}/chunks/${index}`, data, {
      headers: { [OWNER_TOKEN_HEADER]: ownerToken, 'Content-Type': 'application/octet-stream' },
    });
  }

  complete(id: string, ownerToken: string): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/${id}/complete`, null, {
      headers: { [OWNER_TOKEN_HEADER]: ownerToken },
    });
  }

  get(id: string): Observable<TransferInfo> {
    return this.http.get<TransferInfo>(`${this.baseUrl}/${id}`);
  }

  startDownload(id: string): Observable<DownloadGrant> {
    return this.http.post<DownloadGrant>(`${this.baseUrl}/${id}/downloads`, null);
  }

  downloadChunk(id: string, downloadToken: string, index: number): Observable<ArrayBuffer> {
    return this.http.get(`${this.baseUrl}/${id}/chunks/${index}`, {
      headers: { [DOWNLOAD_TOKEN_HEADER]: downloadToken },
      responseType: 'arraybuffer',
    });
  }

  revoke(id: string, ownerToken: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${id}`, { headers: { [OWNER_TOKEN_HEADER]: ownerToken } });
  }
}
