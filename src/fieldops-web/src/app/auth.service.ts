import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, tap } from 'rxjs';
import { LoginResponse } from './login-response';

@Injectable({ providedIn: 'root' })
export class AuthService {
  // Day 105: relative for the same reason as WorkOrderService.
  private readonly apiBaseUrl = '/api/auth';

  // Day 94: kept only in memory (a signal), not localStorage — the simplest
  // possible demo. It means the token is lost on every page refresh; a real
  // app would persist it (with its own tradeoffs, e.g. XSS exposure for
  // localStorage), deliberately out of today's scope.
  private readonly token = signal<string | null>(null);

  // Day 95: same in-memory-only simplification as the token above — the
  // role LoginResponse already carried since Day 93, finally stored
  // somewhere a component can actually read it from.
  private readonly role = signal<string | null>(null);

  constructor(private readonly http: HttpClient) {}

  // Day 122: the API now verifies a password (SECURITY_REVIEW.md F2).
  login(employeeId: number, password: string): Observable<LoginResponse> {
    return this.http.post<LoginResponse>(`${this.apiBaseUrl}/login`, { employeeId, password }).pipe(
      tap(response => {
        this.token.set(response.token);
        this.role.set(response.role);
      }),
    );
  }

  getToken(): string | null {
    return this.token();
  }

  isLoggedIn(): boolean {
    return this.token() !== null;
  }

  // Day 95: a UI-convenience check ONLY. Hiding a button here stops a casual
  // user from finding it; it does nothing to stop someone from calling the
  // API directly — real enforcement belongs on the server, always. (Since
  // Day 121 the server requires a token and enforces the roles it checks,
  // but WorkOrdersController.Create itself is still open to Members.)
  isAdmin(): boolean {
    return this.role() === 'Admin';
  }
}
