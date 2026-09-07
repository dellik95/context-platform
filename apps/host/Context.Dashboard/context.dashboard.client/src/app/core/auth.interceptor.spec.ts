import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router } from '@angular/router';
import { Observable, firstValueFrom, of, throwError } from 'rxjs';
import { ApiService } from './api.service';
import { authInterceptor } from './auth.interceptor';
import { ProjectDto } from './models';
import { ProjectTokenService } from './project-token.service';

/**
 * Covers the interceptor-level self-heal for a stale project token (see auth.interceptor.ts's own
 * remarks) -- ProjectContextService.init only handles "no token stored at all"; this is the
 * complementary "token stored but the server rotated it" case. ApiService is stubbed so these
 * tests exercise only the interceptor's retry logic, not the real reissue-endpoint wiring (already
 * covered by ProjectEndpoints.ReissueTokenAsync on the server and ApiService.reissueToken itself).
 */
describe('authInterceptor stale project-token retry', () => {
  const PROJECT_ID = 'proj-1';
  const URL = '/api/projects/proj-1/status';

  let httpMock: HttpTestingController;
  let http: HttpClient;
  let projectTokenService: ProjectTokenService;

  function fakeProject(token: string | null): ProjectDto {
    return { id: PROJECT_ID, name: 'Test', rootPath: '/x', vcsRemote: null, createdAt: '2026-01-01T00:00:00Z', token };
  }

  /** Router stub exposing just the `:id` param currentProjectId() reads off the snapshot. */
  function routerStub(): Router {
    const leaf = { paramMap: { get: (key: string) => (key === 'id' ? PROJECT_ID : null) }, firstChild: null } as unknown as ActivatedRouteSnapshot;
    return { routerState: { snapshot: { root: leaf } } } as unknown as Router;
  }

  function configure(reissueToken: (id: string) => Observable<ProjectDto>): void {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        { provide: Router, useFactory: routerStub },
        { provide: ApiService, useValue: { reissueToken } as Partial<ApiService> },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    http = TestBed.inject(HttpClient);
    projectTokenService = TestBed.inject(ProjectTokenService);
    projectTokenService.setToken(PROJECT_ID, 'stale-token');
  }

  afterEach(() => httpMock.verify());

  it('reissues the token and retries the request exactly once on a stale-token 401', async () => {
    configure(() => of(fakeProject('fresh-token')));

    const resultPromise = firstValueFrom(http.get(URL));

    const firstAttempt = httpMock.expectOne(URL);
    expect(firstAttempt.request.headers.get('Authorization')).toBe('Bearer stale-token');
    firstAttempt.flush(null, { status: 401, statusText: 'Unauthorized' });

    const retry = httpMock.expectOne(URL);
    expect(retry.request.headers.get('Authorization')).toBe('Bearer fresh-token');
    retry.flush({ ok: true });

    await expect(resultPromise).resolves.toEqual({ ok: true });
    expect(projectTokenService.getToken(PROJECT_ID)).toBe('fresh-token');
  });

  it('propagates the original 401 when the reissue call itself fails', async () => {
    configure(() => throwError(() => new HttpErrorResponse({ status: 403, statusText: 'Forbidden' })));

    const resultPromise = firstValueFrom(http.get(URL));

    httpMock.expectOne(URL).flush(null, { status: 401, statusText: 'Unauthorized' });

    await expect(resultPromise).rejects.toMatchObject({ status: 401 });
    httpMock.expectNone(URL);
    expect(projectTokenService.getToken(PROJECT_ID)).toBe('stale-token');
  });

  it('propagates the retried 401 as-is when the reissued token is also rejected, without looping', async () => {
    configure(() => of(fakeProject('also-bad-token')));

    const resultPromise = firstValueFrom(http.get(URL));

    httpMock.expectOne(URL).flush(null, { status: 401, statusText: 'Unauthorized' });

    const retry = httpMock.expectOne(URL);
    expect(retry.request.headers.get('Authorization')).toBe('Bearer also-bad-token');
    retry.flush(null, { status: 401, statusText: 'Unauthorized' });

    await expect(resultPromise).rejects.toMatchObject({ status: 401 });
    httpMock.expectNone(URL);
  });
});
