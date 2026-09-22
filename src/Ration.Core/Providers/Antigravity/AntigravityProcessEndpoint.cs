namespace Ration.Core.Providers.Antigravity;

/// <summary>
/// A listener port paired with the CSRF token owned by its language-server
/// process. The token stays in memory and is never part of diagnostics.
/// </summary>
public sealed record AntigravityProcessEndpoint(int Port, string? CsrfToken);
