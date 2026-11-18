import { describe, expect, it } from "vitest";
import nextConfig from "../next.config";
import { contentSecurityPolicy, securityHeaders } from "../src/lib/security-headers";

const env = {
  NEXT_PUBLIC_API_BASE: "https://api.example.org",
  NEXT_PUBLIC_FIREBASE_AUTH_DOMAIN: "example.firebaseapp.com",
  NODE_ENV: "production",
};

function headers(values = env) {
  return Object.fromEntries(securityHeaders(values).map(({ key, value }) => [key, value]));
}

describe("frontend security headers", () => {
  it("sets nosniff, referrer policy and a self/Firebase-only CSP", () => {
    const result = headers();
    expect(result["X-Content-Type-Options"]).toBe("nosniff");
    expect(result["Referrer-Policy"]).toBe("strict-origin-when-cross-origin");
    const csp = result["Content-Security-Policy"];
    expect(csp).toContain("default-src 'self'");
    expect(csp).toContain("script-src 'self' 'unsafe-inline'");
    expect(csp).toContain("connect-src 'self' https://api.example.org");
    expect(csp).toContain("https://identitytoolkit.googleapis.com");
    expect(csp).toContain("https://securetoken.googleapis.com");
    expect(csp).toContain("https://example.firebaseapp.com");
    expect(csp).toContain("frame-ancestors 'none'");
    expect(csp).not.toContain("*");
    expect(csp).not.toContain("unsafe-eval");
  });

  it("allows eval only in development", () => {
    expect(contentSecurityPolicy({ ...env, NODE_ENV: "development" })).toContain("'unsafe-eval'");
    expect(contentSecurityPolicy(env)).not.toContain("'unsafe-eval'");
  });

  it("applies the headers to every Next.js route", async () => {
    const rules = await nextConfig.headers!();
    expect(rules).toHaveLength(1);
    expect(rules[0].source).toBe("/:path*");
    expect(rules[0].headers.map((header) => header.key)).toEqual([
      "X-Content-Type-Options",
      "Referrer-Policy",
      "Content-Security-Policy",
    ]);
  });
});
