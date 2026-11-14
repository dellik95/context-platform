import { TestBed } from '@angular/core/testing';
import { UsageDayDto } from '../../core/models';
import { ActivityBarChartComponent } from './activity-bar-chart.component';

describe('ActivityBarChartComponent', () => {
  const sampleDays: UsageDayDto[] = [
    { day: '2026-11-10', tokens: 1200, calls: 4, estimatedSavedTokens: 3400 },
    { day: '2026-11-11', tokens: 800, calls: 3, estimatedSavedTokens: 2100 },
  ];

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ActivityBarChartComponent],
    }).compileComponents();
  });

  it('renders fallback text when no daily data exists', () => {
    const fixture = TestBed.createComponent(ActivityBarChartComponent);
    fixture.componentRef.setInput('days', []);
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('No daily data');
  });

  it('renders both series and allows switching visibility between spent and saved', () => {
    const fixture = TestBed.createComponent(ActivityBarChartComponent);
    fixture.componentRef.setInput('days', sampleDays);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelectorAll('rect.fill-indigo-500').length).toBe(2);
    expect(host.querySelectorAll('rect.fill-emerald-500').length).toBe(2);

    const buttons = Array.from(host.querySelectorAll('button'));
    const spentOnly = buttons.find((b) => b.textContent?.includes('Spent'))!;
    spentOnly.click();
    fixture.detectChanges();

    expect(host.querySelectorAll('rect.fill-indigo-500').length).toBe(2);
    expect(host.querySelectorAll('rect.fill-emerald-500').length).toBe(0);

    const savedOnly = buttons.find((b) => b.textContent?.includes('Saved est.'))!;
    savedOnly.click();
    fixture.detectChanges();

    expect(host.querySelectorAll('rect.fill-indigo-500').length).toBe(0);
    expect(host.querySelectorAll('rect.fill-emerald-500').length).toBe(2);
    expect(savedOnly.getAttribute('aria-pressed')).toBe('true');

    const firstGroup = host.querySelector('svg g[role="button"]')!;
    firstGroup.dispatchEvent(new MouseEvent('click'));
    fixture.detectChanges();
    expect(host.textContent).toContain('3,400');
  });
});
