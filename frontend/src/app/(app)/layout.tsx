"use client";
import { useEffect, useState } from "react";
import { useAuth } from "@/providers/AuthProvider";
import { useRouter, usePathname } from "next/navigation";
import { signOut } from "firebase/auth";
import { auth } from "@/lib/firebase";
import { GuardedLink as Link, NavigationGuardProvider, useNavigationGuard } from "@/components/navigation-guard";
import { Button } from "@/components/ui/button";
import { ChevronLeft, ChevronRight } from "lucide-react";
import { motion, AnimatePresence, useReducedMotion } from "framer-motion";
import { FirstUseNotice } from "@/components/clinical-limitations";

const NAV = [
  { label: "Documents", suffix: "documents" },
  { label: "Workflow", suffix: "workflow" },
  { label: "Rules", suffix: "rules" },
  { label: "Tests", suffix: "tests" },
  { label: "Playground", suffix: "playground" },
  { label: "Settings", suffix: "settings" },
];

export default function AppLayout({ children }: { children: React.ReactNode }) {
  return <NavigationGuardProvider><AppShell>{children}</AppShell></NavigationGuardProvider>;
}

function AppShell({ children }: { children: React.ReactNode }) {
  const { user, loading, emailVerified } = useAuth();
  const router = useRouter();
  const pathname = usePathname();
  const [sidebarOpen, setSidebarOpen] = useState(true);
  const reducedMotion = useReducedMotion();
  const { confirmLeave } = useNavigationGuard();
  const [signOutError, setSignOutError] = useState("");
  useEffect(() => {
    if (!loading && !user) router.push("/login");
    if (!loading && user && !emailVerified) router.push("/verify-email");
  }, [loading, user, emailVerified, router]);
  if (loading || !user) return <div className="p-8">Loading…</div>;
  if (!emailVerified) return <div className="p-8">Please verify your email to continue…</div>;

  // extract workspaceId from path
  const m = pathname?.match(/\/w\/([^/]+)/);
  const ws = m?.[1];

  // Build breadcrumbs
  const breadcrumbs = [];
  if (ws) {
    breadcrumbs.push({ label: "Workspace", href: `/w/${ws}/documents` });
    const suffix = pathname?.match(/\/w\/[^/]+\/([^/]+)/)?.[1];
    if (suffix) {
      const navItem = NAV.find(n => n.suffix === suffix);
      if (navItem) breadcrumbs.push({ label: navItem.label, href: pathname });
    }
  }

  return (
    <div className="flex min-h-screen">
      <aside
        className={`border-r transition-all duration-300 ease-in-out overflow-hidden ${sidebarOpen ? "w-56" : "w-16"} p-4 space-y-2 bg-card/50 backdrop-blur-sm`}
      >
        <div className="flex items-center justify-between mb-4">
          <Link href="/workspaces" className={`font-bold transition-all ${sidebarOpen ? "block" : "hidden"}`}>Workspaces</Link>
           <Button variant="ghost" size="icon" aria-label={sidebarOpen ? "Collapse sidebar" : "Expand sidebar"} aria-expanded={sidebarOpen} onClick={() => setSidebarOpen(!sidebarOpen)} className="shrink-0">
            {sidebarOpen ? <ChevronLeft className="w-4 h-4" /> : <ChevronRight className="w-4 h-4" />}
          </Button>
        </div>
        {ws && NAV.map(n => (
          <Link key={n.suffix} href={`/w/${ws}/${n.suffix}`}
            className={`flex items-center gap-2 px-2 py-1 rounded transition-all duration-200 hover:bg-accent ${pathname?.endsWith(n.suffix) ? "bg-accent text-accent-foreground" : "text-muted-foreground hover:text-foreground"}`}>
            <span className={`truncate ${sidebarOpen ? "block" : "hidden"}`}>{n.label}</span>
          </Link>
        ))}
        <div className={`pt-4 text-xs text-muted-foreground truncate ${sidebarOpen ? "block" : "hidden"}`}>{user.email}</div>
         <Button variant="ghost" size="sm" onClick={async () => {
           if (!confirmLeave()) return;
           try { await signOut(auth); router.push("/login"); }
           catch { setSignOutError("Could not sign out. Try again."); }
         }} className={`w-full justify-start ${sidebarOpen ? "block" : "hidden"}`}>Sign out</Button>
         {signOutError && <p role="alert" className="text-sm text-destructive">{signOutError}</p>}
      </aside>
      <main className="flex-1 p-6">
        <FirstUseNotice />
        {/* Breadcrumbs */}
        {breadcrumbs.length > 0 && (
          <div className="flex items-center gap-2 text-sm text-muted-foreground mb-4">
            {breadcrumbs.map((bc, i) => (
              <span key={i} className="flex items-center gap-2">
                {i > 0 && <span>/</span>}
                <Link href={bc.href} className="hover:text-foreground transition-colors">{bc.label}</Link>
              </span>
            ))}
          </div>
        )}
        <AnimatePresence mode="wait">
          <motion.div
            key={pathname}
            initial={reducedMotion ? { opacity: 0 } : { opacity: 0, rotateX: 8, z: -50, scale: 0.95 }}
            animate={reducedMotion ? { opacity: 1 } : { opacity: 1, rotateX: 0, z: 0, scale: 1 }}
            exit={reducedMotion ? { opacity: 0 } : { opacity: 0, rotateX: -8, z: -50, scale: 0.95 }}
            transition={{ duration: reducedMotion ? 0.15 : 0.4, ease: [0.4, 0, 0.2, 1] }}
            style={reducedMotion ? {} : { transformStyle: "preserve-3d", perspective: 1200 }}
          >
            {children}
          </motion.div>
        </AnimatePresence>
      </main>
    </div>
  );
}
