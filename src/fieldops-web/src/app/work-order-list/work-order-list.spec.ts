import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { WorkOrderList } from './work-order-list';
import { WorkOrderService } from '../work-order.service';
import { AuthService } from '../auth.service';

describe('WorkOrderList', () => {
  describe('with the real WorkOrderService (HTTP shape only)', () => {
    let fixture: ComponentFixture<WorkOrderList>;
    let httpMock: HttpTestingController;

    beforeEach(async () => {
      await TestBed.configureTestingModule({
        imports: [WorkOrderList],
        providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
      }).compileComponents();

      fixture = TestBed.createComponent(WorkOrderList);
      httpMock = TestBed.inject(HttpTestingController);
    });

    afterEach(() => {
      httpMock.verify();
    });

    it('should create', () => {
      fixture.detectChanges();
      httpMock.expectOne('/api/workorders').flush([]);
      expect(fixture.componentInstance).toBeTruthy();
    });

    it('should request the real endpoint without identity headers (the JWT carries identity)', () => {
      fixture.detectChanges();
      const request = httpMock.expectOne('/api/workorders');
      expect(request.request.method).toBe('GET');
      expect(request.request.headers.has('X-Organization-Id')).toBe(false);
      expect(request.request.headers.has('X-Employee-Id')).toBe(false);
      request.flush([]);
    });

    // Day 91 correction: Day 90's comment here originally blamed "a test-tooling
    // limitation" for the real HTTP flush not updating the rendered table. That
    // diagnosis was WRONG, caught live by Berkan testing in an actual browser
    // (not just here): this app is zoneless (no zone.js), and a plain class
    // field reassigned inside an `.subscribe(...)` callback never tells Angular
    // to re-render — there's no automatic "an async thing finished" hook without
    // zone.js. `workOrders` is now a `signal`, which Angular's zoneless change
    // detection DOES react to. This test is the real regression test for that
    // fix: a genuine HttpTestingController flush, through the real component,
    // now correctly produces rendered rows.
    it('renders real rows after a genuine HTTP flush (regression test for the zoneless signal fix)', () => {
      fixture.detectChanges();
      httpMock.expectOne('/api/workorders').flush([{ id: 1, title: 'Real flush order', status: 0 }]);
      fixture.detectChanges();

      const rows = fixture.nativeElement.querySelectorAll('tbody tr');
      expect(rows.length).toBe(1);
      expect(rows[0].textContent).toContain('Real flush order');
    });
  });

  describe('rendering (stubbed service, synchronous data)', () => {
    it('renders one row per work order returned by the service', () => {
      TestBed.configureTestingModule({
        imports: [WorkOrderList],
        providers: [
          provideRouter([]),
          { provide: WorkOrderService, useValue: { getAll: () => of([{ id: 7, title: 'Stubbed order', status: 2 }]) } },
          { provide: AuthService, useValue: { isAdmin: () => false } },
        ],
      });
      const fixture = TestBed.createComponent(WorkOrderList);
      fixture.detectChanges();

      const rows = fixture.nativeElement.querySelectorAll('tbody tr');
      expect(rows.length).toBe(1);
      expect(rows[0].textContent).toContain('Stubbed order');
      expect(rows[0].textContent).toContain('InProgress');
    });
  });

  describe('role-aware "+ New Work Order" link (Day 95)', () => {
    function setUp(isAdmin: boolean) {
      TestBed.configureTestingModule({
        imports: [WorkOrderList],
        providers: [
          provideRouter([]),
          { provide: WorkOrderService, useValue: { getAll: () => of([]) } },
          { provide: AuthService, useValue: { isAdmin: () => isAdmin } },
        ],
      });
      const fixture = TestBed.createComponent(WorkOrderList);
      fixture.detectChanges();
      return fixture;
    }

    it('shows the link for an Admin', () => {
      const fixture = setUp(true);
      const link: HTMLAnchorElement | null = fixture.nativeElement.querySelector('a[href="/work-orders/new"]');
      expect(link).not.toBeNull();
    });

    it('hides the link for a non-Admin', () => {
      const fixture = setUp(false);
      const link: HTMLAnchorElement | null = fixture.nativeElement.querySelector('a[href="/work-orders/new"]');
      expect(link).toBeNull();
    });
  });
});
