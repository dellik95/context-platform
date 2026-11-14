import { TestBed } from '@angular/core/testing';
import { DonutChartComponent, DonutSegment } from './donut-chart.component';

describe('DonutChartComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DonutChartComponent],
    }).compileComponents();
  });

  it('shows empty state when all segment values are zero', () => {
    const fixture = TestBed.createComponent(DonutChartComponent);
    fixture.componentRef.setInput('segments', [
      { label: 'Done', value: 0, color: '#10b981' },
      { label: 'Failed', value: 0, color: '#ef4444' },
    ] satisfies DonutSegment[]);
    fixture.componentRef.setInput('emptyLabel', 'Nothing here');
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Nothing here');
  });

  it('renders positive segments, center total, and updates active segment on legend hover', () => {
    const fixture = TestBed.createComponent(DonutChartComponent);
    fixture.componentRef.setInput('segments', [
      { label: 'Done', value: 75, color: '#10b981' },
      { label: 'Pending', value: 25, color: '#f59e0b' },
      { label: 'Failed', value: 0, color: '#ef4444' },
    ] satisfies DonutSegment[]);
    fixture.componentRef.setInput('centerLabel', 'Jobs');
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelectorAll('li').length).toBe(2);
    expect(host.textContent).toContain('100');
    expect(host.textContent).toContain('Jobs');

    const secondButton = host.querySelectorAll('li button')[1] as HTMLButtonElement;
    secondButton.dispatchEvent(new MouseEvent('mouseenter'));
    fixture.detectChanges();

    expect(host.textContent).toContain('25%');
  });
});
