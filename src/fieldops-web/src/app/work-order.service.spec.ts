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

  it('getById requests the single-work-order endpoint', () => {
    let result: unknown;
    service.getById(2).subscribe(workOrder => (result = workOrder));

    const request = httpMock.expectOne('/api/workorders/2');
    expect(request.request.method).toBe('GET');
    request.flush({ id: 2, title: 'Second', status: 1 });

    expect(result).toEqual({ id: 2, title: 'Second', status: 1 });
  });

  it('getById returns undefined when the API answers 404', () => {
    let result: unknown = 'not-set';
    service.getById(999).subscribe(workOrder => (result = workOrder));

    httpMock.expectOne('/api/workorders/999').flush(null, { status: 404, statusText: 'Not Found' });

    expect(result).toBeUndefined();
  });

  it('getById passes other errors through instead of hiding them', () => {
    let errorStatus: number | undefined;
    service.getById(2).subscribe({ error: (error: { status: number }) => (errorStatus = error.status) });

    httpMock.expectOne('/api/workorders/2').flush(null, { status: 500, statusText: 'Server Error' });

    expect(errorStatus).toBe(500);
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
