/**
 * Development configuration. The API base URL is the only thing that differs between
 * environments; no secret ever appears in a frontend build, because anything shipped to a
 * browser is public by definition (Appendix B of the master reference).
 */
export const environment = {
  production: false,
  apiBaseUrl: 'http://localhost:5082/api/v1',
  /** How often the dashboard re-fetches. Polling, deliberately, until SignalR arrives (§31). */
  dashboardRefreshMs: 15_000,
};
