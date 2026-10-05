import { Injectable } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { Observable, of, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { WorkOrder } from './work-order';
import { WorkOrderStatusReport } from './work-order-status-report';

@Injectable({ providedIn: 'root' })
export class WorkOrderService {
  // Day 90: hardcoded, same demo organization/employee every other day's
  // curl verification has used. No login/JWT exists yet (that's a later
  // Week 18 topic) — this is a deliberate, temporary stand-in for real
  // authentication, not a production shape.
  // Day 105: relative, not http://localhost:5138 — in a browser, "localhost"
  // means the visitor's own machine, so a hardcoded host only ever worked on
  // a developer laptop. Requests now go to whatever origin served the page;
  // nginx (production) or proxy.conf.json (ng serve) forwards /api onward.
  private readonly apiBaseUrl = '/api/workorders';
  private readonly demoHeaders = new HttpHeaders({
    'X-Organization-Id': '1',
    'X-Employee-Id': '1',
  });

  constructor(private readonly http: HttpClient) {}

  getAll(): Observable<WorkOrder[]> {
    return this.http.get<WorkOrder[]>(this.apiBaseUrl, { headers: this.demoHeaders });
  }

  // Day 115: the Day 91 stand-in (load the whole list, search it here) is
  // gone — the list is now paged on the server, so a work order beyond the
  // first page would never be found. GET /api/workorders/{id} returns 404 for
  // a missing work order or another organization's; that maps to undefined
  // (the detail page's "not found"), while any other error still surfaces.
  getById(id: number): Observable<WorkOrder | undefined> {
    return this.http.get<WorkOrder>(`${this.apiBaseUrl}/${id}`, { headers: this.demoHeaders }).pipe(
      catchError((error: HttpErrorResponse) => (error.status === 404 ? of(undefined) : throwError(() => error))),
    );
  }

  // Day 92: mirrors FieldOps.Api's real CreateWorkOrderRequest(Title, CustomerId?)
  // shape exactly — customerId is optional there too, so null is sent as-is
  // rather than omitted, matching the backend's own nullable int?.
  create(title: string, customerId: number | null): Observable<WorkOrder> {
    return this.http.post<WorkOrder>(
      this.apiBaseUrl,
      { title, customerId },
      { headers: this.demoHeaders },
    );
  }

  // Day 96: FieldOps.Api's existing GET /api/workorders/report (Day 48/49,
  // cached in Redis server-side) — nothing new on the backend, just the
  // first Angular call to it.
  getReport(): Observable<WorkOrderStatusReport> {
    return this.http.get<WorkOrderStatusReport>(`${this.apiBaseUrl}/report`, { headers: this.demoHeaders });
  }
}
