# DevicePulse client

Angular 19 SPA for the DevicePulse API. Standalone components, signals, lazily-loaded routes.

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
- **Angular 19, not 20**, because the installed Node runtime is 20.11 and Angular 20 requires
  ≥ 20.19. Upgrading Node lifts this.
- **Tests run in headless Chrome with `--no-sandbox`** via the launcher in `karma.conf.js`,
  which CI containers need. Set `CHROME_BIN` if Chrome is not discovered automatically.
