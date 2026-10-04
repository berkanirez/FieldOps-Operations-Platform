import { Injectable } from '@angular/core';
import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';
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

  // Day 91: FieldOps.Api has no single-work-order endpoint today — adding
  // one is a backend change outside today's scope (Angular routing). Reusing
  // the existing list endpoint and filtering client-side is a deliberate,
  // temporary simplification: it works, but refetches every work order just
  // to show one. A dedicated GET /api/workorders/{id} would be the real fix.
  getById(id: number): Observable<WorkOrder | undefined> {
    return this.getAll().pipe(map(workOrders => workOrders.find(w => w.id === id)));
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
