# Security configuration

## Diagnostic endpoint

`/api/diag/di-trace` exposes service-resolution diagnostics and is deliberately restricted:

- In `SSO` authentication mode, only members of `Authentication:AdminGroup` can call it.
- In `SingleUser` mode, it is available only over a loopback connection. Remote requests receive `404 Not Found`.

## API keys

LLM provider API keys entered through the settings UI are encrypted before they are written to LiteDB. Existing plaintext keys are encrypted automatically the next time their provider configuration is read. Runtime services receive the decrypted value; API responses never return it.

Encryption uses the ASP.NET Core Data Protection key ring with the application name `ChangeDetection`. Container and multi-instance deployments should mount a persistent, access-controlled directory and set:

```text
Security__DataProtectionKeyRingPath=/run/secrets/changedetection-keyring
```

The key-ring directory must be backed up separately from the database. Losing it makes encrypted provider keys unrecoverable.

Search-provider API keys are not stored in LiteDB. Supply them through an external configuration source such as environment variables:

```text
SearchSettings__GoogleCseApiKey=...
SearchSettings__BraveApiKey=...
SearchSettings__NewsDataApiKey=...
```

LLM providers declared through configuration can likewise source their key from the environment, for example `LlmProviders__0__ApiKey`.
