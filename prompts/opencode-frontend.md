# OpenCode Frontend Build Prompt — Clinical RAG Platform

You are an expert frontend engineer. Build the Next.js 14 (App Router) + TypeScript + Tailwind CSS + shadcn/ui frontend for the Clinical RAG platform. Backend: ASP.NET Core API (base URL from env `NEXT_PUBLIC_API_BASE`, default `http://localhost:5100`). Auth: Firebase (email/password, email verification). RAG runs on Google Colab via the backend proxy.

## Non-negotiables
- TypeScript strict mode. Typed API client in `lib/api.ts` attaching Firebase ID token as `Authorization: Bearer <token>`.
- Dark + light theme via shadcn. Mobile responsive.
- Every clinical statement shows its citation (label, section, page, strength, draft badge) and the quote.
- Locked safety rules: verifier cannot be turned off; show "Safety-locked" badge on those controls.
- Config changes go draft → test → publish; only published config serves users. Never publish without a test run.

## Stack
- Next.js 14 App Router, TypeScript, Tailwind v4, shadcn/ui
- Firebase (email/password + email verification)
- @tanstack/react-query, react-hook-form + zod, react-dropzone, sonner, lucide-react
- PDF viewer: react-pdf

## Routes
```
app/(auth)/login, signup, verify-email
app/(app)/layout.tsx (auth guard + workspace switcher)
app/(app)/workspaces/page.tsx
app/(app)/w/[workspaceId]/documents/page.tsx
app/(app)/w/[workspaceId]/documents/[docId]/chunks/page.tsx
app/(app)/w/[workspaceId]/workflow/page.tsx
app/(app)/w/[workspaceId]/rules/page.tsx
app/(app)/w/[workspaceId]/tests/page.tsx
app/(app)/w/[workspaceId]/playground/page.tsx
app/(app)/w/[workspaceId]/settings/page.tsx
```

Build it now. Start with scaffold + auth + workspaces, then documents/chunks, then the rest.
