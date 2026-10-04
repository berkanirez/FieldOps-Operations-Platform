import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { authInterceptor } from './auth.interceptor';
import { AuthService } from './auth.service';

describe('authInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;
  let authServiceStub: { getToken: () => string | null };

  beforeEach(() => {
    authServiceStub = { getToken: () => null };

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        { provide: AuthService, useFactory: () => authServiceStub },
      ],
    });

    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('adds an Authorization header when a token exists', () => {
    authServiceStub.getToken = () => 'real-token';

    http.get('/api/workorders').subscribe();

    const request = httpMock.expectOne('/api/workorders');
    expect(request.request.headers.get('Authorization')).toBe('Bearer real-token');
    request.flush([]);
  });

  it('sends no Authorization header when there is no token', () => {
    http.get('/api/workorders').subscribe();

    const request = httpMock.expectOne('/api/workorders');
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush([]);
  });
});
