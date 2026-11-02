# 6. Security, governance and access control

## 6.1 Threat model (summary)

| Threat | Main controls |
|---|---|
| User reads knowledge they could not read at the source | ACL mirroring, filters inside the query, re-check before generation, leak test suite |
| Data exfiltration to external AI or the web | enrichment-only egress, network policy, redaction, classification rules, budgets, audit |
| Prompt injection through ingested content | data/instruction separation, injection scanning, no write tools, citation verification |
| Poisoned knowledge (malicious or wrong documents) | trust tiers, authoritative sources, steward approval, provenance, feedback |
| Stolen connector credentials | OpenBao, short-lived dynamic credentials, least-privilege service accounts, rotation |
| Token theft from VS Code | OIDC PKCE, short access tokens, refresh in VS Code SecretStorage, device binding optional |
| Insider misuse | RBAC and ABAC, query audit, anomaly alerts on volume or sensitive domains |
| Supply chain | pinned versions, lock files, image signing (cosign), SBOMs, vulnerability scans in CI |

## 6.2 Identity

- **Keycloak** is the single OIDC issuer for the portal, the VS Code extension (Authorization Code + PKCE through `vscode.authentication`) and service-to-service calls (client credentials).
- It federates **Microsoft Entra ID** (OIDC), on-prem AD/LDAP, or other SAML/OIDC IdPs. Group claims come from Entra/AD; SCIM or scheduled sync keeps groups current.
- The laptop profile ships a dev realm with sample users, roles and groups; no Entra is needed.

## 6.3 Authorization model

**RBAC (coarse, platform roles):** `platform-admin`, `tenant-admin`, `knowledge-steward` (per domain), `contributor` (can register sources in their team), `reader`, `developer` (IDE features), `auditor` (read-only audit).

**ReBAC (fine, OpenFGA):** relationships mirror the source systems and organisation.

```text
model
  schema 1.1
type user
type group
  relations
    define member: [user, group#member]
type team
  relations
    define member: [user, group#member]
    define admin: [user]
type tenant
  relations
    define member: [user, group#member, team#member]
type domain
  relations
    define tenant: [tenant]
    define steward: [user, group#member]
    define reader: [user, group#member, team#member] or steward
type source
  relations
    define owner_team: [team]
    define domain: [domain]
    define reader: [user, group#member, team#member, tenant#member]
    define can_read: reader and member from tenant_of
    define tenant_of: [tenant]
    define admin: admin from owner_team
type document
  relations
    define source: [source]
    # Item-level grants carry the document's classification; clearance comes from the request context.
    define viewer: [user with clearance_ok, group#member with clearance_ok]
    define can_read: viewer or can_read from source
type repository
  relations
    define source: [source]
    define reader: [user, group#member, team#member] or reader from source
    define can_read: reader
condition clearance_ok(user_clearance: int, doc_classification: int) {
  user_clearance >= doc_classification
}
```

The full model, tests (`fga model test`) and examples live in `deploy/openfga/` (Phase 3). The snippet above shows the shape only.

**ABAC conditions:** classification vs. clearance, tenant, team, and optionally location or device posture for `Restricted` content.

**Inheritance from sources:** connectors capture native permissions (SharePoint site/library/item ACLs, Confluence space and page restrictions, Jira project roles and issue security, Git repository team access) and write OpenFGA tuples plus `acl_principals` on Qdrant points. A document is only searchable after its ACL is captured (fail closed).

**Enforcement points:**

1. API endpoint authorization (RBAC policy per route).
2. Retrieval filter (Qdrant payload filter + SQL predicates) built from OpenFGA `ListObjects` and user principals.
3. Final `Check` on documents in the context.
4. Graph traversal prunes nodes whose provenance the user cannot read.
5. The VS Code extension only sends files from the open workspace that the user selects or that the policy allows (`.ekcignore` respected).

## 6.4 Isolation

- **Tenant:** `tenant_id` on every row. PostgreSQL Row-Level Security policies use `current_setting('ekc.tenant_id')`, set per connection by the API. Qdrant filters use the tenant payload index (option: one collection per tenant for strong isolation).
- **Team and domain:** OpenFGA relations; stewards are scoped per domain.
- **User data:** conversations are private to the user unless shared. Admins see metadata, not content, unless an audited break-glass role is used.

## 6.5 Secrets and encryption

- **OpenBao** holds connector tokens, DB credentials (dynamic PostgreSQL roles), external LLM keys and signing keys. Services authenticate with Kubernetes auth or AppRole. The laptop profile uses Docker secrets from `.env` with the same interface.
- **In transit:** TLS at the edge; mTLS between services in Kubernetes (service mesh optional, or cert-manager-issued certificates); PostgreSQL and Qdrant with TLS on the server profile.
- **At rest:** encrypted volumes (LUKS, cloud disk encryption); SeaweedFS server-side encryption; sensitive columns (connector config secrets refs, enrichment payloads) encrypted with OpenBao transit; backups encrypted (restic/age).
- Nothing secret in Git, images or logs; secret scanning in CI.

## 6.6 Audit and governance

- Append-only `audit_events` (hash-chained) for: sign-in, source changes, ingestion runs, ACL sync deltas, queries (question hash, retrieved doc ids, model, tier mix), enrichment requests, outbound calls, approvals, policy and model changes, admin actions.
- Exported to Loki and optionally to a SIEM (syslog/OTLP).
- **Retention:** configurable per tenant. Question text can be stored hashed only, if required.
- Governance dashboards: external call volume and spend, content by tier and freshness, approval backlog, ACL drift.
