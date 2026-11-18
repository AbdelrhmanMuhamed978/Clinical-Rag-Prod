const firebaseConnectOrigins = [
  "https://identitytoolkit.googleapis.com",
  "https://securetoken.googleapis.com",
  "https://firebaseinstallations.googleapis.com",
  "https://www.googleapis.com",
] as const;

export type SecurityEnvironment = Record<string, string | undefined>;

function origin(value: string | undefined, fallback: string): string {
  try {
    return new URL(value || fallback).origin;
  } catch {
    return new URL(fallback).origin;
  }
}

export function contentSecurityPolicy(env: SecurityEnvironment = process.env): string {
  const apiOrigin = origin(env.NEXT_PUBLIC_API_BASE, "http://localhost:5100");
  const authDomain = env.NEXT_PUBLIC_FIREBASE_AUTH_DOMAIN || "demo.firebaseapp.com";
  const authOrigin = authDomain.startsWith("http")
    ? origin(authDomain, "https://demo.firebaseapp.com")
    : `https://${authDomain}`;
  const development = env.NODE_ENV === "development";
  const connect = ["'self'", apiOrigin, ...firebaseConnectOrigins, authOrigin];
  if (development) connect.push("ws://localhost:3000");
  return [
    "default-src 'self'",
    `script-src 'self' 'unsafe-inline'${development ? " 'unsafe-eval'" : ""}`,
    "style-src 'self' 'unsafe-inline'",
    "img-src 'self' data: blob:",
    "font-src 'self' data:",
    `connect-src ${connect.join(" ")}`,
    `frame-src ${authOrigin}`,
    "worker-src 'self' blob:",
    "object-src 'none'",
    "base-uri 'self'",
    "form-action 'self'",
    "frame-ancestors 'none'",
  ].join("; ");
}

export function securityHeaders(env: SecurityEnvironment = process.env) {
  return [
    { key: "X-Content-Type-Options", value: "nosniff" },
    { key: "Referrer-Policy", value: "strict-origin-when-cross-origin" },
    { key: "Content-Security-Policy", value: contentSecurityPolicy(env) },
  ];
}
