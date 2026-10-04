import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { WorkOrderService } from './work-order.service';

describe('WorkOrderService', () => {
  let service: WorkOrderService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(WorkOrderService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('should create', () => {
    expect(service).toBeTruthy();
  });

  it('getById finds the matching work order from the list endpoint', () => {
    let result: unknown;
    service.getById(2).subscribe(workOrder => (result = workOrder));

    httpMock.expectOne('/api/workorders').flush([
      { id: 1, title: 'First', status: 0 },
      { id: 2, title: 'Second', status: 1 },
    ]);

    expect(result).toEqual({ id: 2, title: 'Second', status: 1 });
  });

  it('getById returns undefined when no work order matches', () => {
    let result: unknown = 'not-set';
    service.getById(999).subscribe(workOrder => (result = workOrder));

    httpMock.expectOne('/api/workorders').flush([{ id: 1, title: 'First', status: 0 }]);

    expect(result).toBeUndefined();
  });

  it('getReport requests the real report endpoint and returns the counts', () => {
    let result: unknown;
    service.getReport().subscribe(report => (result = report));

    const request = httpMock.expectOne('/api/workorders/report');
    expect(request.request.method).toBe('GET');
    request.flush({ organizationId: 1, open: 2, assigned: 0, inProgress: 1, completed: 4 });

    expect(result).toEqual({ organizationId: 1, open: 2, assigned: 0, inProgress: 1, completed: 4 });
  });
});
