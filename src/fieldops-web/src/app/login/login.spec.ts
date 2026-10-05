import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';
import { Login } from './login';
import { AuthService } from '../auth.service';

describe('Login', () => {
  let component: Login;
  let fixture: ComponentFixture<Login>;
  let loginSpy: ReturnType<typeof vi.fn>;
  let navigateSpy: ReturnType<typeof vi.fn>;

  function setUp() {
    navigateSpy = vi.fn();
    TestBed.configureTestingModule({
      imports: [Login],
      providers: [
        { provide: AuthService, useValue: { login: loginSpy } },
        { provide: Router, useValue: { navigate: navigateSpy } },
      ],
    });
    fixture = TestBed.createComponent(Login);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  it('does not call the service when the form is invalid (empty employee id)', () => {
    loginSpy = vi.fn();
    setUp();

    component.submit();

    expect(loginSpy).not.toHaveBeenCalled();
  });

  it('navigates to the list on successful login', () => {
    loginSpy = vi.fn().mockReturnValue(of({ token: 't', employeeId: 1, organizationId: 1, role: 'Admin' }));
    setUp();

    component['form'].controls.employeeId.setValue(1);
    component['form'].controls.password.setValue('FieldOps-Demo-2026!');
    component.submit();

    expect(loginSpy).toHaveBeenCalledWith(1, 'FieldOps-Demo-2026!');
    expect(navigateSpy).toHaveBeenCalledWith(['/']);
  });

  it('does not call the service when the password is empty', () => {
    loginSpy = vi.fn();
    setUp();

    component['form'].controls.employeeId.setValue(1);
    component.submit();

    expect(loginSpy).not.toHaveBeenCalled();
  });

  it('shows an error message when the login call fails', () => {
    loginSpy = vi.fn().mockReturnValue(throwError(() => new Error('not found')));
    setUp();

    component['form'].controls.employeeId.setValue(999);
    component['form'].controls.password.setValue('wrong-password');
    component.submit();

    expect(component['errorMessage']()).toContain('Login failed');
    expect(navigateSpy).not.toHaveBeenCalled();
  });
});
