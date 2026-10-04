import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AuthService } from './auth.service';

describe('AuthService', () => {
  let service: AuthService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(AuthService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('starts logged out, with no token, and not an admin', () => {
    expect(service.isLoggedIn()).toBe(false);
    expect(service.getToken()).toBeNull();
    expect(service.isAdmin()).toBe(false);
  });

  it('stores the token after a successful login', () => {
    service.login(1).subscribe();

    const request = httpMock.expectOne('/api/auth/login');
    expect(request.request.body).toEqual({ employeeId: 1 });
    request.flush({ token: 'fake-token', employeeId: 1, organizationId: 1, role: 'Admin' });

    expect(service.isLoggedIn()).toBe(true);
    expect(service.getToken()).toBe('fake-token');
  });

  it('isAdmin reflects the role returned by login', () => {
    service.login(1).subscribe();
    httpMock
      .expectOne('/api/auth/login')
      .flush({ token: 'fake-token', employeeId: 1, organizationId: 1, role: 'Admin' });
    expect(service.isAdmin()).toBe(true);
  });

  it('isAdmin is false for a non-Admin role', () => {
    service.login(2).subscribe();
    httpMock
      .expectOne('/api/auth/login')
      .flush({ token: 'fake-token', employeeId: 2, organizationId: 1, role: 'Member' });
    expect(service.isAdmin()).toBe(false);
  });
});
