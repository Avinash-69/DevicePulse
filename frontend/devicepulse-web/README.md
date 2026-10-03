# DevicePulse client

Angular 22 SPA for the DevicePulse API. Standalone components, signals, lazily-loaded routes.

See the [root README](../../README.md) for what the system does. This file covers running and
extending the client.

---

## Running

```bash
npm install
npm start        # http://localhost:4200
```

Expects the API at `http://localhost:5082` (see `src/environments/environment.ts`) with
`http://localhost:4200` in its `Security:AllowedCorsOrigins`. The development `appsettings`
already has it.

```bash
npm run build -- --configuration production
npm test -- --watch=false
```

---

## Layout

```
src/app/
├── core/
│   ├── models/api.models.ts      TypeScript mirrors of the server DTOs
│   ├── services/                 API client, notifications, theme
│   ├── auth/                     session service, permission keys, route guards
│   └── interceptors/             bearer token + refresh-and-retry, error handling
├── shared/
│   ├── components/               UI primitives, inline-SVG charts
│   └── utils/                    relative/absolute time and duration pipes
├── layout/                       shell, navigation, toast host
└── features/                     one lazily-loaded folder per page
```

---

## Conventions

**Signals, not RxJS, for component state.** `signal` and `computed` hold page state; RxJS stays
where it belongs — the HTTP layer, and `debounceTime`/`distinctUntilChanged` on search inputs.

**Each component is a single file.** Template and styles are inline. For components of this
size, three files per component is more navigation than structure.

**Styling is custom properties, not a component library.** `src/styles.scss` defines every token
once and redefines them under `:root[data-theme='dark']`. A component referencing `var(--accent)`
or `var(--surface)` follows the theme with no extra work. There is no component framework
because this application needs tables, forms, badges, modals and three chart shapes, and a
framework plus its theming layer would cost more than it saves.

**Charts are inline SVG.** `shared/components/charts.component.ts` has a line chart with a
min/max band, horizontal bars, a donut and a battery meter. They scale with `viewBox`, so they
are responsive without measuring the DOM, and they inherit the theme tokens.

**Server validation is shown inline, next to the field.** The error interceptor toasts
everything except 401 (the auth interceptor is already refreshing and retrying) and 400 with
field errors — those belong on the input the user is about to correct. `fieldErrorsFrom(error)`
extracts them.

**Permission checks are for visibility only.** `auth.has(...)`, the `<dp-if-permitted>` wrapper
and the route guards decide what to render and where to navigate. They are a convenience: the
API re-checks every permission on every request, so a user who edits `localStorage` or the URL
reaches a page whose calls all return 403.

---

## Adding a page

1. Create `features/<area>/<name>.component.ts` as a standalone component.
2. Add a lazy route in `app.routes.ts` with `permissionGuard(Permissions.whatever)`.
3. Add a nav entry to the matching group in `layout/shell.component.ts`.
4. Add any new endpoint to `core/services/api.service.ts` and its types to
   `core/models/api.models.ts`.

Mirror any new permission key in `core/auth/permissions.ts` to match the backend catalog.

---

## Things to know before changing code here

- **`as` aliasing only works on the primary `@if`.** `@else if (x(); as y)` is a compile error;
  nest an `@if` inside the `@else`.
- **A union of two differently-typed `Observable`s is not callable.** Annotate the variable with
  the union of the payload types, as `reference-data.component.ts` does.
- **`RelativeTimePipe` is impure on purpose.** A pure pipe would evaluate once and then display
  "just now" forever — frozen while looking live.
- **Angular is on 22, and the floor is a security one rather than a preference.** The 19.2.x
  line ended at 19.2.25 with four open advisories and no patch, and on 20.x the build
  toolchain still carried a critical `piscina` RCE and a high `webpack-dev-middleware` path
  traversal. 22.2.1 is the first release that clears all of them, which is why CI can keep
  `npm audit --audit-level=high` strict instead of excluding dev dependencies. Requires Node
  22.22+, 24.15+ or 26+, and TypeScript 6.
- **Every component sets `ChangeDetectionStrategy.Eager`, and removing it would break the
  timestamps.** Angular 22 made `OnPush` the default, and the v22 migration added `Eager`
  everywhere to preserve the old behaviour. That is kept deliberately: `RelativeTimePipe` is
  impure and depends on being re-evaluated on every check, so under `OnPush` a view with no
  signal change would stop being checked and "4 minutes ago" would freeze in place. Moving to
  `OnPush` is a worthwhile optimisation for a signal-based app this size, but it has to be done
  together with converting that pipe to a timer-driven signal, and verified in a browser.
- **Tests run on Vitest in jsdom, not Karma in a browser.** `ng test` goes through
  `@angular/build:unit-test`. Karma was removed because it was deprecated in 2023 and pins
  `chokidar@3` -> `braces`, and no patched `braces` exists at any version. No browser or
  `CHROME_BIN` is needed. Jasmine-only matchers are therefore unavailable: use `toBe(true)`
  rather than `toBeTrue()`.
- **`npm audit` is gated in two tiers, and the reason is specific.** CI fails on
  `npm audit --omit=dev --audit-level=high`, which is 0 vulnerabilities and covers everything
  that reaches a browser. It reports, without failing, a full-tree audit that currently shows
  four high advisories in `braces`/`chokidar` reached only through `karma`. `karma` is still
  installed because `@angular/build` declares it an *optional peer* for its legacy builder, so
  npm pulls it in even though nothing here invokes it. There is no patched `braces` to upgrade
  to and no supported way to refuse an optional peer, so the honest options were a permanently
  red pipeline or a gate that distinguishes shipped code from dormant tooling. If npm ever
  gains a way to decline an optional peer, or `@angular/build` drops the karma builder, the
  advisory tier should go back to blocking.
