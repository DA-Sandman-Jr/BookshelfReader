# Backlog

## Planned work

- Migrate consuming applications and deployments to a current .NET 10 servicing release before adopting BookshelfReader 4.0.0. Configure production API keys explicitly; the reference host now requires them by default. Consumer applications and deployment environments are outside this repository.
- Before publishing 4.0.0, run representative bookshelf photos against each configured provider with authorized live credentials. The automated suite uses offline HTTP handlers and cannot establish real model availability, recognition quality, or account access. Keep model overrides current with provider retirement schedules.

## Known defects

No additional confirmed defects are recorded from the October 22, 2026 maintenance pass. Windows-only image preprocessing is an intentional package limitation, documented in the integration guide.
