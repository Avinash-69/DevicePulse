import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

/**
 * Root component. Deliberately nothing more than an outlet: the authenticated chrome lives in
 * ShellComponent, which is itself a routed component, so the login page can render without it.
 */
@Component({
  selector: 'dp-root',
  standalone: true,
  imports: [RouterOutlet],
  template: '<router-outlet />',
})
export class AppComponent {}
