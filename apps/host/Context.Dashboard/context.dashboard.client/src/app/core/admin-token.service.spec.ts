import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { AdminTokenService } from './admin-token.service';

describe('AdminTokenService', () => {
  let service: AdminTokenService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(AdminTokenService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('retries cleanly after an initial fetch failure and caches once succeeded', async () => {
    const failedAttempt = firstValueFrom(service.getToken());
    httpMock
      .expectOne((req) => req.url.endsWith('/api/auth/admin-token'))
      .flush('Service unavailable', { status: 503, statusText: 'Service Unavailable' });

    await expect(failedAttempt).rejects.toMatchObject({ status: 503 });

    const secondAttempt = firstValueFrom(service.getToken());
    httpMock.expectOne((req) => req.url.endsWith('/api/auth/admin-token')).flush({ token: 'admin-secret' });
    await expect(secondAttempt).resolves.toEqual({ token: 'admin-secret' });

    const cachedAttempt = firstValueFrom(service.getToken());
    httpMock.expectNone((req) => req.url.endsWith('/api/auth/admin-token'));
    await expect(cachedAttempt).resolves.toEqual({ token: 'admin-secret' });
  });
});
