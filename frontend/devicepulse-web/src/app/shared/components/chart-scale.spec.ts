import { niceStep } from './charts.component';
import { severityLevel } from './ui.components';

describe('niceStep', () => {
  it('rounds up to 1, 2, 2.5, 5 or 10 times a power of ten', () => {
    expect(niceStep(0.7)).toBe(1);
    expect(niceStep(1.3)).toBe(2);
    expect(niceStep(2.2)).toBe(2.5);
    expect(niceStep(3.4)).toBe(5);
    expect(niceStep(7)).toBe(10);
    expect(niceStep(13)).toBe(20);
    expect(niceStep(240)).toBe(250);
  });

  it('keeps a value that is already nice', () => {
    expect(niceStep(5)).toBe(5);
    expect(niceStep(10)).toBe(10);
  });
});

describe('severityLevel', () => {
  it('maps each severity onto the four-step meter', () => {
    expect(severityLevel('Low')).toBe(1);
    expect(severityLevel('Medium')).toBe(2);
    expect(severityLevel('High')).toBe(3);
    expect(severityLevel('Critical')).toBe(4);
    expect(severityLevel(null)).toBe(0);
  });
});
