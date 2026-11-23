# ADR-0001: Host Keycloak on Azure Container Apps

**Accepted:** 19 September 2026. **Owner:** Conor Woods.
**Lab configuration amended:** 21 September 2026.

## Decision

Run the official Keycloak container on Azure Container Apps, with PostgreSQL
Flexible Server for persistent identity state. Pin the image version/digest and
use production mode. Treat containers as replaceable; keep database recovery
independent of container replacement.

Azure Front Door provides public HTTPS ingress to Keycloak and the ASP.NET Core
portal/API on App Service. Keycloak authenticates administrators; the portal
owns physical-access policies, stored separately from Keycloak's schema.

Container Apps avoids operating a VM and provides a managed container lifecycle
without introducing Kubernetes. App Service for Containers was also viable.
AKS would add unnecessary operational work for the current workload. This is a
fit-for-purpose choice, not a claim of lowest cost or high availability.

## Lab configuration and trade-offs

The lab uses Front Door Standard with optional custom-rule WAF, one 0.5 CPU /
1 GiB Consumption replica, and B1ms PostgreSQL with 32 GiB storage. The Keycloak
origin permits Front Door backend network ranges but does not enforce this
profile's Front Door ID. Another Front Door profile can therefore reach that
origin; stronger profile isolation remains outstanding.

Key Vault and managed identities reduce direct credential handling but do not
remove the need to protect database and client secrets. Administrative access,
MFA, proxy trust, TLS, backups, image updates and restore testing remain project
responsibilities. Database migrations mean reverting a container image alone
is not a sufficient upgrade rollback.

Keycloak and the portal are deployed. The durable procedures, configuration and
remaining limitations are in the [Keycloak runbook](../../../infrastructure/keycloak/README.md),
[portal hosting guide](../../../infrastructure/managementportal/README.md) and
[roadmap](../../roadmap.md).

## References

- [Keycloak containers](https://www.keycloak.org/server/containers)
- [Keycloak reverse proxy configuration](https://www.keycloak.org/server/reverseproxy)
- [Azure Front Door origin security](https://learn.microsoft.com/en-us/azure/frontdoor/origin-security)
