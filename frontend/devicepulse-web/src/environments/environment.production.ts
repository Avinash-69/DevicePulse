/**
 * Production configuration.
 *
 * The API is assumed to be served under the same origin as the SPA (behind a reverse proxy),
 * which removes the need for CORS in production entirely. Point this at an absolute URL if the
 * two are deployed separately — and add that origin to Security:AllowedCorsOrigins on the API.
 */
export const environment = {
  production: true,
  apiBaseUrl: '/api/v1',
  dashboardRefreshMs: 30_000,
};
