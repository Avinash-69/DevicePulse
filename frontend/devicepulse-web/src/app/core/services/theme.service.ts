import { Injectable, computed, signal } from '@angular/core';

type Theme = 'light' | 'dark';

const STORAGE_KEY = 'devicepulse.theme';

/**
 * Light/dark theme, applied by setting data-theme on the document root — which is what the
 * custom properties in styles.scss key off.
 *
 * Defaults to the operating system preference rather than forcing light, and only remembers a
 * choice once the user has actually made one. That way someone on a dark desktop gets a dark
 * app on first visit, and someone who deliberately picked light keeps it.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly current = signal<Theme>(this.resolveInitial());

  readonly theme = this.current.asReadonly();
  readonly isDark = computed(() => this.current() === 'dark');

  constructor() {
    this.apply(this.current());

    // Follow the system if the user has not chosen explicitly.
    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', (event) => {
      if (localStorage.getItem(STORAGE_KEY)) {
        return;
      }

      this.current.set(event.matches ? 'dark' : 'light');
      this.apply(this.current());
    });
  }

  toggle(): void {
    this.set(this.current() === 'dark' ? 'light' : 'dark');
  }

  set(theme: Theme): void {
    this.current.set(theme);
    this.apply(theme);

    try {
      localStorage.setItem(STORAGE_KEY, theme);
    } catch {
      // Private browsing can refuse writes. The theme still applies for this session; it just
      // will not be remembered, which is not worth surfacing to the user.
    }
  }

  private apply(theme: Theme): void {
    document.documentElement.setAttribute('data-theme', theme);
  }

  private resolveInitial(): Theme {
    try {
      const stored = localStorage.getItem(STORAGE_KEY);

      if (stored === 'light' || stored === 'dark') {
        return stored;
      }
    } catch {
      // Fall through to the system preference.
    }

    return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }
}
